using System.Net.WebSockets;
using System.Text;
using LabGuard.Server.Data;
using LabGuard.Server.Screen;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace LabGuard.Server.Tests;

// Several browsers sharing one PC's stream: the frame fan-out and its credits, the stream's mode, control by one view at a time,
// and views leaving. With fake sockets and the fake clock. The single-view rules are in ScreenStreamHubTests.
public class ScreenStreamHubViewersTests
{
    private const string PcOn = "{\"t\":\"control\",\"on\":true}";
    private const string PcOff = "{\"t\":\"control\",\"on\":false}";
    private const string On = "{\"control\":\"on\"}";
    private const string Live = "{\"state\":\"live\"}";
    private const string Waiting = "{\"state\":\"waiting\"}";
    private const string Move = "{\"t\":\"move\",\"x\":1,\"y\":1}";
    private const string CursorText = "{\"cursor\":\"default\"}";

    private static readonly TimeSpan ShortTimeout = TimeSpan.FromMilliseconds(100);

    private sealed record OpenView(ScreenViewer Viewer, FakeWebSocket Browser);

    private static ScreenStreamHub NewHub(TestApp app) =>
        new(
            new ScreenViewAudit(app.Services.GetRequiredService<IServiceScopeFactory>(), NullLogger<ScreenViewAudit>.Instance),
            new ScreenControlAudit(app.Services.GetRequiredService<IServiceScopeFactory>(), NullLogger<ScreenControlAudit>.Instance),
            app.Time);

    private static async Task<OpenView> OpenAsync(
        ScreenStreamHub hub,
        TestApp app,
        string mode = ScreenProtocol.NotifiedMode,
        int adminId = 1,
        bool isPassive = false,
        int auditId = 0)
    {
        var browser = new FakeWebSocket();
        var result = await hub.OpenAsync(1, mode, app.Time.GetUtcNow().AddHours(1), auditId, new ScreenSocket(browser, ShortTimeout), hub.Epoch,
            new ScreenViewOwner(adminId, 1, $"admin-{adminId}"), "PC-01", isPassive);
        Assert.Equal(ScreenOpenStatus.Opened, result.Status);
        return new OpenView(result.Viewer!, browser);
    }

    private static async Task<(ScreenSocket Pc, FakeWebSocket Socket)> AttachAsync(ScreenStreamHub hub, ScreenViewer viewer)
    {
        var socket = new FakeWebSocket();
        var pc = new ScreenSocket(socket, ShortTimeout);
        Assert.True(await hub.AttachPcAsync(viewer.Stream, pc, viewer.Stream.ViewId));
        return (pc, socket);
    }

    private static IReadOnlyList<string> TextsOf(FakeWebSocket socket) =>
        socket.Sent.Where(message => message.Type == WebSocketMessageType.Text).Select(message => Encoding.UTF8.GetString(message.Payload)).ToArray();

    private static IReadOnlyList<string> FrameLabelsOf(FakeWebSocket socket) =>
        socket.Sent.Where(message => message.Type == WebSocketMessageType.Binary).Select(message => LiveViewClient.LabelOf(message.Payload, 2)).ToArray();

    private static int AcksSentTo(FakeWebSocket pc) => TextsOf(pc).Count(text => text == "ack");

    private static BrowserControlMessage Input(string json) =>
        ScreenControlMessages.Parse(WebSocketMessageType.Text, Encoding.UTF8.GetBytes(json));

    private static async Task<int> InsertAuditRowAsync(TestApp app, string mode)
    {
        var scopes = app.Services.GetRequiredService<IServiceScopeFactory>();
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = new ScreenViewEvent { AdminUsername = "tester", PcName = "PC-01", Mode = mode, StartedAtUtc = app.Time.GetUtcNow() };
        db.ScreenViewEvents.Add(row);
        await db.SaveChangesAsync();
        return row.Id;
    }

    private static Task<List<ScreenControlEvent>> ControlRowsAsync(TestApp app) =>
        app.WithDbAsync(db => db.ScreenControlEvents.AsNoTracking().OrderBy(e => e.Id).ToListAsync());

    private static Task<List<ScreenViewEvent>> ViewRowsAsync(TestApp app) =>
        app.WithDbAsync(db => db.ScreenViewEvents.AsNoTracking().OrderBy(e => e.Id).ToListAsync());

    // Frames

    [Fact]
    public async Task TwoViewers_BothReceiveEachFrame_AndThePcGetsOneAckPerFrame()
    {
        using var app = new TestApp();
        var hub = NewHub(app);
        var first = await OpenAsync(hub, app);
        var second = await OpenAsync(hub, app, adminId: 2);
        var (pc, pcSocket) = await AttachAsync(hub, first.Viewer);

        await hub.RelayFrameAsync(first.Viewer.Stream, pc, LiveViewClient.Frame("f1"));
        await hub.RelayAckAsync(first.Viewer);
        await hub.RelayAckAsync(second.Viewer);

        Assert.Equal(new[] { "f1" }, FrameLabelsOf(first.Browser));
        Assert.Equal(new[] { "f1" }, FrameLabelsOf(second.Browser));
        Assert.Equal(1, AcksSentTo(pcSocket));
    }

    [Fact]
    public async Task AViewerThatNeverAcks_SkipsFrames_AndThePcKeepsThePaceOfTheOther()
    {
        using var app = new TestApp();
        var hub = NewHub(app);
        var fast = await OpenAsync(hub, app);
        var slow = await OpenAsync(hub, app, adminId: 2);
        var (pc, pcSocket) = await AttachAsync(hub, fast.Viewer);

        for (var index = 1; index <= 6; index++)
        {
            await hub.RelayFrameAsync(fast.Viewer.Stream, pc, LiveViewClient.Frame($"f{index}"));
            await hub.RelayAckAsync(fast.Viewer);
        }

        Assert.Equal(new[] { "f1", "f2", "f3", "f4", "f5", "f6" }, FrameLabelsOf(fast.Browser));
        Assert.Equal(new[] { "f1", "f2" }, FrameLabelsOf(slow.Browser));
        Assert.Equal(6, AcksSentTo(pcSocket));
        Assert.Null(slow.Browser.CloseSent);
    }

    [Fact]
    public async Task AViewerThatAcksAgain_GetsFramesAgain()
    {
        using var app = new TestApp();
        var hub = NewHub(app);
        var fast = await OpenAsync(hub, app);
        var slow = await OpenAsync(hub, app, adminId: 2);
        var (pc, _) = await AttachAsync(hub, fast.Viewer);
        await hub.RelayFrameAsync(fast.Viewer.Stream, pc, LiveViewClient.Frame("f1"));
        await hub.RelayFrameAsync(fast.Viewer.Stream, pc, LiveViewClient.Frame("f2"));
        await hub.RelayAckAsync(fast.Viewer);
        await hub.RelayFrameAsync(fast.Viewer.Stream, pc, LiveViewClient.Frame("f3"));

        await hub.RelayAckAsync(slow.Viewer);
        await hub.RelayAckAsync(fast.Viewer);
        await hub.RelayFrameAsync(fast.Viewer.Stream, pc, LiveViewClient.Frame("f4"));

        Assert.Equal(new[] { "f1", "f2", "f4" }, FrameLabelsOf(slow.Browser));
        Assert.Equal(new[] { "f1", "f2", "f3", "f4" }, FrameLabelsOf(fast.Browser));
    }

    [Fact]
    public async Task AViewerThatStopsReading_IsClosedAlone_AndTheOthersKeepTheStream()
    {
        using var app = new TestApp();
        var hub = NewHub(app);
        var reading = await OpenAsync(hub, app);
        var stalled = await OpenAsync(hub, app, adminId: 2);
        var (pc, pcSocket) = await AttachAsync(hub, reading.Viewer);
        stalled.Browser.StallSends = true;

        await hub.RelayFrameAsync(reading.Viewer.Stream, pc, LiveViewClient.Frame("f1"));
        await hub.RelayAckAsync(reading.Viewer);
        await hub.RelayFrameAsync(reading.Viewer.Stream, pc, LiveViewClient.Frame("f2"));

        Assert.Equal(WebSocketCloseStatus.PolicyViolation, stalled.Browser.CloseSent!.Value.Status);
        Assert.Null(reading.Browser.CloseSent);
        Assert.Null(pcSocket.CloseSent);
        Assert.Equal(new[] { "f1", "f2" }, FrameLabelsOf(reading.Browser));
        Assert.Same(reading.Viewer, Assert.Single(hub.ViewersFor(1)));
    }

    [Fact]
    public async Task AViewerLeaving_PassesOnTheAcksForFramesOnlyItHeld()
    {
        using var app = new TestApp();
        var hub = NewHub(app);
        var stuck = await OpenAsync(hub, app);
        var leaving = await OpenAsync(hub, app, adminId: 2);
        var (pc, pcSocket) = await AttachAsync(hub, stuck.Viewer);
        await hub.RelayFrameAsync(stuck.Viewer.Stream, pc, LiveViewClient.Frame("f1"));
        await hub.RelayFrameAsync(stuck.Viewer.Stream, pc, LiveViewClient.Frame("f2"));
        await hub.RelayAckAsync(leaving.Viewer);
        await hub.RelayAckAsync(leaving.Viewer);
        await hub.RelayFrameAsync(stuck.Viewer.Stream, pc, LiveViewClient.Frame("f3"));
        await hub.RelayFrameAsync(stuck.Viewer.Stream, pc, LiveViewClient.Frame("f4"));
        Assert.Equal(2, AcksSentTo(pcSocket));

        await hub.CloseAsync(leaving.Viewer, WebSocketCloseStatus.NormalClosure, "The viewer closed.");

        Assert.Equal(4, AcksSentTo(pcSocket));
        Assert.Null(pcSocket.CloseSent);
    }

    [Fact]
    public async Task AViewerJoiningALiveStream_IsToldItIsLive()
    {
        using var app = new TestApp();
        var hub = NewHub(app);
        var first = await OpenAsync(hub, app);
        await AttachAsync(hub, first.Viewer);

        var second = await OpenAsync(hub, app, adminId: 2);

        Assert.Equal(new[] { Waiting, Live }, TextsOf(second.Browser));
    }

    [Fact]
    public async Task APcLeaving_TellsEveryViewerItIsWaiting()
    {
        using var app = new TestApp();
        var hub = NewHub(app);
        var first = await OpenAsync(hub, app);
        var second = await OpenAsync(hub, app, adminId: 2);
        var (pc, _) = await AttachAsync(hub, first.Viewer);

        await hub.DetachPcAsync(first.Viewer.Stream, pc);

        Assert.Equal(Waiting, TextsOf(first.Browser).Last());
        Assert.Equal(Waiting, TextsOf(second.Browser).Last());
    }

    // Leaving

    [Fact]
    public async Task OnlyTheLastViewerLeaving_ClosesThePcSocket()
    {
        using var app = new TestApp();
        var hub = NewHub(app);
        var first = await OpenAsync(hub, app);
        var second = await OpenAsync(hub, app, adminId: 2);
        var (_, pcSocket) = await AttachAsync(hub, first.Viewer);

        await hub.CloseAsync(first.Viewer, WebSocketCloseStatus.NormalClosure, "The viewer closed.");
        Assert.Null(pcSocket.CloseSent);
        Assert.NotNull(hub.StreamFor(1));

        await hub.CloseAsync(second.Viewer, ScreenProtocol.PcOfflineCloseStatus, "The PC is offline or was removed.");
        Assert.Equal(ScreenProtocol.PcOfflineCloseStatus, pcSocket.CloseSent!.Value.Status);
        Assert.Null(hub.StreamFor(1));
    }

    [Fact]
    public async Task CloseAllOfAdmin_ClosesOnlyThatAdminsViewOfTheSharedPc_AndTheOtherKeepsReceivingFrames()
    {
        using var app = new TestApp();
        var hub = NewHub(app);
        var mine = await OpenAsync(hub, app, adminId: 1);
        var other = await OpenAsync(hub, app, adminId: 2);
        var (pc, pcSocket) = await AttachAsync(hub, mine.Viewer);

        await hub.CloseAllOfAdminAsync(1, ScreenProtocol.SessionEndedCloseStatus, ScreenProtocol.SessionEndedReason);
        await hub.RelayFrameAsync(other.Viewer.Stream, pc, LiveViewClient.Frame("f1"));

        Assert.Equal(ScreenProtocol.SessionEndedCloseStatus, mine.Browser.CloseSent!.Value.Status);
        Assert.Null(other.Browser.CloseSent);
        Assert.Null(pcSocket.CloseSent);
        Assert.Same(other.Viewer, Assert.Single(hub.Snapshot()));
        Assert.Equal(new[] { "f1" }, FrameLabelsOf(other.Browser));
    }

    [Fact]
    public async Task EachViewer_EndsOnlyItsOwnAuditRow()
    {
        using var app = new TestApp();
        var hub = NewHub(app);
        var firstRow = await InsertAuditRowAsync(app, ScreenProtocol.NotifiedMode);
        var secondRow = await InsertAuditRowAsync(app, ScreenProtocol.AnonymousMode);
        var first = await OpenAsync(hub, app, auditId: firstRow);
        await OpenAsync(hub, app, mode: ScreenProtocol.AnonymousMode, adminId: 2, auditId: secondRow);

        await hub.CloseAsync(first.Viewer, WebSocketCloseStatus.NormalClosure, "The viewer closed.");

        var rows = await ViewRowsAsync(app);
        Assert.NotNull(rows.Single(row => row.Id == firstRow).EndedAtUtc);
        Assert.Null(rows.Single(row => row.Id == secondRow).EndedAtUtc);
    }

    [Fact]
    public async Task APassiveViewerPastItsDeadline_IsClosedOnTheNextFrame_AndTheOthersStillGetIt()
    {
        using var app = new TestApp();
        var hub = NewHub(app);
        var lasting = await OpenAsync(hub, app);
        var expiringBrowser = new FakeWebSocket();
        var expiring = (await hub.OpenAsync(1, ScreenProtocol.NotifiedMode, app.Time.GetUtcNow().AddMinutes(10), 0,
            new ScreenSocket(expiringBrowser, ShortTimeout), hub.Epoch, new ScreenViewOwner(2, 1, "other"), "PC-01", isPassive: true)).Viewer!;
        var (pc, _) = await AttachAsync(hub, lasting.Viewer);
        app.Time.Advance(TimeSpan.FromMinutes(11));

        await hub.RelayFrameAsync(lasting.Viewer.Stream, pc, LiveViewClient.Frame("f1"));

        Assert.Equal(ScreenProtocol.SessionEndedCloseStatus, expiringBrowser.CloseSent!.Value.Status);
        Assert.Empty(FrameLabelsOf(expiringBrowser));
        Assert.Equal(new[] { "f1" }, FrameLabelsOf(lasting.Browser));
        Assert.True(expiring.IsClosed);
    }

    // The cap

    [Fact]
    public async Task AFullView_WhenFull_TakesTheOldestPassiveViewersPlace_With4001()
    {
        using var app = new TestApp();
        var hub = NewHub(app);
        var full = await OpenAsync(hub, app);
        var oldestPassive = await OpenAsync(hub, app, adminId: 2, isPassive: true);
        var newerPassive = await OpenAsync(hub, app, adminId: 2, isPassive: true);
        var otherFull = await OpenAsync(hub, app, adminId: 3);

        var joining = await OpenAsync(hub, app, adminId: 4);

        Assert.Equal(ScreenProtocol.ReplacedCloseStatus, oldestPassive.Browser.CloseSent!.Value.Status);
        Assert.Null(newerPassive.Browser.CloseSent);
        Assert.Equal(new[] { full.Viewer, newerPassive.Viewer, otherFull.Viewer, joining.Viewer }, hub.ViewersFor(1));
    }

    [Fact]
    public async Task AFullView_WhenFullOfFullViews_IsBusy_AndClosesNobody()
    {
        using var app = new TestApp();
        var hub = NewHub(app);
        var views = new List<OpenView>();
        for (var index = 0; index < ScreenProtocol.MaxViewersPerPc; index++)
        {
            views.Add(await OpenAsync(hub, app, adminId: index + 1));
        }

        var result = await hub.OpenAsync(1, ScreenProtocol.NotifiedMode, app.Time.GetUtcNow().AddHours(1), 0,
            new ScreenSocket(new FakeWebSocket(), ShortTimeout), hub.Epoch, new ScreenViewOwner(9, 1, "late"), "PC-01", isPassive: false);

        Assert.Equal(ScreenOpenStatus.Busy, result.Status);
        Assert.All(views, view => Assert.Null(view.Browser.CloseSent));
    }

    [Fact]
    public async Task AFullView_NeverTakesThePlaceOfAPassiveViewerThatHoldsControl()
    {
        using var app = new TestApp();
        var hub = NewHub(app);
        var controlling = await OpenAsync(hub, app, adminId: 1, isPassive: true);
        var (pc, _) = await AttachAsync(hub, controlling.Viewer);
        await hub.RequestControlAsync(controlling.Viewer, isServerPc: false);
        await hub.ConfirmControlAsync(controlling.Viewer.Stream, pc);
        var passive = await OpenAsync(hub, app, adminId: 2, isPassive: true);
        await OpenAsync(hub, app, adminId: 3);
        await OpenAsync(hub, app, adminId: 4);

        await OpenAsync(hub, app, adminId: 5);

        Assert.Null(controlling.Browser.CloseSent);
        Assert.Equal(ScreenProtocol.ReplacedCloseStatus, passive.Browser.CloseSent!.Value.Status);
        Assert.Equal(ScreenControlState.On, controlling.Viewer.ControlState);
    }

    // The mode

    [Fact]
    public async Task TheStream_IsNotifiedWhileAnyViewerIsNotified()
    {
        using var app = new TestApp();
        var hub = NewHub(app);
        var notified = await OpenAsync(hub, app);
        var stream = notified.Viewer.Stream;
        var viewId = stream.ViewId;

        await OpenAsync(hub, app, mode: ScreenProtocol.AnonymousMode, adminId: 2);

        Assert.Equal(ScreenProtocol.NotifiedMode, stream.Mode);
        Assert.Equal(viewId, stream.ViewId);
    }

    [Fact]
    public async Task ANotifiedViewerJoiningAnAnonymousStream_MakesItNotified_AndLetsGoOfThePcWith4001()
    {
        using var app = new TestApp();
        var hub = NewHub(app);
        var anonymous = await OpenAsync(hub, app, mode: ScreenProtocol.AnonymousMode);
        var stream = anonymous.Viewer.Stream;
        var anonymousViewId = stream.ViewId;
        var (pc, pcSocket) = await AttachAsync(hub, anonymous.Viewer);

        var notified = await OpenAsync(hub, app, adminId: 2);
        await hub.RelayFrameAsync(stream, pc, LiveViewClient.Frame("f1"));

        Assert.Equal(ScreenProtocol.NotifiedMode, stream.Mode);
        Assert.NotEqual(anonymousViewId, stream.ViewId);
        Assert.False(stream.ViewIdMatches(anonymousViewId));
        Assert.Equal(ScreenProtocol.ReplacedCloseStatus, pcSocket.CloseSent!.Value.Status);
        Assert.True(pcSocket.Aborted);
        Assert.Null(stream.CurrentPc);
        Assert.Equal(Waiting, TextsOf(anonymous.Browser).Last());
        Assert.Empty(FrameLabelsOf(notified.Browser));
    }

    [Fact]
    public async Task TheLastNotifiedViewerLeaving_MakesTheStreamAnonymous_AndKeepsThePc()
    {
        using var app = new TestApp();
        var hub = NewHub(app);
        var notified = await OpenAsync(hub, app);
        var anonymous = await OpenAsync(hub, app, mode: ScreenProtocol.AnonymousMode, adminId: 2);
        var stream = notified.Viewer.Stream;
        var notifiedViewId = stream.ViewId;
        var (_, pcSocket) = await AttachAsync(hub, notified.Viewer);

        await hub.CloseAsync(notified.Viewer, WebSocketCloseStatus.NormalClosure, "The viewer closed.");

        Assert.Equal(ScreenProtocol.AnonymousMode, stream.Mode);
        Assert.NotEqual(notifiedViewId, stream.ViewId);
        Assert.Equal(new ScreenStreamView(stream.ViewId, ScreenProtocol.AnonymousMode), stream.Describe());
        Assert.Null(pcSocket.CloseSent);
        Assert.Null(anonymous.Browser.CloseSent);
    }

    // Control

    private static async Task<(ScreenStreamHub Hub, OpenView Controller, OpenView Watcher, ScreenSocket Pc, FakeWebSocket PcSocket)> ControlledAsync(TestApp app)
    {
        var hub = NewHub(app);
        var controller = await OpenAsync(hub, app, adminId: 1);
        var watcher = await OpenAsync(hub, app, adminId: 2);
        var (pc, pcSocket) = await AttachAsync(hub, controller.Viewer);
        await hub.RequestControlAsync(controller.Viewer, isServerPc: false);
        await hub.ConfirmControlAsync(controller.Viewer.Stream, pc);
        Assert.Equal(ScreenControlState.On, controller.Viewer.ControlState);
        return (hub, controller, watcher, pc, pcSocket);
    }

    [Fact]
    public async Task OnlyTheControllerHearsControl_AndTheOtherViewerSeesItAsOff()
    {
        using var app = new TestApp();
        var (_, controller, watcher, _, _) = await ControlledAsync(app);

        Assert.Contains(On, TextsOf(controller.Browser));
        Assert.DoesNotContain(TextsOf(watcher.Browser), text => text.Contains("\"control\"", StringComparison.Ordinal));
        Assert.Equal(ScreenControlState.Off, watcher.Viewer.ControlState);
    }

    [Fact]
    public async Task ASecondControlRequest_IsRefusedAsInUse_AndThePcIsNotAskedAgain()
    {
        using var app = new TestApp();
        var (hub, controller, watcher, _, pcSocket) = await ControlledAsync(app);

        await hub.RequestControlAsync(watcher.Viewer, isServerPc: false);

        Assert.Equal("{\"control\":\"off\",\"reason\":\"in-use\"}", TextsOf(watcher.Browser).Last());
        Assert.Equal(1, TextsOf(pcSocket).Count(text => text == PcOn));
        Assert.Equal(ScreenControlState.On, controller.Viewer.ControlState);
        Assert.Equal(ScreenControlState.Off, watcher.Viewer.ControlState);
    }

    [Fact]
    public async Task ARequestWhileAnotherViewerIsStillRequesting_IsRefusedAsInUse()
    {
        using var app = new TestApp();
        var hub = NewHub(app);
        var first = await OpenAsync(hub, app);
        var second = await OpenAsync(hub, app, adminId: 2);
        await AttachAsync(hub, first.Viewer);
        await hub.RequestControlAsync(first.Viewer, isServerPc: false);

        await hub.RequestControlAsync(second.Viewer, isServerPc: false);

        Assert.Equal("{\"control\":\"off\",\"reason\":\"in-use\"}", TextsOf(second.Browser).Last());
        Assert.Equal(ScreenControlState.Requesting, first.Viewer.ControlState);
    }

    [Fact]
    public async Task InputFromAViewerThatDoesNotHoldControl_IsNotRelayed()
    {
        using var app = new TestApp();
        var (hub, controller, watcher, _, pcSocket) = await ControlledAsync(app);

        await hub.RelayInputAsync(watcher.Viewer, Input(Move));
        Assert.DoesNotContain(Move, TextsOf(pcSocket));

        await hub.RelayInputAsync(controller.Viewer, Input(Move));
        Assert.Contains(Move, TextsOf(pcSocket));
    }

    [Fact]
    public async Task AStopFromAViewerThatDoesNotHoldControl_IsAnsweredStopped_AndLeavesTheControlOn()
    {
        using var app = new TestApp();
        var (hub, controller, watcher, _, pcSocket) = await ControlledAsync(app);

        await hub.StopControlAsync(watcher.Viewer);

        Assert.Equal("{\"control\":\"off\",\"reason\":\"stopped\"}", TextsOf(watcher.Browser).Last());
        Assert.DoesNotContain(PcOff, TextsOf(pcSocket));
        Assert.Equal(ScreenControlState.On, controller.Viewer.ControlState);
        Assert.DoesNotContain(TextsOf(controller.Browser), text => text.Contains("\"off\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheCursor_GoesOnlyToTheController()
    {
        using var app = new TestApp();
        var (hub, controller, watcher, pc, _) = await ControlledAsync(app);

        await hub.RelayCursorAsync(controller.Viewer.Stream, pc, Encoding.UTF8.GetBytes(CursorText));

        Assert.Equal(CursorText, TextsOf(controller.Browser).Last());
        Assert.DoesNotContain(CursorText, TextsOf(watcher.Browser));
    }

    [Fact]
    public async Task TheControllerLeaving_TellsThePcOff_EndsTheControlRow_AndLetsAnotherViewerAsk()
    {
        using var app = new TestApp();
        var (hub, controller, watcher, pc, pcSocket) = await ControlledAsync(app);

        await hub.CloseAsync(controller.Viewer, WebSocketCloseStatus.NormalClosure, "The viewer closed.");

        Assert.Equal(PcOff, TextsOf(pcSocket).Last());
        Assert.Null(pcSocket.CloseSent);
        Assert.NotNull(Assert.Single(await ControlRowsAsync(app)).EndedUtc);
        await hub.RequestControlAsync(watcher.Viewer, isServerPc: false);
        await hub.ConfirmControlAsync(watcher.Viewer.Stream, pc);
        Assert.Equal(ScreenControlState.On, watcher.Viewer.ControlState);
        Assert.Equal("admin-2", (await ControlRowsAsync(app)).Last().AdminUserName);
    }

    [Fact]
    public async Task ControlByAnAnonymousViewerOfANotifiedStream_IsRecordedAsNotified()
    {
        using var app = new TestApp();
        var hub = NewHub(app);
        await OpenAsync(hub, app, adminId: 1);
        var anonymous = await OpenAsync(hub, app, mode: ScreenProtocol.AnonymousMode, adminId: 2);
        var (pc, _) = await AttachAsync(hub, anonymous.Viewer);

        await hub.RequestControlAsync(anonymous.Viewer, isServerPc: false);
        await hub.ConfirmControlAsync(anonymous.Viewer.Stream, pc);

        var row = Assert.Single(await ControlRowsAsync(app));
        Assert.Equal(ScreenProtocol.NotifiedMode, row.Mode);
        Assert.Equal("admin-2", row.AdminUserName);
    }

    [Fact]
    public async Task APcLeavingWhileControlled_TellsOnlyTheControllerConnection_AndEveryViewerWaiting()
    {
        using var app = new TestApp();
        var (hub, controller, watcher, pc, _) = await ControlledAsync(app);

        await hub.DetachPcAsync(controller.Viewer.Stream, pc);

        Assert.Equal(new[] { "{\"control\":\"off\",\"reason\":\"connection\"}", Waiting }, TextsOf(controller.Browser).TakeLast(2));
        Assert.Equal(Waiting, TextsOf(watcher.Browser).Last());
        Assert.DoesNotContain(TextsOf(watcher.Browser), text => text.Contains("\"control\"", StringComparison.Ordinal));
    }

    // Nothing that streams without the notice reaches a notified viewer

    private const string StartAnonymous = "{\"t\":\"start\",\"mode\":\"anonymous\"}";
    private const string StartNotified = "{\"t\":\"start\",\"mode\":\"notified\"}";
    private const string Stop = "{\"t\":\"stop\"}";

    private static async Task<(SecureHelperConnection Helper, FakeWebSocket Socket)> AttachHelperAsync(ScreenStreamHub hub)
    {
        var socket = new FakeWebSocket();
        var helper = new SecureHelperConnection(1, "hash", new ScreenSocket(socket, ShortTimeout));
        await hub.AttachHelperAsync(helper);
        return (helper, socket);
    }

    [Fact]
    public async Task APcAttachingUnderTheOldViewId_AfterANotifiedViewerJoined_IsRefused_AndNoFrameReachesTheViewer()
    {
        using var app = new TestApp();
        var hub = NewHub(app);
        var anonymous = await OpenAsync(hub, app, mode: ScreenProtocol.AnonymousMode);
        var stream = anonymous.Viewer.Stream;
        var anonymousViewId = stream.ViewId;
        var notified = await OpenAsync(hub, app, adminId: 2);
        var pc = new ScreenSocket(new FakeWebSocket(), ShortTimeout);

        var attached = await hub.AttachPcAsync(stream, pc, anonymousViewId);
        await hub.RelayFrameAsync(stream, pc, LiveViewClient.Frame("f1"));

        Assert.False(attached);
        Assert.Null(stream.CurrentPc);
        Assert.Empty(FrameLabelsOf(notified.Browser));
        Assert.Empty(FrameLabelsOf(anonymous.Browser));
    }

    [Fact]
    public async Task ANotifiedViewerJoining_StopsTheHelperStreamingAnonymously_AndNoneOfItsFramesReachTheViewer()
    {
        using var app = new TestApp();
        var hub = NewHub(app);
        var anonymous = await OpenAsync(hub, app, mode: ScreenProtocol.AnonymousMode);
        var stream = anonymous.Viewer.Stream;
        var (pc, _) = await AttachAsync(hub, anonymous.Viewer);
        var (helper, helperSocket) = await AttachHelperAsync(hub);
        await hub.SetPcPausedAsync(stream, pc, isPaused: true);
        Assert.Equal(StartAnonymous, TextsOf(helperSocket).Last());

        var notified = await OpenAsync(hub, app, adminId: 2);
        await hub.RelayHelperFrameAsync(helper, LiveViewClient.Frame("h1"));

        Assert.Equal(Stop, TextsOf(helperSocket).Last());
        Assert.Empty(FrameLabelsOf(notified.Browser));
        Assert.False(stream.IsCurrentSource(helper.Socket));
        // Frames the helper had on the way are dropped, not taken as a fault.
        Assert.True(hub.IsHelperFrameAllowed(helper));
    }

    [Fact]
    public async Task ANotifiedViewerJoining_WhileTheHelperCarriesControl_TellsItOffBeforeTheStop_AndEndsTheControlRow()
    {
        using var app = new TestApp();
        var hub = NewHub(app);
        var controller = await OpenAsync(hub, app, mode: ScreenProtocol.AnonymousMode);
        var stream = controller.Viewer.Stream;
        var (pc, pcSocket) = await AttachAsync(hub, controller.Viewer);
        var (helper, helperSocket) = await AttachHelperAsync(hub);
        await hub.SetPcPausedAsync(stream, pc, isPaused: true);
        await hub.RequestControlAsync(controller.Viewer, isServerPc: false);
        await hub.ConfirmControlAsync(stream, helper.Socket);
        Assert.Equal(ScreenControlState.On, controller.Viewer.ControlState);

        await OpenAsync(hub, app, adminId: 2);

        Assert.Equal(new[] { PcOff, Stop }, TextsOf(helperSocket).TakeLast(2));
        Assert.Equal(PcOff, TextsOf(pcSocket).Last());
        Assert.Contains("{\"control\":\"off\",\"reason\":\"connection\"}", TextsOf(controller.Browser));
        Assert.Equal(ScreenControlState.Off, controller.Viewer.ControlState);
        Assert.NotNull(Assert.Single(await ControlRowsAsync(app)).EndedUtc);
    }

    [Fact]
    public async Task AnOldStreamsLateStop_DoesNotEndTheHelperRunOfTheNewStream()
    {
        using var app = new TestApp();
        var hub = NewHub(app);
        var old = await OpenAsync(hub, app, mode: ScreenProtocol.AnonymousMode);
        var oldStream = old.Viewer.Stream;
        var (oldPc, _) = await AttachAsync(hub, old.Viewer);
        var (helper, helperSocket) = await AttachHelperAsync(hub);
        await hub.SetPcPausedAsync(oldStream, oldPc, isPaused: true);
        Assert.Equal(StartAnonymous, TextsOf(helperSocket).Last());

        // The old stream's stop waits behind its input gate while a new stream starts the helper.
        await oldStream.InputGate.WaitAsync();
        var closing = hub.CloseAsync(old.Viewer, WebSocketCloseStatus.NormalClosure, "The viewer closed.");
        var fresh = await OpenAsync(hub, app, adminId: 2);
        var (newPc, _) = await AttachAsync(hub, fresh.Viewer);
        await hub.SetPcPausedAsync(fresh.Viewer.Stream, newPc, isPaused: true);
        oldStream.InputGate.Release();
        await closing;

        var texts = TextsOf(helperSocket);
        Assert.Equal(StartNotified, texts.Last());
        Assert.DoesNotContain(Stop, texts.SkipWhile(text => text != StartNotified));
        Assert.True(fresh.Viewer.Stream.IsCurrentSource(helper.Socket));
    }

    [Fact]
    public async Task HelperFrames_WhileItsStopWaitsBehindTheGate_AreAllowedButNotRelayed_AndRefusedOnceTheGraceAfterTheStopIsOver()
    {
        using var app = new TestApp();
        var hub = NewHub(app);
        var anonymous = await OpenAsync(hub, app, mode: ScreenProtocol.AnonymousMode);
        var stream = anonymous.Viewer.Stream;
        var (pc, _) = await AttachAsync(hub, anonymous.Viewer);
        var (helper, helperSocket) = await AttachHelperAsync(hub);
        await hub.SetPcPausedAsync(stream, pc, isPaused: true);

        await stream.InputGate.WaitAsync();
        var browser = new FakeWebSocket();
        var joining = hub.OpenAsync(1, ScreenProtocol.NotifiedMode, app.Time.GetUtcNow().AddHours(1), 0, new ScreenSocket(browser, ShortTimeout),
            hub.Epoch, new ScreenViewOwner(2, 1, "admin-2"), "PC-01", isPassive: false);
        app.Time.Advance(TimeSpan.FromSeconds(2));

        Assert.Equal(ScreenProtocol.NotifiedMode, stream.Mode);
        Assert.True(hub.IsHelperFrameAllowed(helper));
        await hub.RelayHelperFrameAsync(helper, LiveViewClient.Frame("h1"));
        Assert.DoesNotContain(browser.Sent, message => message.Type == WebSocketMessageType.Binary);
        Assert.DoesNotContain(Stop, TextsOf(helperSocket));

        stream.InputGate.Release();
        await joining;
        Assert.Equal(Stop, TextsOf(helperSocket).Last());
        Assert.True(hub.IsHelperFrameAllowed(helper));
        app.Time.Advance(SecureHelperProtocol.StopGrace);
        Assert.False(hub.IsHelperFrameAllowed(helper));
    }
}
