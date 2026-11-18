using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using LabGuard.Server.Data;
using LabGuard.Server.Screen;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LabGuard.Server.Tests;

// The PC's secure-desktop helper over the real endpoints: its auth, one helper per PC, taking over while the PC's stream is
// paused, control and Ctrl+Alt+Del routing, the fallback with no helper, and every way a helper is closed.
public class ScreenSecureHelperEndpointTests
{
    private const string Waiting = "{\"state\":\"waiting\"}";
    private const string Live = "{\"state\":\"live\"}";
    private const string Paused = "{\"state\":\"paused\"}";
    private const string Secure = "{\"state\":\"secure\"}";
    private const string Start = "{\"t\":\"start\",\"mode\":\"notified\"}";
    private const string StartAnonymous = "{\"t\":\"start\",\"mode\":\"anonymous\"}";
    private const string Stop = "{\"t\":\"stop\"}";
    private const string Release = "{\"t\":\"release\"}";
    private const string PcOn = "{\"t\":\"control\",\"on\":true}";
    private const string PcOff = "{\"t\":\"control\",\"on\":false}";
    private const string BrowserOn = "{\"t\":\"control\",\"on\":true}";
    private const string BrowserOff = "{\"t\":\"control\",\"on\":false}";
    private const string Requesting = "{\"control\":\"requesting\"}";
    private const string On = "{\"control\":\"on\"}";
    private const string Sas = "{\"t\":\"sas\"}";

    private sealed record LiveView(int PcId, string Token, string ViewId, WebSocket Browser);

    private static string OffMessage(string reason) => $"{{\"control\":\"off\",\"reason\":\"{reason}\"}}";

    private static string Move(int x, int y) => $"{{\"t\":\"move\",\"x\":{x},\"y\":{y}}}";

    private static async Task<string> TextAsync(WebSocket socket)
    {
        var message = await LiveViewClient.ReceiveAsync(socket);
        Assert.Equal(WebSocketMessageType.Text, message.Type);
        return message.Text;
    }

    private static async Task<LiveView> OpenViewAsync(TestApp app, string query = "")
    {
        var (id, token) = await LiveViewClient.AddPcAsync(app);
        var cookie = await LiveViewClient.SignInCookieAsync(app);
        var browser = await LiveViewClient.OpenAdminAsync(app, id, cookie, query);
        Assert.Equal(Waiting, await LiveViewClient.ReceiveStateAsync(browser));
        var reply = await LiveViewClient.HeartbeatAsync(app, token);
        return new LiveView(id, token, reply.GetProperty("screenView").GetProperty("viewId").GetString()!, browser);
    }

    private static async Task<WebSocket> ConnectPcAsync(TestApp app, LiveView view)
    {
        var pc = await LiveViewClient.OpenPcAsync(app, view.Token, view.ViewId);
        Assert.Equal(Live, await TextAsync(view.Browser));
        return pc;
    }

    // The upgrade completes before the server registers the helper, so a test waits until it is registered.
    private static async Task<WebSocket> ConnectHelperAsync(TestApp app, int pcId, string token)
    {
        var hub = app.Services.GetRequiredService<ScreenStreamHub>();
        var registeredBefore = hub.HelperSnapshot().FirstOrDefault(helper => helper.PcId == pcId);
        var helper = await LiveViewClient.OpenHelperAsync(app, token);
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var registered = hub.HelperSnapshot().FirstOrDefault(candidate => candidate.PcId == pcId);
            if (registered is not null && !ReferenceEquals(registered, registeredBefore))
            {
                return helper;
            }

            await Task.Delay(20);
        }

        Assert.Fail("The helper was never registered.");
        return helper;
    }

    // The PC pauses while a helper is connected: the helper is started and the browser hears secure.
    private static async Task PauseOntoHelperAsync(LiveView view, WebSocket pc, WebSocket helper, string start = Start)
    {
        await LiveViewClient.SendTextAsync(pc, "paused");
        Assert.Equal(start, await TextAsync(helper));
        Assert.Equal(Secure, await TextAsync(view.Browser));
    }

    private static async Task ControlOnThroughPcAsync(LiveView view, WebSocket pc)
    {
        await LiveViewClient.SendTextAsync(view.Browser, BrowserOn);
        Assert.Equal(Requesting, await TextAsync(view.Browser));
        Assert.Equal(PcOn, await TextAsync(pc));
        await LiveViewClient.SendTextAsync(pc, "control-on");
        Assert.Equal(On, await TextAsync(view.Browser));
    }

    private static async Task<List<ScreenControlEvent>> AwaitControlRowsAsync(TestApp app, Func<List<ScreenControlEvent>, bool> ready)
    {
        var rows = await app.WithDbAsync(db => db.ScreenControlEvents.AsNoTracking().OrderBy(e => e.Id).ToListAsync());
        for (var attempt = 0; attempt < 50 && !ready(rows); attempt++)
        {
            await Task.Delay(100);
            rows = await app.WithDbAsync(db => db.ScreenControlEvents.AsNoTracking().OrderBy(e => e.Id).ToListAsync());
        }

        return rows;
    }

    private static async Task FinishAsync(TestApp app, LiveView view)
    {
        await view.Browser.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
        while ((await LiveViewClient.ReceiveAsync(view.Browser)).Type != WebSocketMessageType.Close)
        {
        }

        for (var attempt = 0; attempt < 50; attempt++)
        {
            var openRows = await app.WithDbAsync(async db =>
                await db.ScreenControlEvents.CountAsync(e => e.EndedUtc == null)
                + await db.ScreenViewEvents.CountAsync(e => e.EndedAtUtc == null));
            if (openRows == 0)
            {
                return;
            }

            await Task.Delay(100);
        }
    }

    private static async Task<WebSocketCloseStatus?> CloseStatusAsync(WebSocket socket)
    {
        while (true)
        {
            var message = await LiveViewClient.ReceiveAsync(socket);
            if (message.Type == WebSocketMessageType.Close)
            {
                return message.CloseStatus;
            }
        }
    }

    // Auth, before the upgrade

    [Fact]
    public async Task SecureStream_WithoutAToken_Returns401()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();

        using var response = await client.GetAsync("/api/client/screen/secure-stream");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task SecureStream_WithAnUnknownToken_Returns401()
    {
        using var app = new TestApp();
        await LiveViewClient.AddPcAsync(app);

        var status = await LiveViewClient.RefusedStatusAsync(() => LiveViewClient.OpenHelperAsync(app, "not-a-real-token"));

        Assert.Equal(401, status);
    }

    [Fact]
    public async Task SecureStream_WithoutAWebSocket_Returns400()
    {
        using var app = new TestApp();
        var (_, token) = await LiveViewClient.AddPcAsync(app);
        using var client = app.CreatePcClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/client/screen/secure-stream");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task SecureStream_OnTheAdminPort_IsNotFound()
    {
        using var app = new TestApp();
        var (_, token) = await LiveViewClient.AddPcAsync(app);
        using var browser = app.CreateBrowser();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/client/screen/secure-stream");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await browser.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task SecureStream_AReconnectWithinTheCooldown_IsRefusedWith429()
    {
        using var app = new TestApp();
        var (id, token) = await LiveViewClient.AddPcAsync(app);
        using var first = await ConnectHelperAsync(app, id, token);

        var status = await LiveViewClient.RefusedStatusAsync(() => LiveViewClient.OpenHelperAsync(app, token));

        Assert.Equal(429, status);
    }

    // One helper per PC

    [Fact]
    public async Task SecureStream_ASecondHelper_ReplacesTheFirstWith4001_AndTakesOver()
    {
        using var app = new TestApp();
        var view = await OpenViewAsync(app);
        using var pc = await ConnectPcAsync(app, view);
        using var first = await ConnectHelperAsync(app, view.PcId, view.Token);
        await PauseOntoHelperAsync(view, pc, first);
        app.Time.Advance(TimeSpan.FromSeconds(1));

        using var second = await ConnectHelperAsync(app, view.PcId, view.Token);

        Assert.Equal(Stop, await TextAsync(first));
        Assert.Equal(ScreenProtocol.ReplacedCloseStatus, await CloseStatusAsync(first));
        Assert.Equal(Start, await TextAsync(second));
        await LiveViewClient.SendAsync(second, LiveViewClient.Frame("from-second"));
        Assert.Equal("from-second", LiveViewClient.LabelOf((await LiveViewClient.ReceiveAsync(view.Browser)).Payload, 11));
        await FinishAsync(app, view);
    }

    [Fact]
    public async Task SecureStream_HelpersOfDifferentPcs_DoNotReplaceEachOther()
    {
        using var app = new TestApp();
        var (firstId, firstToken) = await LiveViewClient.AddPcAsync(app, "PC-01");
        var (secondId, secondToken) = await LiveViewClient.AddPcAsync(app, "PC-02");

        using var first = await ConnectHelperAsync(app, firstId, firstToken);
        using var second = await ConnectHelperAsync(app, secondId, secondToken);

        Assert.Equal(2, app.Services.GetRequiredService<ScreenStreamHub>().HelperSnapshot().Count);
        Assert.Equal(WebSocketState.Open, first.State);
    }

    // Taking over while the PC is paused

    [Fact]
    public async Task Paused_WithAHelper_StartsIt_RelaysItsFrames_AndAcksGoToIt_UntilResumedStopsIt()
    {
        using var app = new TestApp();
        var view = await OpenViewAsync(app);
        using var pc = await ConnectPcAsync(app, view);
        using var helper = await ConnectHelperAsync(app, view.PcId, view.Token);

        await PauseOntoHelperAsync(view, pc, helper);
        var frame = LiveViewClient.Frame("secure-1", size: 40_000);
        await LiveViewClient.SendAsync(helper, frame);
        Assert.Equal(frame, (await LiveViewClient.ReceiveAsync(view.Browser)).Payload);
        await LiveViewClient.SendTextAsync(view.Browser, "ack");
        Assert.Equal("ack", await TextAsync(helper));

        await LiveViewClient.SendTextAsync(pc, "resumed");
        Assert.Equal(Stop, await TextAsync(helper));
        Assert.Equal(Live, await TextAsync(view.Browser));
        await LiveViewClient.SendAsync(helper, LiveViewClient.Frame("late"));
        await LiveViewClient.SendAsync(pc, LiveViewClient.Frame("pc-again"));

        Assert.Equal("pc-again", LiveViewClient.LabelOf((await LiveViewClient.ReceiveAsync(view.Browser)).Payload, 8));
        await FinishAsync(app, view);
    }

    [Fact]
    public async Task Paused_TheHelperHasItsOwnWindowOfTwoFrames()
    {
        using var app = new TestApp();
        var view = await OpenViewAsync(app);
        using var pc = await ConnectPcAsync(app, view);
        using var helper = await ConnectHelperAsync(app, view.PcId, view.Token);
        await PauseOntoHelperAsync(view, pc, helper);

        await LiveViewClient.SendAsync(helper, LiveViewClient.Frame("one"));
        await LiveViewClient.SendAsync(helper, LiveViewClient.Frame("two"));
        await LiveViewClient.SendAsync(helper, LiveViewClient.Frame("three"));
        Assert.Equal("one", LiveViewClient.LabelOf((await LiveViewClient.ReceiveAsync(view.Browser)).Payload, 3));
        Assert.Equal("two", LiveViewClient.LabelOf((await LiveViewClient.ReceiveAsync(view.Browser)).Payload, 3));
        await LiveViewClient.SendTextAsync(view.Browser, "ack");
        Assert.Equal("ack", await TextAsync(helper));
        await LiveViewClient.SendAsync(helper, LiveViewClient.Frame("four"));

        Assert.Equal("four", LiveViewClient.LabelOf((await LiveViewClient.ReceiveAsync(view.Browser)).Payload, 4));
        await FinishAsync(app, view);
    }

    [Fact]
    public async Task AHelperConnectingWhileThePcIsPaused_StartsAtOnce()
    {
        using var app = new TestApp();
        var view = await OpenViewAsync(app, query: "?mode=anonymous");
        using var pc = await ConnectPcAsync(app, view);
        await LiveViewClient.SendTextAsync(pc, "paused");
        Assert.Equal(Paused, await TextAsync(view.Browser));

        using var helper = await ConnectHelperAsync(app, view.PcId, view.Token);

        Assert.Equal(StartAnonymous, await TextAsync(helper));
        Assert.Equal(Secure, await TextAsync(view.Browser));
        await FinishAsync(app, view);
    }

    [Fact]
    public async Task AnIdleHelperSendingAFrame_IsClosedWith1008_AndNothingReachesTheBrowser()
    {
        using var app = new TestApp();
        var view = await OpenViewAsync(app);
        using var pc = await ConnectPcAsync(app, view);
        using var helper = await ConnectHelperAsync(app, view.PcId, view.Token);

        await LiveViewClient.SendAsync(helper, LiveViewClient.Frame("idle"));
        Assert.Equal(WebSocketCloseStatus.PolicyViolation, await CloseStatusAsync(helper));
        await LiveViewClient.SendAsync(pc, LiveViewClient.Frame("pc"));

        Assert.Equal("pc", LiveViewClient.LabelOf((await LiveViewClient.ReceiveAsync(view.Browser)).Payload, 2));
        await FinishAsync(app, view);
    }

    [Fact]
    public async Task AFrameWithinTheGraceAfterStop_IsDropped_ButOneAfterItClosesTheHelper()
    {
        using var app = new TestApp();
        var view = await OpenViewAsync(app);
        using var pc = await ConnectPcAsync(app, view);
        using var helper = await ConnectHelperAsync(app, view.PcId, view.Token);
        await PauseOntoHelperAsync(view, pc, helper);
        await LiveViewClient.SendTextAsync(pc, "resumed");
        Assert.Equal(Stop, await TextAsync(helper));
        Assert.Equal(Live, await TextAsync(view.Browser));

        await LiveViewClient.SendAsync(helper, LiveViewClient.Frame("in-grace"));
        await LiveViewClient.SendAsync(pc, LiveViewClient.Frame("pc"));
        Assert.Equal("pc", LiveViewClient.LabelOf((await LiveViewClient.ReceiveAsync(view.Browser)).Payload, 2));
        app.Time.Advance(SecureHelperProtocol.StopGrace);
        await LiveViewClient.SendAsync(helper, LiveViewClient.Frame("too-late"));

        Assert.Equal(WebSocketCloseStatus.PolicyViolation, await CloseStatusAsync(helper));
        await FinishAsync(app, view);
    }

    [Fact]
    public async Task AStreamingHelperOverTheFrameCap_IsClosedWith1008()
    {
        using var app = new TestApp();
        var view = await OpenViewAsync(app);
        using var pc = await ConnectPcAsync(app, view);
        using var helper = await ConnectHelperAsync(app, view.PcId, view.Token);
        await PauseOntoHelperAsync(view, pc, helper);

        for (var frame = 0; frame <= SecureHelperProtocol.MaxFramesPerSecond; frame++)
        {
            await LiveViewClient.SendAsync(helper, LiveViewClient.Frame($"f{frame}"));
        }

        Assert.Equal(WebSocketCloseStatus.PolicyViolation, await CloseStatusAsync(helper));
        await FinishAsync(app, view);
    }

    [Fact]
    public async Task SecureStream_HasItsOwnRateLimit_ThatDoesNotUseUpTheHeartbeats()
    {
        using var app = new TestApp();
        var (_, token) = await LiveViewClient.AddPcAsync(app);
        for (var attempt = 0; attempt < LabGuard.Server.Clients.ClientEndpoints.MaxSecureHelperConnectsPerMinute; attempt++)
        {
            Assert.Equal(401, await LiveViewClient.RefusedStatusAsync(() => LiveViewClient.OpenHelperAsync(app, "not-a-real-token")));
        }

        var limited = await LiveViewClient.RefusedStatusAsync(() => LiveViewClient.OpenHelperAsync(app, token));

        Assert.Equal(429, limited);
        await LiveViewClient.HeartbeatAsync(app, token);
    }

    [Fact]
    public async Task TheViewerClosing_StopsTheHelper_WhichStaysConnected()
    {
        using var app = new TestApp();
        var view = await OpenViewAsync(app);
        using var pc = await ConnectPcAsync(app, view);
        using var helper = await ConnectHelperAsync(app, view.PcId, view.Token);
        await PauseOntoHelperAsync(view, pc, helper);

        await FinishAsync(app, view);

        Assert.Equal(Stop, await TextAsync(helper));
        Assert.Single(app.Services.GetRequiredService<ScreenStreamHub>().HelperSnapshot());
    }

    [Fact]
    public async Task ThePcStreamLeaving_StopsTheHelper_AndTheViewWaits()
    {
        using var app = new TestApp();
        var view = await OpenViewAsync(app);
        using var pc = await ConnectPcAsync(app, view);
        using var helper = await ConnectHelperAsync(app, view.PcId, view.Token);
        await PauseOntoHelperAsync(view, pc, helper);

        await pc.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);

        Assert.Equal(Stop, await TextAsync(helper));
        Assert.Equal(Waiting, await TextAsync(view.Browser));
        await FinishAsync(app, view);
    }

    // No helper: today's behaviour

    [Fact]
    public async Task Paused_WithNoHelper_TellsTheBrowserPaused_AndInputStillGoesToThePc()
    {
        using var app = new TestApp();
        var view = await OpenViewAsync(app);
        using var pc = await ConnectPcAsync(app, view);
        await ControlOnThroughPcAsync(view, pc);

        await LiveViewClient.SendTextAsync(pc, "paused");
        Assert.Equal(Paused, await TextAsync(view.Browser));
        await LiveViewClient.SendTextAsync(view.Browser, Move(5, 6));
        Assert.Equal(Move(5, 6), await TextAsync(pc));
        await LiveViewClient.SendTextAsync(pc, "control-off");

        Assert.Equal(OffMessage(ControlReasons.PcEnded), await TextAsync(view.Browser));
        await FinishAsync(app, view);
    }

    [Fact]
    public async Task TheHelperLeavingWhileItStreams_FallsBackToPaused_AndEndsControl()
    {
        using var app = new TestApp();
        var view = await OpenViewAsync(app);
        using var pc = await ConnectPcAsync(app, view);
        using var helper = await ConnectHelperAsync(app, view.PcId, view.Token);
        await ControlOnThroughPcAsync(view, pc);
        await PauseOntoHelperAsync(view, pc, helper);

        await helper.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);

        Assert.Equal(OffMessage(ControlReasons.Connection), await TextAsync(view.Browser));
        Assert.Equal(Paused, await TextAsync(view.Browser));
        var rows = await AwaitControlRowsAsync(app, rows => rows.All(row => row.EndedUtc is not null));
        Assert.NotNull(Assert.Single(rows).EndedUtc);
        await FinishAsync(app, view);
    }

    // Control across a switch

    [Fact]
    public async Task ControlCarriesOntoTheHelper_InputGoesToIt_AndBackToThePcOnResume_WithOneAuditRow()
    {
        using var app = new TestApp();
        var view = await OpenViewAsync(app);
        using var pc = await ConnectPcAsync(app, view);
        using var helper = await ConnectHelperAsync(app, view.PcId, view.Token);
        await ControlOnThroughPcAsync(view, pc);

        await LiveViewClient.SendTextAsync(pc, "paused");
        Assert.Equal(Start, await TextAsync(helper));
        Assert.Equal(PcOn, await TextAsync(helper));
        Assert.Equal(Secure, await TextAsync(view.Browser));
        Assert.Equal(Release, await TextAsync(pc));
        await LiveViewClient.SendTextAsync(view.Browser, Move(10, 20));
        Assert.Equal(Move(10, 20), await TextAsync(helper));
        // The paused PC can't end control the helper carries.
        await LiveViewClient.SendTextAsync(pc, "control-off");

        await LiveViewClient.SendTextAsync(pc, "resumed");
        Assert.Equal(Stop, await TextAsync(helper));
        Assert.Equal(Live, await TextAsync(view.Browser));
        Assert.Equal(PcOn, await TextAsync(pc));
        await LiveViewClient.SendTextAsync(view.Browser, Move(30, 40));
        Assert.Equal(Move(30, 40), await TextAsync(pc));

        var rows = await AwaitControlRowsAsync(app, _ => true);
        Assert.Null(Assert.Single(rows).EndedUtc);
        await FinishAsync(app, view);
    }

    [Fact]
    public async Task ControlRequestedWhileTheHelperStreams_IsConfirmedByTheHelper()
    {
        using var app = new TestApp();
        var view = await OpenViewAsync(app);
        using var pc = await ConnectPcAsync(app, view);
        using var helper = await ConnectHelperAsync(app, view.PcId, view.Token);
        await PauseOntoHelperAsync(view, pc, helper);

        await LiveViewClient.SendTextAsync(view.Browser, BrowserOn);
        Assert.Equal(Requesting, await TextAsync(view.Browser));
        Assert.Equal(PcOn, await TextAsync(helper));
        // Only the socket that was asked can confirm.
        await LiveViewClient.SendTextAsync(pc, "control-on");
        await LiveViewClient.SendTextAsync(helper, "control-on");

        Assert.Equal(On, await TextAsync(view.Browser));
        var rows = await AwaitControlRowsAsync(app, rows => rows.Count == 1);
        Assert.Single(rows);
        await FinishAsync(app, view);
    }

    [Fact]
    public async Task TheHelperEndingControl_EndsItAsPcEnded()
    {
        using var app = new TestApp();
        var view = await OpenViewAsync(app);
        using var pc = await ConnectPcAsync(app, view);
        using var helper = await ConnectHelperAsync(app, view.PcId, view.Token);
        await ControlOnThroughPcAsync(view, pc);
        await PauseOntoHelperAsync(view, pc, helper);
        Assert.Equal(PcOn, await TextAsync(helper));

        await LiveViewClient.SendTextAsync(helper, "control-off");

        Assert.Equal(OffMessage(ControlReasons.PcEnded), await TextAsync(view.Browser));
        await FinishAsync(app, view);
    }

    [Fact]
    public async Task TheAdministratorStoppingControl_WhileTheHelperStreams_TellsBothTheHelperAndThePc()
    {
        using var app = new TestApp();
        var view = await OpenViewAsync(app);
        using var pc = await ConnectPcAsync(app, view);
        using var helper = await ConnectHelperAsync(app, view.PcId, view.Token);
        await ControlOnThroughPcAsync(view, pc);
        await PauseOntoHelperAsync(view, pc, helper);
        Assert.Equal(PcOn, await TextAsync(helper));
        Assert.Equal(Release, await TextAsync(pc));

        await LiveViewClient.SendTextAsync(view.Browser, BrowserOff);

        Assert.Equal(OffMessage(ControlReasons.Stopped), await TextAsync(view.Browser));
        Assert.Equal(PcOff, await TextAsync(pc));
        Assert.Equal(PcOff, await TextAsync(helper));
        await FinishAsync(app, view);
    }

    // Ctrl+Alt+Del

    [Fact]
    public async Task Sas_GoesToAnIdleHelper_NotThePc_WhileThePcStreams()
    {
        using var app = new TestApp();
        var view = await OpenViewAsync(app);
        using var pc = await ConnectPcAsync(app, view);
        using var helper = await ConnectHelperAsync(app, view.PcId, view.Token);
        await ControlOnThroughPcAsync(view, pc);

        await LiveViewClient.SendTextAsync(view.Browser, Sas);
        Assert.Equal(Sas, await TextAsync(helper));
        await LiveViewClient.SendTextAsync(view.Browser, Move(1, 2));

        Assert.Equal(Move(1, 2), await TextAsync(pc));
        await FinishAsync(app, view);
    }

    [Fact]
    public async Task Sas_GoesToTheStreamingHelper()
    {
        using var app = new TestApp();
        var view = await OpenViewAsync(app);
        using var pc = await ConnectPcAsync(app, view);
        using var helper = await ConnectHelperAsync(app, view.PcId, view.Token);
        await ControlOnThroughPcAsync(view, pc);
        await PauseOntoHelperAsync(view, pc, helper);
        Assert.Equal(PcOn, await TextAsync(helper));

        await LiveViewClient.SendTextAsync(view.Browser, Sas);

        Assert.Equal(Sas, await TextAsync(helper));
        await FinishAsync(app, view);
    }

    [Fact]
    public async Task Sas_WithNoHelper_StillGoesToThePc()
    {
        using var app = new TestApp();
        var view = await OpenViewAsync(app);
        using var pc = await ConnectPcAsync(app, view);
        await ControlOnThroughPcAsync(view, pc);

        await LiveViewClient.SendTextAsync(view.Browser, Sas);

        Assert.Equal(Sas, await TextAsync(pc));
        await FinishAsync(app, view);
    }

    [Fact]
    public async Task Sas_WithoutControl_IsNotSent()
    {
        using var app = new TestApp();
        var view = await OpenViewAsync(app);
        using var pc = await ConnectPcAsync(app, view);
        using var helper = await ConnectHelperAsync(app, view.PcId, view.Token);

        await LiveViewClient.SendTextAsync(view.Browser, Sas);
        await PauseOntoHelperAsync(view, pc, helper);

        Assert.Equal(WebSocketState.Open, helper.State);
        await FinishAsync(app, view);
    }

    // Strict messages and sizes

    [Theory]
    [InlineData("hello")]
    [InlineData("ack")]
    [InlineData("{\"t\":\"x\"}")]
    public async Task UnknownText_ClosesTheHelperWith1008(string text)
    {
        using var app = new TestApp();
        var (id, token) = await LiveViewClient.AddPcAsync(app);
        using var helper = await ConnectHelperAsync(app, id, token);

        await LiveViewClient.SendTextAsync(helper, text);

        Assert.Equal(WebSocketCloseStatus.PolicyViolation, await CloseStatusAsync(helper));
    }

    [Fact]
    public async Task BinaryThatIsNotAJpeg_ClosesTheHelperWith1008()
    {
        using var app = new TestApp();
        var (id, token) = await LiveViewClient.AddPcAsync(app);
        using var helper = await ConnectHelperAsync(app, id, token);

        await LiveViewClient.SendAsync(helper, "NOT-A-FRAME"u8.ToArray());

        Assert.Equal(WebSocketCloseStatus.PolicyViolation, await CloseStatusAsync(helper));
    }

    [Fact]
    public async Task AFrameWithAnOversizedHeader_ClosesTheHelperWith1008()
    {
        using var app = new TestApp();
        var (id, token) = await LiveViewClient.AddPcAsync(app);
        using var helper = await ConnectHelperAsync(app, id, token);

        await LiveViewClient.SendAsync(helper, LiveViewClient.Frame("huge", width: 9000, height: 100));

        Assert.Equal(WebSocketCloseStatus.PolicyViolation, await CloseStatusAsync(helper));
    }

    [Fact]
    public async Task AFrameOverTwoMiB_ClosesTheHelperWith1009()
    {
        using var app = new TestApp();
        var (id, token) = await LiveViewClient.AddPcAsync(app);
        using var helper = await ConnectHelperAsync(app, id, token);

        var sending = LiveViewClient.SendAsync(helper, LiveViewClient.Frame("big", size: ScreenProtocol.MaxPcBinaryBytes + 1));
        var status = await CloseStatusAsync(helper);
        try
        {
            await sending.WaitAsync(LiveViewClient.Patience);
        }
        catch (Exception exception) when (exception is WebSocketException or OperationCanceledException or TimeoutException)
        {
            // The server stopped reading at the limit, so the rest of the send can fail. The close is what matters.
        }

        Assert.Equal(WebSocketCloseStatus.MessageTooBig, status);
    }

    [Fact]
    public async Task ATextOver16Bytes_ClosesTheHelperWith1009()
    {
        using var app = new TestApp();
        var (id, token) = await LiveViewClient.AddPcAsync(app);
        using var helper = await ConnectHelperAsync(app, id, token);

        await LiveViewClient.SendTextAsync(helper, new string('x', 17));

        Assert.Equal(WebSocketCloseStatus.MessageTooBig, await CloseStatusAsync(helper));
    }

    [Fact]
    public async Task KnownTextWhileIdle_KeepsTheHelperOpen()
    {
        using var app = new TestApp();
        var view = await OpenViewAsync(app);
        using var pc = await ConnectPcAsync(app, view);
        using var helper = await ConnectHelperAsync(app, view.PcId, view.Token);

        foreach (var text in new[] { "paused", "resumed", "control-on", "control-off" })
        {
            await LiveViewClient.SendTextAsync(helper, text);
        }

        await PauseOntoHelperAsync(view, pc, helper);
        await FinishAsync(app, view);
    }

    // Removal and revocation

    [Fact]
    public async Task DeletingThePc_ClosesItsHelperWith4002()
    {
        using var app = new TestApp();
        var (id, token) = await LiveViewClient.AddPcAsync(app);
        using var helper = await ConnectHelperAsync(app, id, token);
        using var admin = await app.SignedInAdminAsync();
        app.Time.Advance(TimeSpan.FromMinutes(1));

        using var response = await admin.DeleteAsync($"/api/pcs/{id}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(ScreenProtocol.PcOfflineCloseStatus, await CloseStatusAsync(helper));
        Assert.Empty(app.Services.GetRequiredService<ScreenStreamHub>().HelperSnapshot());
    }

    [Fact]
    public async Task TheMonitor_ClosesAHelperWhoseTokenWasReplaced_With4002_AndLeavesOthers()
    {
        using var app = new TestApp();
        var (revokedId, revokedToken) = await LiveViewClient.AddPcAsync(app, "PC-01");
        var (keptId, keptToken) = await LiveViewClient.AddPcAsync(app, "PC-02");
        using var revoked = await ConnectHelperAsync(app, revokedId, revokedToken);
        using var kept = await ConnectHelperAsync(app, keptId, keptToken);
        await app.WithDbAsync(db => db.Pcs
            .Where(p => p.Id == revokedId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(p => p.TokenHash, new string('0', 64))));

        await app.Services.GetRequiredService<ScreenViewMonitor>().CheckOnceAsync(CancellationToken.None);

        Assert.Equal(ScreenProtocol.PcOfflineCloseStatus, await CloseStatusAsync(revoked));
        var remaining = Assert.Single(app.Services.GetRequiredService<ScreenStreamHub>().HelperSnapshot());
        Assert.Equal(keptId, remaining.PcId);
    }
}
