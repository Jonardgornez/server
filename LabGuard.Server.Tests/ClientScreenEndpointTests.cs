using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using LabGuard.Server.Screen;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

namespace LabGuard.Server.Tests;

// The PC's end of a live view: its auth and view checks, the frame relay with its credits, and the heartbeat that starts it.
public class ClientScreenEndpointTests
{
    private const string BrowserWaiting = "{\"state\":\"waiting\"}";
    private const string BrowserLive = "{\"state\":\"live\"}";
    private const string BrowserPaused = "{\"state\":\"paused\"}";

    private sealed record LiveView(int PcId, string Token, string ViewId, WebSocket Browser);

    // The replaced connection is closed, so a send on it can fail. Only what the browser receives is checked.
    private static async Task TrySendAsync(WebSocket socket, byte[] payload)
    {
        try
        {
            await LiveViewClient.SendAsync(socket, payload);
        }
        catch (Exception exception) when (exception is WebSocketException or IOException or ObjectDisposedException or OperationCanceledException)
        {
        }
    }

    private static async Task<string> ViewIdFromHeartbeatAsync(TestApp app, string token)
    {
        var reply = await LiveViewClient.HeartbeatAsync(app, token);
        return reply.GetProperty("screenView").GetProperty("viewId").GetString()!;
    }

    // The administrator opens a view, and the PC learns its id from the next heartbeat.
    private static async Task<LiveView> OpenLiveViewAsync(TestApp app, string name = "PC-01")
    {
        var (id, token) = await LiveViewClient.AddPcAsync(app, name);
        var cookie = await LiveViewClient.SignInCookieAsync(app);
        var browser = await LiveViewClient.OpenAdminAsync(app, id, cookie);
        Assert.Equal(BrowserWaiting, await LiveViewClient.ReceiveStateAsync(browser));
        return new LiveView(id, token, await ViewIdFromHeartbeatAsync(app, token), browser);
    }

    // The PC connects its stream; the browser then hears that it is live.
    private static async Task<WebSocket> ConnectPcAsync(TestApp app, LiveView view)
    {
        var pc = await LiveViewClient.OpenPcAsync(app, view.Token, view.ViewId);
        Assert.Equal(BrowserLive, await LiveViewClient.ReceiveStateAsync(view.Browser));
        return pc;
    }

    private static async Task<HttpResponseMessage> PlainStreamRequestAsync(TestApp app, string? token, string viewId)
    {
        using var pc = app.CreatePcClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/client/screen/stream?viewId={viewId}");
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return await pc.SendAsync(request);
    }

    // Heartbeat

    [Fact]
    public async Task Heartbeat_ReportsNoView_WhileNoViewerIsOpen()
    {
        using var app = new TestApp();
        var (_, token) = await LiveViewClient.AddPcAsync(app);

        var reply = await LiveViewClient.HeartbeatAsync(app, token);

        Assert.Equal(JsonValueKind.Null, reply.GetProperty("screenView").ValueKind);
    }

    [Fact]
    public async Task Heartbeat_ReportsTheView_WhileOpen_AndClearsAfterTheViewerCloses()
    {
        using var app = new TestApp();
        var live = await OpenLiveViewAsync(app);

        var whileOpen = await LiveViewClient.HeartbeatAsync(app, live.Token);
        await live.Browser.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
        await LiveViewClient.ReceiveAsync(live.Browser);
        var afterClose = await LiveViewClient.HeartbeatAsync(app, live.Token);

        var view = whileOpen.GetProperty("screenView");
        Assert.Equal(live.ViewId, view.GetProperty("viewId").GetString());
        Assert.Equal(ScreenProtocol.NotifiedMode, view.GetProperty("mode").GetString());
        Assert.Equal(JsonValueKind.Null, afterClose.GetProperty("screenView").ValueKind);
    }

    [Fact]
    public async Task Heartbeat_ReportsOnlyThisPcsView()
    {
        using var app = new TestApp();
        var live = await OpenLiveViewAsync(app, "PC-01");
        var (_, otherToken) = await LiveViewClient.AddPcAsync(app, "PC-02");

        var reply = await LiveViewClient.HeartbeatAsync(app, otherToken);

        Assert.Equal(JsonValueKind.Null, reply.GetProperty("screenView").ValueKind);
    }

    // Auth and view checks, before the upgrade

    [Fact]
    public async Task ScreenStream_WithoutAToken_Returns401()
    {
        using var app = new TestApp();

        using var response = await PlainStreamRequestAsync(app, token: null, viewId: "abc");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ScreenStream_WithAnUnknownToken_Returns401()
    {
        using var app = new TestApp();
        var live = await OpenLiveViewAsync(app);

        var status = await LiveViewClient.RefusedStatusAsync(() => LiveViewClient.OpenPcAsync(app, "not-a-real-token", live.ViewId));

        Assert.Equal(401, status);
    }

    [Fact]
    public async Task ScreenStream_WithoutAWebSocket_Returns400()
    {
        using var app = new TestApp();
        var live = await OpenLiveViewAsync(app);

        using var response = await PlainStreamRequestAsync(app, live.Token, live.ViewId);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ScreenStream_WithAWrongViewId_Returns404()
    {
        using var app = new TestApp();
        var live = await OpenLiveViewAsync(app);

        var status = await LiveViewClient.RefusedStatusAsync(() => LiveViewClient.OpenPcAsync(app, live.Token, new string('0', 32)));

        Assert.Equal(404, status);
    }

    [Fact]
    public async Task ScreenStream_WithoutAnyViewOpen_Returns404()
    {
        using var app = new TestApp();
        var (_, token) = await LiveViewClient.AddPcAsync(app);

        var status = await LiveViewClient.RefusedStatusAsync(() => LiveViewClient.OpenPcAsync(app, token, "anything"));

        Assert.Equal(404, status);
    }

    [Fact]
    public async Task ScreenStream_ForAnotherPcsView_Returns404()
    {
        using var app = new TestApp();
        var owner = await OpenLiveViewAsync(app, "PC-01");
        var (_, intruderToken) = await LiveViewClient.AddPcAsync(app, "PC-02");

        var status = await LiveViewClient.RefusedStatusAsync(() => LiveViewClient.OpenPcAsync(app, intruderToken, owner.ViewId));

        Assert.Equal(404, status);
    }

    [Fact]
    public async Task ScreenStream_AfterAReplacement_RefusesTheOldViewId()
    {
        using var app = new TestApp();
        var first = await OpenLiveViewAsync(app);
        var cookie = await LiveViewClient.SignInCookieAsync(app);
        using var second = await LiveViewClient.OpenAdminAsync(app, first.PcId, cookie);
        Assert.Equal(BrowserWaiting, await LiveViewClient.ReceiveStateAsync(second));

        var status = await LiveViewClient.RefusedStatusAsync(() => LiveViewClient.OpenPcAsync(app, first.Token, first.ViewId));

        Assert.Equal(404, status);
    }

    // Frames and state

    [Fact]
    public async Task ScreenStream_RelaysEachJpegFrameToTheBrowser_Unchanged()
    {
        using var app = new TestApp();
        var live = await OpenLiveViewAsync(app);
        using var pc = await ConnectPcAsync(app, live);
        var frame = LiveViewClient.Frame("frame-1", size: 50_000);

        await LiveViewClient.SendAsync(pc, frame);

        var received = await LiveViewClient.ReceiveAsync(live.Browser);
        Assert.Equal(WebSocketMessageType.Binary, received.Type);
        Assert.Equal(frame, received.Payload);
    }

    [Fact]
    public async Task ScreenStream_DropsBinaryThatIsNotAJpeg_AndTextOtherThanPausedAndResumed()
    {
        using var app = new TestApp();
        var live = await OpenLiveViewAsync(app);
        using var pc = await ConnectPcAsync(app, live);

        await LiveViewClient.SendAsync(pc, "NOT-A-FRAME"u8.ToArray());
        await LiveViewClient.SendAsync(pc, new byte[] { 0xFF, 0xD8, 0x00, 0x01 });
        await LiveViewClient.SendTextAsync(pc, "hello");
        await LiveViewClient.SendAsync(pc, LiveViewClient.Frame("good"));

        var received = await LiveViewClient.ReceiveAsync(live.Browser);
        Assert.Equal(WebSocketMessageType.Binary, received.Type);
        Assert.Equal("good", LiveViewClient.LabelOf(received.Payload, 4));
    }

    [Fact]
    public async Task ScreenStream_PausedAndResumed_ChangeTheBrowserState()
    {
        using var app = new TestApp();
        var live = await OpenLiveViewAsync(app);
        using var pc = await ConnectPcAsync(app, live);

        await LiveViewClient.SendTextAsync(pc, "paused");
        Assert.Equal(BrowserPaused, await LiveViewClient.ReceiveStateAsync(live.Browser));
        await LiveViewClient.SendTextAsync(pc, "resumed");

        Assert.Equal(BrowserLive, await LiveViewClient.ReceiveStateAsync(live.Browser));
    }

    [Fact]
    public async Task ScreenStream_AFrameOverFourMiB_ClosesThePcSocketWith1009_AndTheViewWaits()
    {
        using var app = new TestApp();
        var live = await OpenLiveViewAsync(app);
        using var pc = await ConnectPcAsync(app, live);
        var tooBig = LiveViewClient.Frame("big", size: ScreenProtocol.MaxPcBinaryBytes + 1);

        var sending = LiveViewClient.SendAsync(pc, tooBig);
        var close = await LiveViewClient.ReceiveAsync(pc);
        try
        {
            await sending.WaitAsync(LiveViewClient.Patience);
        }
        catch (Exception exception) when (exception is WebSocketException or OperationCanceledException or TimeoutException)
        {
            // The server stopped reading at the limit, so the rest of the send can fail. The close is what matters.
        }

        Assert.Equal(WebSocketCloseStatus.MessageTooBig, close.CloseStatus);
        Assert.Equal(BrowserWaiting, await LiveViewClient.ReceiveStateAsync(live.Browser));
    }

    [Fact]
    public async Task ScreenStream_WhenTheFramesInFlightAreFull_ExtraFramesAreDropped_UntilAnAck()
    {
        using var app = new TestApp();
        var live = await OpenLiveViewAsync(app);
        using var pc = await ConnectPcAsync(app, live);

        await LiveViewClient.SendAsync(pc, LiveViewClient.Frame("one"));
        await LiveViewClient.SendAsync(pc, LiveViewClient.Frame("two"));
        await LiveViewClient.SendAsync(pc, LiveViewClient.Frame("three"));
        Assert.Equal("one", LiveViewClient.LabelOf((await LiveViewClient.ReceiveAsync(live.Browser)).Payload, 3));
        Assert.Equal("two", LiveViewClient.LabelOf((await LiveViewClient.ReceiveAsync(live.Browser)).Payload, 3));

        await LiveViewClient.SendTextAsync(live.Browser, "ack");
        var ack = await LiveViewClient.ReceiveAsync(pc);
        await LiveViewClient.SendAsync(pc, LiveViewClient.Frame("four"));

        Assert.Equal("ack", ack.Text);
        var next = await LiveViewClient.ReceiveAsync(live.Browser);
        Assert.Equal("four", LiveViewClient.LabelOf(next.Payload, 4));
    }

    [Fact]
    public async Task ScreenStream_BrowserAckWithAFrameOutstanding_ReachesThePc_AndOtherBrowserTextDoesNot()
    {
        using var app = new TestApp();
        var live = await OpenLiveViewAsync(app);
        using var pc = await ConnectPcAsync(app, live);

        await LiveViewClient.SendAsync(pc, LiveViewClient.Frame("one"));
        await LiveViewClient.ReceiveAsync(live.Browser);

        await LiveViewClient.SendTextAsync(live.Browser, "hello");
        await LiveViewClient.SendTextAsync(live.Browser, "ack");

        var received = await LiveViewClient.ReceiveAsync(pc);
        Assert.Equal("ack", received.Text);
    }

    [Fact]
    public async Task ScreenStream_ASecondPcSocket_ReplacesTheFirstWith4001()
    {
        using var app = new TestApp();
        var live = await OpenLiveViewAsync(app);
        using var first = await ConnectPcAsync(app, live);
        app.Time.Advance(TimeSpan.FromSeconds(1));

        using var second = await LiveViewClient.OpenPcAsync(app, live.Token, live.ViewId);

        var replaced = await LiveViewClient.ReceiveAsync(first);
        Assert.Equal((WebSocketCloseStatus)4001, replaced.CloseStatus);
        // The view was already live, so the new connection changes no state and the browser hears nothing new.
        await LiveViewClient.SendAsync(second, LiveViewClient.Frame("from-second"));
        var frame = await LiveViewClient.ReceiveAsync(live.Browser);
        Assert.Equal("from-second", LiveViewClient.LabelOf(frame.Payload, 11));
    }

    [Fact]
    public async Task ScreenStream_PcSocketLeaving_TellsTheBrowserItIsWaiting()
    {
        using var app = new TestApp();
        var live = await OpenLiveViewAsync(app);
        using var pc = await ConnectPcAsync(app, live);

        await pc.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);

        Assert.Equal(BrowserWaiting, await LiveViewClient.ReceiveStateAsync(live.Browser));
    }

    [Fact]
    public async Task ScreenStream_AReconnectWithinTheCooldown_IsRefusedWith429()
    {
        using var app = new TestApp();
        var live = await OpenLiveViewAsync(app);
        using var first = await ConnectPcAsync(app, live);

        var status = await LiveViewClient.RefusedStatusAsync(() => LiveViewClient.OpenPcAsync(app, live.Token, live.ViewId));

        Assert.Equal(429, status);
    }

    [Fact]
    public async Task ScreenStream_TheReplacedConnection_CannotRelayAFrame()
    {
        using var app = new TestApp();
        var live = await OpenLiveViewAsync(app);
        using var stale = await ConnectPcAsync(app, live);
        app.Time.Advance(TimeSpan.FromSeconds(1));
        using var fresh = await LiveViewClient.OpenPcAsync(app, live.Token, live.ViewId);
        Assert.Equal((WebSocketCloseStatus)4001, (await LiveViewClient.ReceiveAsync(stale)).CloseStatus);

        var sending = TrySendAsync(stale, LiveViewClient.Frame("stale"));
        await LiveViewClient.SendAsync(fresh, LiveViewClient.Frame("fresh"));
        await sending.WaitAsync(LiveViewClient.Patience);

        var received = await LiveViewClient.ReceiveAsync(live.Browser);
        Assert.Equal("fresh", LiveViewClient.LabelOf(received.Payload, 5));
        await CloseViewAndAwaitAuditAsync(app, live.Browser);
    }

    // Ends the view and waits until its audit row is written, so nothing is still writing when the test host is torn down.
    private static async Task CloseViewAndAwaitAuditAsync(TestApp app, WebSocket browser)
    {
        await browser.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
        await LiveViewClient.ReceiveAsync(browser);
        for (var attempt = 0; attempt < 50; attempt++)
        {
            if (await app.WithDbAsync(db => db.ScreenViewEvents.CountAsync(e => e.EndedAtUtc == null)) == 0)
            {
                return;
            }

            await Task.Delay(100);
        }
    }

    [Fact]
    public async Task ScreenStream_AFrameWithAnOversizedHeader_IsDropped()
    {
        using var app = new TestApp();
        var live = await OpenLiveViewAsync(app);
        using var pc = await ConnectPcAsync(app, live);

        await LiveViewClient.SendAsync(pc, LiveViewClient.Frame("huge", width: 9000, height: 100));
        await LiveViewClient.SendAsync(pc, LiveViewClient.Frame("fine"));

        var received = await LiveViewClient.ReceiveAsync(live.Browser);
        Assert.Equal("fine", LiveViewClient.LabelOf(received.Payload, 4));
    }

    [Fact]
    public async Task ScreenStream_ATextMessageOver16Bytes_ClosesThePcSocketWith1009()
    {
        using var app = new TestApp();
        var live = await OpenLiveViewAsync(app);
        using var pc = await ConnectPcAsync(app, live);

        await LiveViewClient.SendTextAsync(pc, new string('x', 17));

        var close = await LiveViewClient.ReceiveAsync(pc);
        Assert.Equal(WebSocketCloseStatus.MessageTooBig, close.CloseStatus);
    }
}
