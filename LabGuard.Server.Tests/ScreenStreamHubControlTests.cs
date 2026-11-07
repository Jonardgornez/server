using System.Net.WebSockets;
using System.Text;
using LabGuard.Server.Data;
using LabGuard.Server.Screen;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace LabGuard.Server.Tests;

// The remote control rules of the hub, with fake sockets and the fake clock. The end-to-end wire is tested in ScreenControlEndpointTests.
public class ScreenStreamHubControlTests
{
    private const string Requesting = "{\"control\":\"requesting\"}";
    private const string On = "{\"control\":\"on\"}";
    private const string PcOn = "{\"t\":\"control\",\"on\":true}";
    private const string PcOff = "{\"t\":\"control\",\"on\":false}";
    private const string Live = "{\"state\":\"live\"}";

    private static readonly TimeSpan ShortTimeout = TimeSpan.FromMilliseconds(100);

    private static ScreenStreamHub NewHub(TestApp app) =>
        new(
            new ScreenViewAudit(app.Services.GetRequiredService<IServiceScopeFactory>(), NullLogger<ScreenViewAudit>.Instance),
            new ScreenControlAudit(app.Services.GetRequiredService<IServiceScopeFactory>(), NullLogger<ScreenControlAudit>.Instance),
            app.Time);

    private static DateTimeOffset Deadline(TestApp app) => app.Time.GetUtcNow().AddHours(1);

    private static ScreenSocket NewSocket(FakeWebSocket socket) => new(socket, ShortTimeout);

    private static string OffMessage(string reason) => $"{{\"control\":\"off\",\"reason\":\"{reason}\"}}";

    private static async Task<(ScreenViewer Viewer, FakeWebSocket Browser)> OpenAsync(
        ScreenStreamHub hub,
        TestApp app,
        string mode = ScreenProtocol.NotifiedMode,
        DateTimeOffset? deadline = null)
    {
        var browser = new FakeWebSocket();
        var viewer = await hub.OpenAsync(1, mode, deadline ?? Deadline(app), 0, NewSocket(browser), hub.Epoch, new ScreenViewOwner(1, 1, "tester"), "PC-01");
        return (viewer!, browser);
    }

    private static async Task<(ScreenSocket Pc, FakeWebSocket Socket)> AttachAsync(ScreenStreamHub hub, ScreenViewer viewer)
    {
        var socket = new FakeWebSocket();
        var pc = NewSocket(socket);
        await hub.AttachPcAsync(viewer, pc);
        return (pc, socket);
    }

    private static IReadOnlyList<string> TextsOf(FakeWebSocket socket) =>
        socket.Sent.Where(message => message.Type == WebSocketMessageType.Text).Select(message => Encoding.UTF8.GetString(message.Payload)).ToArray();

    private static BrowserControlMessage Input(string json) =>
        ScreenControlMessages.Parse(WebSocketMessageType.Text, Encoding.UTF8.GetBytes(json));

    private static async Task<List<ScreenControlEvent>> ControlRowsAsync(TestApp app) =>
        await app.WithDbAsync(db => db.ScreenControlEvents.AsNoTracking().OrderBy(e => e.Id).ToListAsync());

    // The timer that abandons a request runs on a thread-pool continuation, so a test waits for its message.
    private static async Task<bool> EventuallyAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(10);
        }

        return condition();
    }

    // Opens a view, attaches a PC, asks for control and has the PC confirm it.
    private static async Task<(ScreenStreamHub Hub, ScreenViewer Viewer, FakeWebSocket Browser, ScreenSocket Pc, FakeWebSocket PcSocket)> OnControlAsync(TestApp app)
    {
        var hub = NewHub(app);
        var (viewer, browser) = await OpenAsync(hub, app);
        var (pc, pcSocket) = await AttachAsync(hub, viewer);
        await hub.RequestControlAsync(viewer, isServerPc: false);
        await hub.ConfirmControlAsync(viewer, pc);
        return (hub, viewer, browser, pc, pcSocket);
    }

    // Refusals

    [Fact]
    public async Task RequestControl_OnAnAnonymousView_IsRefusedAsAnonymous()
    {
        using var app = new TestApp();
        var hub = NewHub(app);
        var (viewer, browser) = await OpenAsync(hub, app, mode: ScreenProtocol.AnonymousMode);
        var (_, pcSocket) = await AttachAsync(hub, viewer);

        await hub.RequestControlAsync(viewer, isServerPc: false);

        Assert.Equal(OffMessage("anonymous"), TextsOf(browser).Last());
        Assert.DoesNotContain(PcOn, TextsOf(pcSocket));
        Assert.Equal(ScreenControlState.Off, viewer.ControlState);
    }

    [Fact]
    public async Task RequestControl_ForTheServersOwnPc_IsRefusedAsServerPc()
    {
        using var app = new TestApp();
        var hub = NewHub(app);
        var (viewer, browser) = await OpenAsync(hub, app);
        var (_, pcSocket) = await AttachAsync(hub, viewer);

        await hub.RequestControlAsync(viewer, isServerPc: true);

        Assert.Equal(OffMessage("server-pc"), TextsOf(browser).Last());
        Assert.DoesNotContain(PcOn, TextsOf(pcSocket));
        Assert.Equal(ScreenControlState.Off, viewer.ControlState);
    }

    [Fact]
    public async Task RequestControl_WithNoPcAttached_IsRefusedAsWaiting()
    {
        using var app = new TestApp();
        var hub = NewHub(app);
        var (viewer, browser) = await OpenAsync(hub, app);

        await hub.RequestControlAsync(viewer, isServerPc: false);

        Assert.Equal(OffMessage("waiting"), TextsOf(browser).Last());
        Assert.Equal(ScreenControlState.Off, viewer.ControlState);
    }

    [Fact]
    public async Task RequestControl_AnAnonymousRefusal_TakesPrecedenceOverTheServersOwnPc()
    {
        using var app = new TestApp();
        var hub = NewHub(app);
        var (viewer, browser) = await OpenAsync(hub, app, mode: ScreenProtocol.AnonymousMode);

        await hub.RequestControlAsync(viewer, isServerPc: true);

        Assert.Equal(OffMessage("anonymous"), TextsOf(browser).Last());
    }

    // The request and its confirmation

    [Fact]
    public async Task RequestControl_AsksTheAttachedPc_AndTellsTheBrowserItIsRequesting()
    {
        using var app = new TestApp();
        var hub = NewHub(app);
        var (viewer, browser) = await OpenAsync(hub, app);
        var (_, pcSocket) = await AttachAsync(hub, viewer);

        await hub.RequestControlAsync(viewer, isServerPc: false);

        Assert.Equal(Requesting, TextsOf(browser).Last());
        Assert.Equal(PcOn, TextsOf(pcSocket).Single());
        Assert.Equal(ScreenControlState.Requesting, viewer.ControlState);
    }

    [Fact]
    public async Task RequestControl_WhileAlreadyRequestingOrOn_IsIgnored()
    {
        using var app = new TestApp();
        var hub = NewHub(app);
        var (viewer, browser) = await OpenAsync(hub, app);
        var (pc, pcSocket) = await AttachAsync(hub, viewer);
        await hub.RequestControlAsync(viewer, isServerPc: false);

        await hub.RequestControlAsync(viewer, isServerPc: false);
        await hub.ConfirmControlAsync(viewer, pc);
        await hub.RequestControlAsync(viewer, isServerPc: false);

        Assert.Single(TextsOf(pcSocket));
        Assert.Equal(1, TextsOf(browser).Count(text => text == Requesting));
        Assert.Equal(1, TextsOf(browser).Count(text => text == On));
    }

    [Fact]
    public async Task ConfirmControl_TurnsControlOn_RecordsAnAuditRow_AndTellsTheBrowser()
    {
        using var app = new TestApp();
        var hub = NewHub(app);
        var (viewer, browser) = await OpenAsync(hub, app);
        var (pc, _) = await AttachAsync(hub, viewer);
        await hub.RequestControlAsync(viewer, isServerPc: false);

        await hub.ConfirmControlAsync(viewer, pc);

        Assert.Equal(On, TextsOf(browser).Last());
        Assert.Equal(ScreenControlState.On, viewer.ControlState);
        var row = Assert.Single(await ControlRowsAsync(app));
        Assert.Equal("tester", row.AdminUserName);
        Assert.Equal("PC-01", row.PcName);
        Assert.Null(row.EndedUtc);
    }

    [Fact]
    public async Task ConfirmControl_WithoutARequest_IsIgnored()
    {
        using var app = new TestApp();
        var hub = NewHub(app);
        var (viewer, browser) = await OpenAsync(hub, app);
        var (pc, _) = await AttachAsync(hub, viewer);

        await hub.ConfirmControlAsync(viewer, pc);

        Assert.Equal(ScreenControlState.Off, viewer.ControlState);
        Assert.DoesNotContain(On, TextsOf(browser));
        Assert.Empty(await ControlRowsAsync(app));
    }

    [Fact]
    public async Task ConfirmControl_FromAPcThatWasNotAsked_IsIgnored()
    {
        using var app = new TestApp();
        var hub = NewHub(app);
        var (viewer, browser) = await OpenAsync(hub, app);
        await AttachAsync(hub, viewer);
        await hub.RequestControlAsync(viewer, isServerPc: false);
        var stranger = NewSocket(new FakeWebSocket());

        await hub.ConfirmControlAsync(viewer, stranger);

        Assert.Equal(ScreenControlState.Requesting, viewer.ControlState);
        Assert.DoesNotContain(On, TextsOf(browser));
        Assert.Empty(await ControlRowsAsync(app));
    }

    [Fact]
    public async Task ConfirmControl_FromAReplacedPc_IsIgnored()
    {
        using var app = new TestApp();
        var hub = NewHub(app);
        var (viewer, browser) = await OpenAsync(hub, app);
        var (replaced, _) = await AttachAsync(hub, viewer);
        await hub.RequestControlAsync(viewer, isServerPc: false);
        await AttachAsync(hub, viewer);

        await hub.ConfirmControlAsync(viewer, replaced);

        Assert.Equal(ScreenControlState.Off, viewer.ControlState);
        Assert.DoesNotContain(On, TextsOf(browser));
        Assert.Empty(await ControlRowsAsync(app));
    }

    [Fact]
    public async Task ConfirmControl_WhenTheAuditCannotBeWritten_EndsTheRequestAsUnsupported_AndKeepsThePcStream()
    {
        using var app = new TestApp();
        var hub = NewHub(app);
        var (viewer, browser) = await OpenAsync(hub, app);
        var (pc, pcSocket) = await AttachAsync(hub, viewer);
        await hub.RequestControlAsync(viewer, isServerPc: false);
        await app.WithDbAsync(db => db.Database.ExecuteSqlRawAsync("DROP TABLE \"ScreenControlEvents\""));

        await hub.ConfirmControlAsync(viewer, pc);

        Assert.Equal(ScreenControlState.Off, viewer.ControlState);
        Assert.Equal(OffMessage("unsupported"), TextsOf(browser).Last());
        Assert.Equal(PcOff, TextsOf(pcSocket).Last());
        Assert.DoesNotContain(On, TextsOf(browser));
        Assert.False(pcSocket.Aborted);
    }

    [Fact]
    public async Task RelayInput_ToAPcThatStopsReading_IsDroppedAndThePcIsAborted()
    {
        using var app = new TestApp();
        var (hub, viewer, _, _, pcSocket) = await OnControlAsync(app);
        pcSocket.StallSends = true;

        await hub.RelayInputAsync(viewer, Input("{\"t\":\"move\",\"x\":1,\"y\":1}"));

        Assert.True(pcSocket.Aborted);
    }

    [Fact]
    public async Task RequestControl_ToAPcThatStopsReading_AbortsThePc()
    {
        using var app = new TestApp();
        var hub = NewHub(app);
        var (viewer, _) = await OpenAsync(hub, app);
        var (_, pcSocket) = await AttachAsync(hub, viewer);
        pcSocket.StallSends = true;

        await hub.RequestControlAsync(viewer, isServerPc: false);

        Assert.True(pcSocket.Aborted);
    }

    [Fact]
    public async Task PrepareControlRequest_OnlyOncePerSecond_AndNotWhileControlIsInUse()
    {
        using var app = new TestApp();
        var hub = NewHub(app);
        var (viewer, _) = await OpenAsync(hub, app);
        await AttachAsync(hub, viewer);

        Assert.True(await hub.PrepareControlRequestAsync(viewer));
        Assert.False(await hub.PrepareControlRequestAsync(viewer));
        app.Time.Advance(TimeSpan.FromSeconds(1));
        Assert.True(await hub.PrepareControlRequestAsync(viewer));
        await hub.RequestControlAsync(viewer, isServerPc: false);
        app.Time.Advance(TimeSpan.FromSeconds(1));
        Assert.False(await hub.PrepareControlRequestAsync(viewer));
    }

    [Fact]
    public async Task PrepareControlRequest_PastTheDeadline_ClosesTheViewAndAsksNothing()
    {
        using var app = new TestApp();
        var hub = NewHub(app);
        var (viewer, browser) = await OpenAsync(hub, app, deadline: app.Time.GetUtcNow().AddMinutes(10));
        app.Time.Advance(TimeSpan.FromMinutes(11));

        Assert.False(await hub.PrepareControlRequestAsync(viewer));
        Assert.Equal(ScreenProtocol.SessionEndedCloseStatus, browser.CloseSent!.Value.Status);
    }

    // The timeout

    [Fact]
    public async Task ControlNotConfirmed_IsAbandonedAsUnsupported_AfterFiveSeconds_AndThePcIsToldOff()
    {
        using var app = new TestApp();
        var hub = NewHub(app);
        var (viewer, browser) = await OpenAsync(hub, app);
        var (_, pcSocket) = await AttachAsync(hub, viewer);
        await hub.RequestControlAsync(viewer, isServerPc: false);

        app.Time.Advance(TimeSpan.FromSeconds(4.9));
        Assert.Equal(ScreenControlState.Requesting, viewer.ControlState);
        app.Time.Advance(TimeSpan.FromSeconds(0.2));

        Assert.True(await EventuallyAsync(() => TextsOf(browser).Contains(OffMessage("unsupported"))));
        Assert.True(await EventuallyAsync(() => TextsOf(pcSocket).Contains(PcOff)));
        Assert.Equal(ScreenControlState.Off, viewer.ControlState);
        Assert.Empty(await ControlRowsAsync(app));
    }

    [Fact]
    public async Task ControlConfirmedBeforeTheTimeout_IsNotAbandonedLater()
    {
        using var app = new TestApp();
        var hub = NewHub(app);
        var (viewer, browser) = await OpenAsync(hub, app);
        var (pc, _) = await AttachAsync(hub, viewer);
        await hub.RequestControlAsync(viewer, isServerPc: false);
        await hub.ConfirmControlAsync(viewer, pc);

        app.Time.Advance(TimeSpan.FromSeconds(30));
        await Task.Delay(100);

        Assert.Equal(ScreenControlState.On, viewer.ControlState);
        Assert.DoesNotContain(OffMessage("unsupported"), TextsOf(browser));
    }

    [Fact]
    public async Task AFreshRequest_IsNotAbandonedByTheTimerOfAnEarlierOne()
    {
        using var app = new TestApp();
        var hub = NewHub(app);
        var (viewer, browser) = await OpenAsync(hub, app);
        var (pc, _) = await AttachAsync(hub, viewer);
        await hub.RequestControlAsync(viewer, isServerPc: false);
        await hub.StopControlAsync(viewer);
        app.Time.Advance(TimeSpan.FromSeconds(3));
        await hub.RequestControlAsync(viewer, isServerPc: false);

        // The first request's wait ends now, but the second one has two seconds left.
        app.Time.Advance(TimeSpan.FromSeconds(2.5));
        await Task.Delay(100);

        Assert.Equal(ScreenControlState.Requesting, viewer.ControlState);
        Assert.DoesNotContain(OffMessage("unsupported"), TextsOf(browser));
        await hub.ConfirmControlAsync(viewer, pc);
        Assert.Equal(ScreenControlState.On, viewer.ControlState);
    }

    // Input

    [Fact]
    public async Task Input_IsRelayedOnlyWhileOn_AndInCanonicalForm()
    {
        using var app = new TestApp();
        var hub = NewHub(app);
        var (viewer, _) = await OpenAsync(hub, app);
        var (pc, pcSocket) = await AttachAsync(hub, viewer);

        await hub.RelayInputAsync(viewer, Input("{\"t\":\"move\",\"x\":1,\"y\":2}"));
        await hub.RequestControlAsync(viewer, isServerPc: false);
        await hub.RelayInputAsync(viewer, Input("{\"t\":\"move\",\"x\":1,\"y\":2}"));
        await hub.ConfirmControlAsync(viewer, pc);
        await hub.RelayInputAsync(viewer, Input("{ \"y\": 20, \"x\": 10, \"t\": \"move\" }"));
        await hub.RelayInputAsync(viewer, Input("{\"t\":\"key\",\"c\":\"KeyA\",\"d\":true}"));

        Assert.Equal(
            new[] { PcOn, "{\"t\":\"move\",\"x\":10,\"y\":20}", "{\"t\":\"key\",\"c\":\"KeyA\",\"d\":true}" },
            TextsOf(pcSocket));
    }

    [Fact]
    public async Task Input_FromAStoppedControl_IsNoLongerRelayed()
    {
        using var app = new TestApp();
        var (hub, viewer, _, _, pcSocket) = await OnControlAsync(app);
        await hub.StopControlAsync(viewer);

        await hub.RelayInputAsync(viewer, Input("{\"t\":\"release\"}"));

        Assert.DoesNotContain("{\"t\":\"release\"}", TextsOf(pcSocket));
    }

    [Fact]
    public async Task Input_OverTheMoveCap_IsDropped_AndOtherInputHasItsOwnCap()
    {
        using var app = new TestApp();
        var (hub, viewer, _, _, pcSocket) = await OnControlAsync(app);

        for (var attempt = 0; attempt < ScreenProtocol.MaxMovesPerSecond + 10; attempt++)
        {
            await hub.RelayInputAsync(viewer, Input("{\"t\":\"move\",\"x\":1,\"y\":1}"));
        }

        await hub.RelayInputAsync(viewer, Input("{\"t\":\"down\",\"b\":0,\"x\":1,\"y\":1}"));
        await hub.RelayInputAsync(viewer, Input("{\"t\":\"up\",\"b\":0,\"x\":1,\"y\":1}"));

        var texts = TextsOf(pcSocket);
        Assert.Equal(ScreenProtocol.MaxMovesPerSecond, texts.Count(text => text.StartsWith("{\"t\":\"move\"", StringComparison.Ordinal)));
        Assert.Contains("{\"t\":\"up\",\"b\":0,\"x\":1,\"y\":1}", texts);
    }

    [Fact]
    public async Task Input_OverTheOtherCap_IsDropped()
    {
        using var app = new TestApp();
        var (hub, viewer, _, _, pcSocket) = await OnControlAsync(app);

        for (var attempt = 0; attempt < ScreenProtocol.MaxOtherInputPerSecond + 10; attempt++)
        {
            await hub.RelayInputAsync(viewer, Input("{\"t\":\"wheel\",\"dx\":0,\"dy\":120}"));
        }

        Assert.Equal(ScreenProtocol.MaxOtherInputPerSecond, TextsOf(pcSocket).Count(text => text.StartsWith("{\"t\":\"wheel\"", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task KeyUpAndUp_AreNotCappedByTheOtherCap_ButOnlyRelayedForWhatIsHeld()
    {
        using var app = new TestApp();
        var (hub, viewer, _, _, pcSocket) = await OnControlAsync(app);
        await hub.RelayInputAsync(viewer, Input("{\"t\":\"key\",\"c\":\"KeyA\",\"d\":true}"));
        await hub.RelayInputAsync(viewer, Input("{\"t\":\"down\",\"b\":0,\"x\":1,\"y\":1}"));

        for (var attempt = 0; attempt < ScreenProtocol.MaxOtherInputPerSecond + 10; attempt++)
        {
            await hub.RelayInputAsync(viewer, Input("{\"t\":\"wheel\",\"dx\":0,\"dy\":120}"));
        }

        await hub.RelayInputAsync(viewer, Input("{\"t\":\"key\",\"c\":\"KeyA\",\"d\":false}"));
        await hub.RelayInputAsync(viewer, Input("{\"t\":\"up\",\"b\":0,\"x\":1,\"y\":1}"));
        await hub.RelayInputAsync(viewer, Input("{\"t\":\"up\",\"b\":0,\"x\":1,\"y\":1}"));

        var texts = TextsOf(pcSocket);
        Assert.Contains("{\"t\":\"key\",\"c\":\"KeyA\",\"d\":false}", texts);
        Assert.Equal(1, texts.Count(text => text == "{\"t\":\"up\",\"b\":0,\"x\":1,\"y\":1}"));
    }

    [Fact]
    public async Task KeyDown_IsCappedWithTheOtherInput()
    {
        using var app = new TestApp();
        var (hub, viewer, _, _, pcSocket) = await OnControlAsync(app);

        for (var attempt = 0; attempt < ScreenProtocol.MaxOtherInputPerSecond + 10; attempt++)
        {
            await hub.RelayInputAsync(viewer, Input("{\"t\":\"key\",\"c\":\"KeyA\",\"d\":true}"));
        }

        Assert.Equal(ScreenProtocol.MaxOtherInputPerSecond, TextsOf(pcSocket).Count(text => text == "{\"t\":\"key\",\"c\":\"KeyA\",\"d\":true}"));
    }

    [Fact]
    public async Task Input_AfterTheSessionDeadline_ClosesTheViewAndIsNotRelayed()
    {
        using var app = new TestApp();
        var hub = NewHub(app);
        var (viewer, browser) = await OpenAsync(hub, app, deadline: app.Time.GetUtcNow().AddMinutes(10));
        var (pc, pcSocket) = await AttachAsync(hub, viewer);
        await hub.RequestControlAsync(viewer, isServerPc: false);
        await hub.ConfirmControlAsync(viewer, pc);

        app.Time.Advance(TimeSpan.FromMinutes(11));
        await hub.RelayInputAsync(viewer, Input("{\"t\":\"move\",\"x\":1,\"y\":1}"));

        Assert.Equal(ScreenProtocol.SessionEndedCloseStatus, browser.CloseSent!.Value.Status);
        Assert.DoesNotContain("{\"t\":\"move\",\"x\":1,\"y\":1}", TextsOf(pcSocket));
        Assert.Null(hub.CurrentFor(1));
    }

    [Fact]
    public async Task RequestControl_AfterTheSessionDeadline_ClosesTheView()
    {
        using var app = new TestApp();
        var hub = NewHub(app);
        var (viewer, browser) = await OpenAsync(hub, app, deadline: app.Time.GetUtcNow().AddMinutes(10));
        await AttachAsync(hub, viewer);

        app.Time.Advance(TimeSpan.FromMinutes(11));
        await hub.RequestControlAsync(viewer, isServerPc: false);

        Assert.Equal(ScreenProtocol.SessionEndedCloseStatus, browser.CloseSent!.Value.Status);
        Assert.Equal(ScreenControlState.Off, viewer.ControlState);
    }

    // Ending control

    [Fact]
    public async Task StopControl_TellsTheBrowserStopped_TellsThePcOff_AndEndsTheAuditRow()
    {
        using var app = new TestApp();
        var (hub, viewer, browser, _, pcSocket) = await OnControlAsync(app);

        await hub.StopControlAsync(viewer);

        Assert.Equal(OffMessage("stopped"), TextsOf(browser).Last());
        Assert.Equal(PcOff, TextsOf(pcSocket).Last());
        Assert.Equal(ScreenControlState.Off, viewer.ControlState);
        Assert.NotNull((await ControlRowsAsync(app)).Single().EndedUtc);
    }

    [Fact]
    public async Task PcEndedControl_TellsTheBrowserPcEnded_AndTellsThePcOff()
    {
        using var app = new TestApp();
        var (hub, viewer, browser, _, pcSocket) = await OnControlAsync(app);

        await hub.EndControlAsync(viewer, ControlReasons.PcEnded);

        Assert.Equal(OffMessage("pc-ended"), TextsOf(browser).Last());
        Assert.Equal(PcOff, TextsOf(pcSocket).Last());
        Assert.NotNull((await ControlRowsAsync(app)).Single().EndedUtc);
    }

    [Fact]
    public async Task StopControl_WhenNoControlIsInProgress_IsStillAnsweredStopped_AndThePcIsNotTold()
    {
        using var app = new TestApp();
        var hub = NewHub(app);
        var (viewer, browser) = await OpenAsync(hub, app);
        var (_, pcSocket) = await AttachAsync(hub, viewer);

        await hub.StopControlAsync(viewer);

        Assert.Equal(OffMessage("stopped"), TextsOf(browser).Last());
        Assert.Empty(TextsOf(pcSocket));
        Assert.Equal(ScreenControlState.Off, viewer.ControlState);
    }

    [Fact]
    public async Task StopControl_FromRequesting_IsAnsweredStopped_AndThePcIsToldOff()
    {
        using var app = new TestApp();
        var hub = NewHub(app);
        var (viewer, browser) = await OpenAsync(hub, app);
        var (_, pcSocket) = await AttachAsync(hub, viewer);
        await hub.RequestControlAsync(viewer, isServerPc: false);

        await hub.StopControlAsync(viewer);

        Assert.Equal(OffMessage("stopped"), TextsOf(browser).Last());
        Assert.Equal(PcOff, TextsOf(pcSocket).Last());
        Assert.Equal(ScreenControlState.Off, viewer.ControlState);
        Assert.Empty(await ControlRowsAsync(app));
    }

    [Fact]
    public async Task StopControl_TwiceAfterControlWasOn_IsAnsweredEachTime_ButThePcIsToldOnce()
    {
        using var app = new TestApp();
        var (hub, viewer, browser, _, pcSocket) = await OnControlAsync(app);

        await hub.StopControlAsync(viewer);
        await hub.StopControlAsync(viewer);

        Assert.Equal(2, TextsOf(browser).Count(text => text == OffMessage("stopped")));
        Assert.Equal(1, TextsOf(pcSocket).Count(text => text == PcOff));
    }

    [Fact]
    public async Task DetachWhileOn_TellsTheBrowserConnection_ThenWaiting_AndEndsTheAuditRow()
    {
        using var app = new TestApp();
        var hub = NewHub(app);
        var (viewer, browser) = await OpenAsync(hub, app);
        var (pc, pcSocket) = await AttachAsync(hub, viewer);
        await hub.RequestControlAsync(viewer, isServerPc: false);
        await hub.ConfirmControlAsync(viewer, pc);

        await hub.DetachPcAsync(viewer, pc);

        var texts = TextsOf(browser);
        Assert.Equal(new[] { OffMessage("connection"), "{\"state\":\"waiting\"}" }, texts.TakeLast(2));
        Assert.Equal(ScreenControlState.Off, viewer.ControlState);
        Assert.Equal(new[] { PcOn }, TextsOf(pcSocket));
        Assert.NotNull((await ControlRowsAsync(app)).Single().EndedUtc);
    }

    [Fact]
    public async Task ReplacedWhileOn_TellsTheBrowserConnection_AndNeitherPcIsTold()
    {
        using var app = new TestApp();
        var hub = NewHub(app);
        var (viewer, browser) = await OpenAsync(hub, app);
        var (pc, oldSocket) = await AttachAsync(hub, viewer);
        await hub.RequestControlAsync(viewer, isServerPc: false);
        await hub.ConfirmControlAsync(viewer, pc);
        var (_, newSocket) = await AttachAsync(hub, viewer);

        // The state is still live, so it is not sent again. The control end is the last thing the browser hears.
        Assert.Equal(OffMessage("connection"), TextsOf(browser).Last());
        Assert.DoesNotContain(PcOff, TextsOf(oldSocket));
        Assert.Empty(TextsOf(newSocket));
        Assert.Equal(ScreenControlState.Off, viewer.ControlState);
        Assert.NotNull((await ControlRowsAsync(app)).Single().EndedUtc);
    }

    [Fact]
    public async Task CloseWhileOn_EndsTheAuditRow_AndClosesTheView()
    {
        using var app = new TestApp();
        var (hub, viewer, browser, _, pcSocket) = await OnControlAsync(app);

        await hub.CloseAsync(viewer, WebSocketCloseStatus.NormalClosure, "The viewer closed.");

        Assert.Equal(WebSocketCloseStatus.NormalClosure, browser.CloseSent!.Value.Status);
        Assert.NotNull(pcSocket.CloseSent);
        var row = Assert.Single(await ControlRowsAsync(app));
        Assert.NotNull(row.EndedUtc);
        Assert.Equal(ScreenControlState.Off, viewer.ControlState);
    }

    [Fact]
    public async Task CloseWhileRequesting_LeavesNoAuditRow()
    {
        using var app = new TestApp();
        var hub = NewHub(app);
        var (viewer, _) = await OpenAsync(hub, app);
        await AttachAsync(hub, viewer);
        await hub.RequestControlAsync(viewer, isServerPc: false);

        await hub.CloseAsync(viewer, WebSocketCloseStatus.NormalClosure, "The viewer closed.");

        Assert.Empty(await ControlRowsAsync(app));
    }

    [Fact]
    public async Task CloseAllAsync_EndsEveryControlPeriod()
    {
        using var app = new TestApp();
        var (hub, _, _, _, _) = await OnControlAsync(app);

        await hub.CloseAllAsync(ScreenProtocol.SessionEndedCloseStatus, ScreenProtocol.SessionEndedReason);

        Assert.NotNull((await ControlRowsAsync(app)).Single().EndedUtc);
    }
}
