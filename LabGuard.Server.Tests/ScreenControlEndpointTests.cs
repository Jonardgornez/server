using System.Net.WebSockets;
using LabGuard.Server.Data;
using LabGuard.Server.Screen;
using Microsoft.EntityFrameworkCore;

namespace LabGuard.Server.Tests;

// Remote control over the real endpoints: the browser's control and input messages, the PC's confirmation, and every way control ends.
public class ScreenControlEndpointTests
{
    private const string Requesting = "{\"control\":\"requesting\"}";
    private const string On = "{\"control\":\"on\"}";
    private const string PcOn = "{\"t\":\"control\",\"on\":true}";
    private const string PcOff = "{\"t\":\"control\",\"on\":false}";
    private const string BrowserOn = "{\"t\":\"control\",\"on\":true}";
    private const string BrowserOff = "{\"t\":\"control\",\"on\":false}";
    private const string Up = "{\"t\":\"up\",\"b\":0,\"x\":1,\"y\":1}";

    private sealed record LiveView(int PcId, string Token, string ViewId, WebSocket Browser);

    private static string OffMessage(string reason) => $"{{\"control\":\"off\",\"reason\":\"{reason}\"}}";

    private static async Task<string> TextAsync(WebSocket socket)
    {
        var message = await LiveViewClient.ReceiveAsync(socket);
        Assert.Equal(WebSocketMessageType.Text, message.Type);
        return message.Text;
    }

    // The administrator's view is open and waiting, and the PC's heartbeat has reported its view id.
    private static async Task<LiveView> OpenViewAsync(TestApp app, string query = "", string pcIp = "192.168.1.50", string name = "PC-01")
    {
        var (id, token) = await LiveViewClient.AddPcAsync(app, name);
        var cookie = await LiveViewClient.SignInCookieAsync(app);
        var browser = await LiveViewClient.OpenAdminAsync(app, id, cookie, query);
        Assert.Equal("{\"state\":\"waiting\"}", await LiveViewClient.ReceiveStateAsync(browser));
        var reply = await LiveViewClient.HeartbeatAsync(app, token, pcIp);
        return new LiveView(id, token, reply.GetProperty("screenView").GetProperty("viewId").GetString()!, browser);
    }

    private static async Task<WebSocket> ConnectPcAsync(TestApp app, LiveView view, string pcIp = "192.168.1.50")
    {
        var pc = await LiveViewClient.OpenPcAsync(app, view.Token, view.ViewId, pcIp);
        Assert.Equal("{\"state\":\"live\"}", await LiveViewClient.ReceiveStateAsync(view.Browser));
        return pc;
    }

    // Asks for control and has the PC confirm it. Returns once the browser hears on.
    private static async Task ControlOnAsync(LiveView view, WebSocket pc)
    {
        await LiveViewClient.SendTextAsync(view.Browser, BrowserOn);
        Assert.Equal(Requesting, await TextAsync(view.Browser));
        Assert.Equal(PcOn, await TextAsync(pc));
        await LiveViewClient.SendTextAsync(pc, "control-on");
        Assert.Equal(On, await TextAsync(view.Browser));
    }

    private static async Task<List<ScreenControlEvent>> ControlRowsAsync(TestApp app) =>
        await app.WithDbAsync(db => db.ScreenControlEvents.AsNoTracking().OrderBy(e => e.Id).ToListAsync());

    // The control end is written after its messages go out, so a test that has seen the message waits for the row.
    private static async Task<List<ScreenControlEvent>> AwaitControlRowsAsync(TestApp app, Func<List<ScreenControlEvent>, bool> ready)
    {
        var rows = await ControlRowsAsync(app);
        for (var attempt = 0; attempt < 50 && !ready(rows); attempt++)
        {
            await Task.Delay(100);
            rows = await ControlRowsAsync(app);
        }

        return rows;
    }

    // Closes the view and waits until nothing is still open, so no audit write is in flight when the test host is torn down.
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

    // Refusals, in the order the rules apply

    [Fact]
    public async Task ControlOn_OnAnAnonymousView_WithNoPcAttached_IsRefusedAsWaiting_NotForTheMode()
    {
        using var app = new TestApp();
        var view = await OpenViewAsync(app, query: "?mode=anonymous");

        await LiveViewClient.SendTextAsync(view.Browser, BrowserOn);

        Assert.Equal(OffMessage("waiting"), await TextAsync(view.Browser));
        Assert.Empty(await ControlRowsAsync(app));
        await FinishAsync(app, view);
    }

    [Theory]
    [InlineData(TestApp.MachineLanAddress)]
    [InlineData("127.0.0.1")]
    public async Task ControlOn_ForTheServersOwnPc_IsRefusedAsServerPc(string pcIp)
    {
        using var app = new TestApp();
        var view = await OpenViewAsync(app, pcIp: pcIp);
        using var pc = await ConnectPcAsync(app, view, pcIp);

        await LiveViewClient.SendTextAsync(view.Browser, BrowserOn);

        Assert.Equal(OffMessage("server-pc"), await TextAsync(view.Browser));
        Assert.Empty(await ControlRowsAsync(app));
        await FinishAsync(app, view);
    }

    [Fact]
    public async Task ControlOn_WithNoPcAttached_IsRefusedAsWaiting()
    {
        using var app = new TestApp();
        var view = await OpenViewAsync(app);

        await LiveViewClient.SendTextAsync(view.Browser, BrowserOn);

        Assert.Equal(OffMessage("waiting"), await TextAsync(view.Browser));
        await FinishAsync(app, view);
    }

    // Requesting, confirming and recording

    [Fact]
    public async Task ControlOn_RequestsThePc_IsOnOnlyAfterItConfirms_AndIsAudited()
    {
        using var app = new TestApp();
        var view = await OpenViewAsync(app, name: "Lab-Desk-3");
        using var pc = await ConnectPcAsync(app, view);

        await ControlOnAsync(view, pc);

        var row = Assert.Single(await ControlRowsAsync(app));
        Assert.Equal(TestApp.Username, row.AdminUserName);
        Assert.Equal("Lab-Desk-3", row.PcName);
        Assert.Null(row.EndedUtc);

        // Frames keep flowing while control is on.
        await LiveViewClient.SendAsync(pc, LiveViewClient.Frame("during-control"));
        var frame = await LiveViewClient.ReceiveAsync(view.Browser);
        Assert.Equal("during-control", LiveViewClient.LabelOf(frame.Payload, 14));
        await FinishAsync(app, view);
    }

    [Fact]
    public async Task PcControlOn_WithoutARequest_IsIgnored()
    {
        using var app = new TestApp();
        var view = await OpenViewAsync(app);
        using var pc = await ConnectPcAsync(app, view);

        await LiveViewClient.SendTextAsync(pc, "control-on");
        await LiveViewClient.SendAsync(pc, LiveViewClient.Frame("after"));

        Assert.Equal("after", LiveViewClient.LabelOf((await LiveViewClient.ReceiveAsync(view.Browser)).Payload, 5));
        Assert.Empty(await ControlRowsAsync(app));
        await FinishAsync(app, view);
    }

    [Fact]
    public async Task ControlNotConfirmedWithinFiveSeconds_IsAbandonedAsUnsupported_AndThePcIsToldOff()
    {
        using var app = new TestApp();
        var view = await OpenViewAsync(app);
        using var pc = await ConnectPcAsync(app, view);
        await LiveViewClient.SendTextAsync(view.Browser, BrowserOn);
        Assert.Equal(Requesting, await TextAsync(view.Browser));
        Assert.Equal(PcOn, await TextAsync(pc));

        app.Time.Advance(TimeSpan.FromSeconds(5));

        Assert.Equal(OffMessage("unsupported"), await TextAsync(view.Browser));
        Assert.Equal(PcOff, await TextAsync(pc));
        Assert.Empty(await ControlRowsAsync(app));
        await FinishAsync(app, view);
    }

    // Input

    [Fact]
    public async Task Input_IsRelayedOnlyWhileOn_InCanonicalForm_AndMalformedInputIsDropped()
    {
        using var app = new TestApp();
        var view = await OpenViewAsync(app);
        using var pc = await ConnectPcAsync(app, view);
        await LiveViewClient.SendTextAsync(view.Browser, "{\"t\":\"move\",\"x\":1,\"y\":1}");
        await ControlOnAsync(view, pc);

        string[] malformed =
        [
            "{\"t\":\"move\",\"x\":-1,\"y\":0}",
            "{\"t\":\"move\",\"x\":65536,\"y\":0}",
            "{\"t\":\"key\",\"c\":\"Key A\",\"d\":true}",
            "{\"t\":\"down\",\"b\":5,\"x\":1,\"y\":1}",
            "{\"t\":\"zap\"}",
            "{\"t\":\"move\",\"x\":1,\"y\":2,\"z\":3}",
            "{\"t\":\"control\",\"on\":\"yes\"}",
            "not json",
        ];
        foreach (var json in malformed)
        {
            await LiveViewClient.SendTextAsync(view.Browser, json);
        }

        await LiveViewClient.SendAsync(view.Browser, "{\"t\":\"release\"}"u8.ToArray());
        await LiveViewClient.SendTextAsync(view.Browser, "{ \"y\": 20, \"x\": 10, \"t\": \"move\" }");

        // The first input the PC gets after on is the valid move, in canonical form. Nothing from before, or malformed, arrives.
        Assert.Equal("{\"t\":\"move\",\"x\":10,\"y\":20}", await TextAsync(pc));
        await FinishAsync(app, view);
    }

    [Fact]
    public async Task Input_OverTheRateCaps_IsDropped_AndTheExemptInputIsStillRelayed()
    {
        using var app = new TestApp();
        var view = await OpenViewAsync(app);
        using var pc = await ConnectPcAsync(app, view);
        await ControlOnAsync(view, pc);
        await LiveViewClient.SendTextAsync(view.Browser, "{\"t\":\"key\",\"c\":\"KeyA\",\"d\":true}");
        await LiveViewClient.SendTextAsync(view.Browser, "{\"t\":\"down\",\"b\":0,\"x\":1,\"y\":1}");

        for (var attempt = 0; attempt < ScreenProtocol.MaxMovesPerSecond + 10; attempt++)
        {
            await LiveViewClient.SendTextAsync(view.Browser, "{\"t\":\"move\",\"x\":1,\"y\":1}");
        }

        for (var attempt = 0; attempt < ScreenProtocol.MaxOtherInputPerSecond + 10; attempt++)
        {
            await LiveViewClient.SendTextAsync(view.Browser, "{\"t\":\"wheel\",\"dx\":0,\"dy\":120}");
        }

        await LiveViewClient.SendTextAsync(view.Browser, "{\"t\":\"key\",\"c\":\"KeyA\",\"d\":false}");
        await LiveViewClient.SendTextAsync(view.Browser, Up);

        var relayed = new List<string>();
        string text;
        do
        {
            text = await TextAsync(pc);
            relayed.Add(text);
        }
        while (text != Up);

        // The key press and the button press use two of the other-input allowance, so the wheel gets the rest.
        Assert.Equal(ScreenProtocol.MaxMovesPerSecond, relayed.Count(t => t.StartsWith("{\"t\":\"move\"", StringComparison.Ordinal)));
        Assert.Equal(ScreenProtocol.MaxOtherInputPerSecond - 2, relayed.Count(t => t.StartsWith("{\"t\":\"wheel\"", StringComparison.Ordinal)));
        Assert.Contains("{\"t\":\"key\",\"c\":\"KeyA\",\"d\":false}", relayed);
        await FinishAsync(app, view);
    }

    [Fact]
    public async Task ControlOn_IsAnsweredAtMostOncePerSecond_AndTheExcessIsIgnored()
    {
        using var app = new TestApp();
        var view = await OpenViewAsync(app);

        await LiveViewClient.SendTextAsync(view.Browser, BrowserOn);
        await LiveViewClient.SendTextAsync(view.Browser, BrowserOn);
        await LiveViewClient.SendTextAsync(view.Browser, BrowserOff);

        // The second request was ignored, so the first answer is followed directly by the stop answer.
        Assert.Equal(OffMessage("waiting"), await TextAsync(view.Browser));
        Assert.Equal(OffMessage("stopped"), await TextAsync(view.Browser));

        app.Time.Advance(TimeSpan.FromSeconds(1));
        await LiveViewClient.SendTextAsync(view.Browser, BrowserOn);
        Assert.Equal(OffMessage("waiting"), await TextAsync(view.Browser));
        await FinishAsync(app, view);
    }

    [Fact]
    public async Task Input_WithAMalformedSurrogateEscape_IsDropped_AndTheViewStaysOpen()
    {
        using var app = new TestApp();
        var view = await OpenViewAsync(app);
        using var pc = await ConnectPcAsync(app, view);
        await ControlOnAsync(view, pc);

        await LiveViewClient.SendTextAsync(view.Browser, "{\"t\":\"key\",\"c\":\"\\ud800\",\"d\":true}");
        await LiveViewClient.SendTextAsync(view.Browser, "{\"t\":\"move\",\"x\":3,\"y\":4}");

        Assert.Equal("{\"t\":\"move\",\"x\":3,\"y\":4}", await TextAsync(pc));
        await FinishAsync(app, view);
    }

    [Fact]
    public async Task ControlOn_WhenThePcsLiveAddressIsThisMachine_IsRefusedAsServerPc()
    {
        using var app = new TestApp();
        // The stored address is another machine's, but the live connection comes from this one.
        var view = await OpenViewAsync(app, pcIp: "192.168.1.50");
        using var pc = await ConnectPcAsync(app, view, TestApp.MachineLanAddress);

        await LiveViewClient.SendTextAsync(view.Browser, BrowserOn);

        Assert.Equal(OffMessage("server-pc"), await TextAsync(view.Browser));
        await FinishAsync(app, view);
    }

    [Fact]
    public async Task ControlOn_WhenTheStoredAddressIsMissing_IsRefusedAsServerPc()
    {
        using var app = new TestApp();
        var view = await OpenViewAsync(app);
        await app.WithDbAsync(db => db.Pcs.Where(p => p.Id == view.PcId).ExecuteUpdateAsync(s => s.SetProperty(p => p.IpAddress, (string?)null)));

        await LiveViewClient.SendTextAsync(view.Browser, BrowserOn);

        Assert.Equal(OffMessage("server-pc"), await TextAsync(view.Browser));
        await FinishAsync(app, view);
    }

    // Ending control

    [Fact]
    public async Task AdminStop_WhileOn_TellsTheBrowserStopped_TellsThePcOff_AndEndsTheAudit()
    {
        using var app = new TestApp();
        var view = await OpenViewAsync(app);
        using var pc = await ConnectPcAsync(app, view);
        await ControlOnAsync(view, pc);

        await LiveViewClient.SendTextAsync(view.Browser, BrowserOff);

        Assert.Equal(OffMessage("stopped"), await TextAsync(view.Browser));
        Assert.Equal(PcOff, await TextAsync(pc));
        var rows = await AwaitControlRowsAsync(app, rows => rows.All(r => r.EndedUtc is not null));
        Assert.NotNull(Assert.Single(rows).EndedUtc);
        await FinishAsync(app, view);
    }

    [Fact]
    public async Task AdminStop_WhileRequesting_IsAnsweredStopped_AndThePcIsToldOff()
    {
        using var app = new TestApp();
        var view = await OpenViewAsync(app);
        using var pc = await ConnectPcAsync(app, view);
        await LiveViewClient.SendTextAsync(view.Browser, BrowserOn);
        Assert.Equal(Requesting, await TextAsync(view.Browser));
        Assert.Equal(PcOn, await TextAsync(pc));

        await LiveViewClient.SendTextAsync(view.Browser, BrowserOff);

        Assert.Equal(OffMessage("stopped"), await TextAsync(view.Browser));
        Assert.Equal(PcOff, await TextAsync(pc));
        Assert.Empty(await ControlRowsAsync(app));
        await FinishAsync(app, view);
    }

    [Fact]
    public async Task AdminStop_WhenNoControlIsInProgress_IsStillAnsweredStopped()
    {
        using var app = new TestApp();
        var view = await OpenViewAsync(app);

        await LiveViewClient.SendTextAsync(view.Browser, BrowserOff);

        Assert.Equal(OffMessage("stopped"), await TextAsync(view.Browser));
        await FinishAsync(app, view);
    }

    [Fact]
    public async Task PcControlOff_WhileOn_TellsTheBrowserPcEnded_AndTheAuditEnds()
    {
        using var app = new TestApp();
        var view = await OpenViewAsync(app);
        using var pc = await ConnectPcAsync(app, view);
        await ControlOnAsync(view, pc);

        await LiveViewClient.SendTextAsync(pc, "control-off");

        Assert.Equal(OffMessage("pc-ended"), await TextAsync(view.Browser));
        Assert.Equal(PcOff, await TextAsync(pc));
        var rows = await AwaitControlRowsAsync(app, rows => rows.All(r => r.EndedUtc is not null));
        Assert.NotNull(Assert.Single(rows).EndedUtc);
        await FinishAsync(app, view);
    }

    [Fact]
    public async Task PcDisconnects_WhileOn_TellsTheBrowserConnection_ThenWaiting()
    {
        using var app = new TestApp();
        var view = await OpenViewAsync(app);
        using var pc = await ConnectPcAsync(app, view);
        await ControlOnAsync(view, pc);

        await pc.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);

        Assert.Equal(OffMessage("connection"), await TextAsync(view.Browser));
        Assert.Equal("{\"state\":\"waiting\"}", await TextAsync(view.Browser));
        var rows = await AwaitControlRowsAsync(app, rows => rows.All(r => r.EndedUtc is not null));
        Assert.NotNull(Assert.Single(rows).EndedUtc);
        await FinishAsync(app, view);
    }

    [Fact]
    public async Task PcReplacedWhileOn_TellsTheBrowserConnection_AndTheNewPcDoesNotHoldControl()
    {
        using var app = new TestApp();
        var view = await OpenViewAsync(app);
        using var first = await ConnectPcAsync(app, view);
        await ControlOnAsync(view, first);
        app.Time.Advance(TimeSpan.FromSeconds(1));

        using var second = await LiveViewClient.OpenPcAsync(app, view.Token, view.ViewId);

        Assert.Equal(OffMessage("connection"), await TextAsync(view.Browser));
        // The new connection changes no state, so the browser's next message is a frame from it, not control.
        await LiveViewClient.SendAsync(second, LiveViewClient.Frame("from-second"));
        Assert.Equal("from-second", LiveViewClient.LabelOf((await LiveViewClient.ReceiveAsync(view.Browser)).Payload, 11));
        var rows = await AwaitControlRowsAsync(app, rows => rows.All(r => r.EndedUtc is not null));
        Assert.NotNull(Assert.Single(rows).EndedUtc);
        await FinishAsync(app, view);
    }

    [Fact]
    public async Task ViewClosedWhileOn_EndsTheAudit()
    {
        using var app = new TestApp();
        var view = await OpenViewAsync(app);
        using var pc = await ConnectPcAsync(app, view);
        await ControlOnAsync(view, pc);

        await FinishAsync(app, view);

        var rows = await ControlRowsAsync(app);
        Assert.NotNull(Assert.Single(rows).EndedUtc);
    }
}
