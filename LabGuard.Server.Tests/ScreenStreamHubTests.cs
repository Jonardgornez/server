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
        new(new ScreenViewAudit(app.Services.GetRequiredService<IServiceScopeFactory>(), NullLogger<ScreenViewAudit>.Instance), app.Time);

    private static DateTimeOffset Deadline(TestApp app) => app.Time.GetUtcNow().AddHours(1);

    private static async Task<(ScreenViewer Viewer, FakeWebSocket Browser)> OpenAsync(ScreenStreamHub hub, TestApp app, int pcId = 1)
    {
        var browser = new FakeWebSocket();
        var viewer = await hub.OpenAsync(pcId, ScreenProtocol.NotifiedMode, Deadline(app), 0, new ScreenSocket(browser, ShortTimeout));
        return (viewer, browser);
    }

    private static string Text(byte[] payload) => Encoding.UTF8.GetString(payload);

    private static IReadOnlyList<byte[]> FramesSentTo(FakeWebSocket socket) =>
        socket.Sent.Where(message => message.Type == WebSocketMessageType.Binary).Select(message => message.Payload).ToArray();

    [Fact]
    public async Task OpenAsync_TellsTheBrowserItIsWaiting()
    {
        using var app = new TestApp();
        var (_, browser) = await OpenAsync(NewHub(app), app);

        Assert.Equal("{\"state\":\"waiting\"}", Text(browser.Sent.Single().Payload));
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
        Assert.True(first.Lifetime.IsCancellationRequested);
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
    public async Task AttachPcAsync_ReplacingAPcSocket_ClosesTheOldOneWith4001()
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
        Assert.Null(secondPc.CloseSent);
        Assert.Equal("{\"state\":\"live\"}", Text(browser.Sent.Last().Payload));
    }

    [Fact]
    public async Task AttachPcAsync_ToAClosedViewer_IsRefused()
    {
        using var app = new TestApp();
        var hub = NewHub(app);
        var (viewer, _) = await OpenAsync(hub, app);
        await hub.CloseAsync(viewer, ScreenProtocol.SessionEndedCloseStatus, "ended");

        Assert.False(await hub.AttachPcAsync(viewer, new ScreenSocket(new FakeWebSocket(), ShortTimeout)));
    }

    [Fact]
    public async Task DetachPcAsync_TellsTheBrowserItIsWaitingAgain()
    {
        using var app = new TestApp();
        var hub = NewHub(app);
        var (viewer, browser) = await OpenAsync(hub, app);
        var pc = new ScreenSocket(new FakeWebSocket(), ShortTimeout);
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
        await hub.AttachPcAsync(viewer, new ScreenSocket(new FakeWebSocket(), ShortTimeout));

        await hub.RelayFrameAsync(viewer, LiveViewClient.Frame("one"));
        await hub.RelayFrameAsync(viewer, LiveViewClient.Frame("two"));
        await hub.RelayFrameAsync(viewer, LiveViewClient.Frame("three"));

        var frames = FramesSentTo(browser);
        Assert.Equal(2, frames.Count);
        Assert.Equal("one", Encoding.ASCII.GetString(frames[0], 3, 3));
        Assert.Equal("two", Encoding.ASCII.GetString(frames[1], 3, 3));
    }

    [Fact]
    public async Task RelayAckAsync_ReturnsACreditAndPassesTheAckToThePc()
    {
        using var app = new TestApp();
        var hub = NewHub(app);
        var (viewer, browser) = await OpenAsync(hub, app);
        var pcSocket = new FakeWebSocket();
        await hub.AttachPcAsync(viewer, new ScreenSocket(pcSocket, ShortTimeout));
        await hub.RelayFrameAsync(viewer, LiveViewClient.Frame("one"));
        await hub.RelayFrameAsync(viewer, LiveViewClient.Frame("two"));

        await hub.RelayAckAsync(viewer);
        await hub.RelayFrameAsync(viewer, LiveViewClient.Frame("three"));

        Assert.Equal("ack", Text(pcSocket.Sent.Single().Payload));
        var frames = FramesSentTo(browser);
        Assert.Equal(3, frames.Count);
        Assert.Equal("three", Encoding.ASCII.GetString(frames[2], 3, 5));
    }

    [Fact]
    public async Task RelayFrameAsync_ToAStalledBrowser_ClosesTheViewWith1008()
    {
        using var app = new TestApp();
        var hub = NewHub(app);
        var (viewer, browser) = await OpenAsync(hub, app);
        var pcSocket = new FakeWebSocket();
        await hub.AttachPcAsync(viewer, new ScreenSocket(pcSocket, ShortTimeout));
        browser.StallSends = true;

        await hub.RelayFrameAsync(viewer, LiveViewClient.Frame("stuck"));

        Assert.Equal(WebSocketCloseStatus.PolicyViolation, browser.CloseSent!.Value.Status);
        Assert.Equal(WebSocketCloseStatus.PolicyViolation, pcSocket.CloseSent!.Value.Status);
        Assert.Null(hub.CurrentFor(1));
        Assert.True(viewer.Lifetime.IsCancellationRequested);
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
        var scopes = app.Services.GetRequiredService<IServiceScopeFactory>();
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = new ScreenViewEvent { AdminUsername = "tester", PcName = "PC-01", Mode = "notified", StartedAtUtc = app.Time.GetUtcNow() };
        db.ScreenViewEvents.Add(row);
        await db.SaveChangesAsync();
        var browser = new FakeWebSocket();
        var viewer = await hub.OpenAsync(1, "notified", Deadline(app), row.Id, new ScreenSocket(browser, ShortTimeout));

        await hub.CloseAsync(viewer, WebSocketCloseStatus.NormalClosure, "The viewer closed.");

        var stored = await app.WithDbAsync(d => d.ScreenViewEvents.AsNoTracking().SingleAsync(e => e.Id == row.Id));
        Assert.NotNull(stored.EndedAtUtc);
        Assert.Equal("tester", stored.AdminUsername);
    }

    [Fact]
    public async Task CloseAllAsync_ClosesEveryViewerWithTheGivenCode()
    {
        using var app = new TestApp();
        var hub = NewHub(app);
        var (_, first) = await OpenAsync(hub, app, pcId: 1);
        var (_, second) = await OpenAsync(hub, app, pcId: 2);

        await hub.CloseAllAsync(ScreenProtocol.SessionEndedCloseStatus, ScreenProtocol.SessionEndedReason);

        Assert.Equal(ScreenProtocol.SessionEndedCloseStatus, first.CloseSent!.Value.Status);
        Assert.Equal(ScreenProtocol.SessionEndedCloseStatus, second.CloseSent!.Value.Status);
        Assert.Empty(hub.Snapshot());
    }

    [Fact]
    public async Task RelayStateAsync_SendsThePausedStateToTheBrowser()
    {
        using var app = new TestApp();
        var hub = NewHub(app);
        var (viewer, browser) = await OpenAsync(hub, app);

        await hub.RelayStateAsync(viewer, ScreenProtocol.PausedMessage);

        Assert.Equal("{\"state\":\"paused\"}", Text(browser.Sent.Last().Payload));
    }
}
