using System.Net.WebSockets;
using System.Text;
using LabGuard.Server.Data;
using LabGuard.Server.Screen;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace LabGuard.Server.Tests;

public class ScreenStreamHubTests
{
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromMilliseconds(100);

    private static ScreenStreamHub NewHub(TestApp app) =>
        new(
            new ScreenViewAudit(app.Services.GetRequiredService<IServiceScopeFactory>(), NullLogger<ScreenViewAudit>.Instance),
            new ScreenControlAudit(app.Services.GetRequiredService<IServiceScopeFactory>(), NullLogger<ScreenControlAudit>.Instance),
            app.Time);

    private static DateTimeOffset Deadline(TestApp app) => app.Time.GetUtcNow().AddHours(1);

    private static ScreenSocket NewPc() => new(new FakeWebSocket(), ShortTimeout);

    private static async Task<int> InsertAuditRowAsync(TestApp app)
    {
        var scopes = app.Services.GetRequiredService<IServiceScopeFactory>();
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = new ScreenViewEvent { AdminUsername = "tester", PcName = "PC-01", Mode = "notified", StartedAtUtc = app.Time.GetUtcNow() };
        db.ScreenViewEvents.Add(row);
        await db.SaveChangesAsync();
        return row.Id;
    }

    private static Task<ScreenViewEvent> StoredAuditAsync(TestApp app, int id) =>
        app.WithDbAsync(db => db.ScreenViewEvents.AsNoTracking().SingleAsync(e => e.Id == id));

    private static async Task<(ScreenViewer Viewer, FakeWebSocket Browser)> OpenAsync(ScreenStreamHub hub, TestApp app, int pcId = 1)
    {
        var browser = new FakeWebSocket();
        var viewer = await hub.OpenAsync(pcId, ScreenProtocol.NotifiedMode, Deadline(app), 0, new ScreenSocket(browser, ShortTimeout), hub.Epoch, "tester", "PC-01");
        return (viewer!, browser);
    }

    private static string Text(byte[] payload) => Encoding.UTF8.GetString(payload);

    private static IReadOnlyList<byte[]> FramesSentTo(FakeWebSocket socket) =>
        socket.Sent.Where(message => message.Type == WebSocketMessageType.Binary).Select(message => message.Payload).ToArray();

    private static IReadOnlyList<string> StatesSentTo(FakeWebSocket socket) =>
        socket.Sent.Where(message => message.Type == WebSocketMessageType.Text).Select(message => Text(message.Payload)).ToArray();

    [Fact]
    public async Task OpenAsync_TellsTheBrowserItIsWaiting_BeforeTheViewIsFound()
    {
        using var app = new TestApp();
        var hub = NewHub(app);

        var (_, browser) = await OpenAsync(hub, app);

        Assert.Equal("{\"state\":\"waiting\"}", Text(browser.Sent.Single().Payload));
        Assert.NotNull(hub.CurrentFor(1));
    }

    [Fact]
    public async Task OpenAsync_ANewViewerReplacesTheOldOneWith4001()
    {
        using var app = new TestApp();
        var hub = NewHub(app);
        var (first, firstBrowser) = await OpenAsync(hub, app);

        var (second, _) = await OpenAsync(hub, app);

        Assert.Equal(ScreenProtocol.ReplacedCloseStatus, firstBrowser.CloseSent!.Value.Status);
        Assert.Same(second, hub.CurrentFor(1));
        var linked = first.LinkLifetime(CancellationToken.None);
        Assert.True(linked is null || linked.IsCancellationRequested);
        linked?.Dispose();
    }

    [Fact]
    public async Task OpenAsync_ForAnotherPc_DoesNotReplaceTheFirst()
    {
        using var app = new TestApp();
        var hub = NewHub(app);
        var (first, firstBrowser) = await OpenAsync(hub, app, pcId: 1);

        await OpenAsync(hub, app, pcId: 2);

        Assert.Null(firstBrowser.CloseSent);
        Assert.Same(first, hub.CurrentFor(1));
    }

    [Fact]
    public async Task OpenAsync_UnderAnEpochThatMovedOn_IsRefused_AndEndsTheAuditRow()
    {
        using var app = new TestApp();
        var hub = NewHub(app);
        var epoch = hub.Epoch;
        await hub.CloseAllAsync(ScreenProtocol.SessionEndedCloseStatus, ScreenProtocol.SessionEndedReason);
        var auditId = await InsertAuditRowAsync(app);

        var viewer = await hub.OpenAsync(1, ScreenProtocol.NotifiedMode, Deadline(app), auditId, new ScreenSocket(new FakeWebSocket(), ShortTimeout), epoch, "tester", "PC-01");

        Assert.Null(viewer);
        Assert.Null(hub.CurrentFor(1));
        Assert.NotNull((await StoredAuditAsync(app, auditId)).EndedAtUtc);
    }

    [Fact]
    public async Task OpenAsync_WhenTheBrowserStallsBeforeTheWaitingState_EndsTheAuditRow_AndRegistersNothing()
    {
        using var app = new TestApp();
        var hub = NewHub(app);
        var auditId = await InsertAuditRowAsync(app);
        var browser = new FakeWebSocket { StallSends = true };

        var viewer = await hub.OpenAsync(1, ScreenProtocol.NotifiedMode, Deadline(app), auditId, new ScreenSocket(browser, ShortTimeout), hub.Epoch, "tester", "PC-01");

        Assert.Null(viewer);
        Assert.Null(hub.CurrentFor(1));
        Assert.NotNull((await StoredAuditAsync(app, auditId)).EndedAtUtc);
    }

    [Fact]
    public async Task AttachPcAsync_ReplacingAPcSocket_Closes4001AndAbortsTheOldOne()
    {
        using var app = new TestApp();
        var hub = NewHub(app);
        var (viewer, browser) = await OpenAsync(hub, app);
        var firstPc = new FakeWebSocket();
        var secondPc = new FakeWebSocket();
        await hub.AttachPcAsync(viewer, new ScreenSocket(firstPc, ShortTimeout));

        var attached = await hub.AttachPcAsync(viewer, new ScreenSocket(secondPc, ShortTimeout));

        Assert.True(attached);
        Assert.Equal(ScreenProtocol.ReplacedCloseStatus, firstPc.CloseSent!.Value.Status);
        Assert.True(firstPc.Aborted);
        Assert.False(secondPc.Aborted);
        Assert.Equal("{\"state\":\"live\"}", Text(browser.Sent.Last().Payload));
    }

    [Fact]
    public async Task AReplacedPc_CannotInjectFramesOrStates()
    {
        using var app = new TestApp();
        var hub = NewHub(app);
        var (viewer, browser) = await OpenAsync(hub, app);
        var replaced = NewPc();
        await hub.AttachPcAsync(viewer, replaced);
        await hub.AttachPcAsync(viewer, NewPc());

        await hub.RelayFrameAsync(viewer, replaced, LiveViewClient.Frame("stale"));
        await hub.RelayPcStateAsync(viewer, replaced, ScreenProtocol.PausedState);

        Assert.Empty(FramesSentTo(browser));
        Assert.DoesNotContain("{\"state\":\"paused\"}", StatesSentTo(browser));
    }

    [Fact]
    public async Task AttachPcAsync_ToAClosedViewer_IsRefused()
    {
        using var app = new TestApp();
        var hub = NewHub(app);
        var (viewer, _) = await OpenAsync(hub, app);
        await hub.CloseAsync(viewer, ScreenProtocol.SessionEndedCloseStatus, "ended");

        Assert.False(await hub.AttachPcAsync(viewer, NewPc()));
    }

    [Fact]
    public async Task DetachPcAsync_TellsTheBrowserItIsWaitingAgain()
    {
        using var app = new TestApp();
        var hub = NewHub(app);
        var (viewer, browser) = await OpenAsync(hub, app);
        var pc = NewPc();
        await hub.AttachPcAsync(viewer, pc);

        await hub.DetachPcAsync(viewer, pc);

        Assert.Equal("{\"state\":\"waiting\"}", Text(browser.Sent.Last().Payload));
        Assert.Null(viewer.CurrentPc);
    }

    [Fact]
    public async Task RelayFrameAsync_DropsFramesBeyondTheCreditWindow_AndNeverQueuesThem()
    {
        using var app = new TestApp();
        var hub = NewHub(app);
        var (viewer, browser) = await OpenAsync(hub, app);
        var pc = NewPc();
        await hub.AttachPcAsync(viewer, pc);

        await hub.RelayFrameAsync(viewer, pc, LiveViewClient.Frame("one"));
        await hub.RelayFrameAsync(viewer, pc, LiveViewClient.Frame("two"));
        await hub.RelayFrameAsync(viewer, pc, LiveViewClient.Frame("three"));

        var frames = FramesSentTo(browser);
        Assert.Equal(2, frames.Count);
        Assert.Equal("one", LiveViewClient.LabelOf(frames[0], 3));
        Assert.Equal("two", LiveViewClient.LabelOf(frames[1], 3));
    }

    [Fact]
    public async Task RelayAckAsync_ReturnsACreditAndPassesTheAckToThePc()
    {
        using var app = new TestApp();
        var hub = NewHub(app);
        var (viewer, browser) = await OpenAsync(hub, app);
        var pcSocket = new FakeWebSocket();
        var pc = new ScreenSocket(pcSocket, ShortTimeout);
        await hub.AttachPcAsync(viewer, pc);
        await hub.RelayFrameAsync(viewer, pc, LiveViewClient.Frame("one"));
        await hub.RelayFrameAsync(viewer, pc, LiveViewClient.Frame("two"));

        await hub.RelayAckAsync(viewer);
        await hub.RelayFrameAsync(viewer, pc, LiveViewClient.Frame("three"));

        Assert.Equal("ack", Text(pcSocket.Sent.Single().Payload));
        var frames = FramesSentTo(browser);
        Assert.Equal(3, frames.Count);
        Assert.Equal("three", LiveViewClient.LabelOf(frames[2], 5));
    }

    [Fact]
    public async Task RelayAckAsync_WithNoCreditOutstanding_IsNotPassedOnToThePc()
    {
        using var app = new TestApp();
        var hub = NewHub(app);
        var (viewer, _) = await OpenAsync(hub, app);
        var pcSocket = new FakeWebSocket();
        await hub.AttachPcAsync(viewer, new ScreenSocket(pcSocket, ShortTimeout));

        await hub.RelayAckAsync(viewer);
        await hub.RelayAckAsync(viewer);

        Assert.Empty(pcSocket.Sent);
    }

    [Fact]
    public async Task AttachPcAsync_StartsTheCreditWindowAgainForTheNewConnection()
    {
        using var app = new TestApp();
        var hub = NewHub(app);
        var (viewer, browser) = await OpenAsync(hub, app);
        var first = NewPc();
        await hub.AttachPcAsync(viewer, first);
        await hub.RelayFrameAsync(viewer, first, LiveViewClient.Frame("one"));
        await hub.RelayFrameAsync(viewer, first, LiveViewClient.Frame("two"));
        var second = NewPc();
        await hub.AttachPcAsync(viewer, second);

        await hub.RelayFrameAsync(viewer, second, LiveViewClient.Frame("three"));

        Assert.Equal("three", LiveViewClient.LabelOf(FramesSentTo(browser)[2], 5));
    }

    [Fact]
    public async Task RelayFrameAsync_ToAStalledBrowser_ClosesTheViewWith1008()
    {
        using var app = new TestApp();
        var hub = NewHub(app);
        var (viewer, browser) = await OpenAsync(hub, app);
        var pcSocket = new FakeWebSocket();
        var pc = new ScreenSocket(pcSocket, ShortTimeout);
        await hub.AttachPcAsync(viewer, pc);
        browser.StallSends = true;

        await hub.RelayFrameAsync(viewer, pc, LiveViewClient.Frame("stuck"));

        Assert.Equal(WebSocketCloseStatus.PolicyViolation, browser.CloseSent!.Value.Status);
        Assert.Equal(WebSocketCloseStatus.PolicyViolation, pcSocket.CloseSent!.Value.Status);
        Assert.Null(hub.CurrentFor(1));
    }

    [Fact]
    public async Task RelayPcStateAsync_SendsOnlyAChangedState()
    {
        using var app = new TestApp();
        var hub = NewHub(app);
        var (viewer, browser) = await OpenAsync(hub, app);
        var pc = NewPc();
        await hub.AttachPcAsync(viewer, pc);

        await hub.RelayPcStateAsync(viewer, pc, ScreenProtocol.PausedState);
        await hub.RelayPcStateAsync(viewer, pc, ScreenProtocol.PausedState);

        Assert.Equal(1, StatesSentTo(browser).Count(state => state == "{\"state\":\"paused\"}"));
    }

    [Fact]
    public async Task CloseAsync_ClosesBothSocketsWithTheSameCode_AndOnlyOnce()
    {
        using var app = new TestApp();
        var hub = NewHub(app);
        var (viewer, browser) = await OpenAsync(hub, app);
        var pcSocket = new FakeWebSocket();
        await hub.AttachPcAsync(viewer, new ScreenSocket(pcSocket, ShortTimeout));

        await hub.CloseAsync(viewer, ScreenProtocol.PcOfflineCloseStatus, "The PC is offline or was removed.");
        await hub.CloseAsync(viewer, ScreenProtocol.SessionEndedCloseStatus, "The administrator session ended.");

        Assert.Equal(ScreenProtocol.PcOfflineCloseStatus, browser.CloseSent!.Value.Status);
        Assert.Equal(ScreenProtocol.PcOfflineCloseStatus, pcSocket.CloseSent!.Value.Status);
        Assert.Equal("The PC is offline or was removed.", browser.CloseSent!.Value.Reason);
        Assert.Null(hub.CurrentFor(1));
    }

    [Fact]
    public async Task CloseAsync_RecordsTheEndOfTheViewInTheAudit()
    {
        using var app = new TestApp();
        var hub = NewHub(app);
        var rowId = await InsertAuditRowAsync(app);
        var browser = new FakeWebSocket();
        var viewer = await hub.OpenAsync(1, "notified", Deadline(app), rowId, new ScreenSocket(browser, ShortTimeout), hub.Epoch, "tester", "PC-01");

        await hub.CloseAsync(viewer!, WebSocketCloseStatus.NormalClosure, "The viewer closed.");

        var stored = await StoredAuditAsync(app, rowId);
        Assert.NotNull(stored.EndedAtUtc);
        Assert.Equal("tester", stored.AdminUsername);
    }

    [Fact]
    public async Task CloseAllAsync_ClosesEveryViewerAndMovesTheEpochOn()
    {
        using var app = new TestApp();
        var hub = NewHub(app);
        var (_, first) = await OpenAsync(hub, app, pcId: 1);
        var (_, second) = await OpenAsync(hub, app, pcId: 2);
        var before = hub.Epoch;

        await hub.CloseAllAsync(ScreenProtocol.SessionEndedCloseStatus, ScreenProtocol.SessionEndedReason);

        Assert.Equal(ScreenProtocol.SessionEndedCloseStatus, first.CloseSent!.Value.Status);
        Assert.Equal(ScreenProtocol.SessionEndedCloseStatus, second.CloseSent!.Value.Status);
        Assert.Empty(hub.Snapshot());
        Assert.NotEqual(before, hub.Epoch);
    }

    [Fact]
    public void TryReserveAttach_RefusesAPcThatReconnectsWithinTheCooldown_ButNotAnotherPc()
    {
        using var app = new TestApp();
        var hub = NewHub(app);
        var now = app.Time.GetUtcNow();

        Assert.True(hub.TryReserveAttach(1, now));
        Assert.False(hub.TryReserveAttach(1, now.AddMilliseconds(999)));
        Assert.True(hub.TryReserveAttach(2, now.AddMilliseconds(10)));
        Assert.True(hub.TryReserveAttach(1, now.AddSeconds(1)));
    }
}
