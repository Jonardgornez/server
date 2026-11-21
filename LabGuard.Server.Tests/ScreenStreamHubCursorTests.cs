using System.Net.WebSockets;
using System.Text;
using LabGuard.Server.Screen;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace LabGuard.Server.Tests;

// The PC's pointer shape, relayed to the browser only while control is on and carried by the PC, with fake sockets and the fake clock.
public class ScreenStreamHubCursorTests
{
    private const string Text = "{\"cursor\":\"text\"}";
    private const string Move = "{\"cursor\":\"move\"}";
    private const string Default = "{\"cursor\":\"default\"}";

    private static readonly TimeSpan ShortTimeout = TimeSpan.FromMilliseconds(100);

    private static ScreenStreamHub NewHub(TestApp app) =>
        new(
            new ScreenViewAudit(app.Services.GetRequiredService<IServiceScopeFactory>(), NullLogger<ScreenViewAudit>.Instance),
            new ScreenControlAudit(app.Services.GetRequiredService<IServiceScopeFactory>(), NullLogger<ScreenControlAudit>.Instance),
            app.Time);

    private static IReadOnlyList<string> TextsOf(FakeWebSocket socket) =>
        socket.Sent.Where(message => message.Type == WebSocketMessageType.Text).Select(message => Encoding.UTF8.GetString(message.Payload)).ToArray();

    private static IReadOnlyList<string> CursorsOf(FakeWebSocket socket) =>
        TextsOf(socket).Where(text => text.StartsWith("{\"cursor\":", StringComparison.Ordinal)).ToArray();

    private static byte[] Cursor(string message) => Encoding.UTF8.GetBytes(message);

    private static async Task<(ScreenStreamHub Hub, ScreenViewer Viewer, FakeWebSocket Browser, ScreenSocket Pc)> SetUpAsync(TestApp app)
    {
        var hub = NewHub(app);
        var browser = new FakeWebSocket();
        var viewer = (await hub.OpenAsync(1, ScreenProtocol.NotifiedMode, app.Time.GetUtcNow().AddHours(1), 0,
            new ScreenSocket(browser, ShortTimeout), hub.Epoch, new ScreenViewOwner(1, 1, "tester"), "PC-01"))!;
        var pc = new ScreenSocket(new FakeWebSocket(), ShortTimeout);
        await hub.AttachPcAsync(viewer.Stream, pc, viewer.Stream.ViewId);
        return (hub, viewer, browser, pc);
    }

    private static async Task TurnControlOnAsync(ScreenStreamHub hub, ScreenViewer viewer, ScreenSocket source)
    {
        await hub.RequestControlAsync(viewer, isServerPc: false);
        await hub.ConfirmControlAsync(viewer.Stream, source);
        Assert.Equal(ScreenControlState.On, viewer.ControlState);
    }

    private static async Task<SecureHelperConnection> AttachHelperAsync(ScreenStreamHub hub)
    {
        var helper = new SecureHelperConnection(1, "hash", new ScreenSocket(new FakeWebSocket(), ShortTimeout));
        await hub.AttachHelperAsync(helper);
        return helper;
    }

    [Fact]
    public async Task Cursor_WhileControlIsOn_IsRelayedToTheBrowser()
    {
        using var app = new TestApp();
        var (hub, viewer, browser, pc) = await SetUpAsync(app);
        await TurnControlOnAsync(hub, viewer, pc);

        await hub.RelayCursorAsync(viewer.Stream, pc, Cursor(Text));

        Assert.Equal(Text, TextsOf(browser).Last());
    }

    [Fact]
    public async Task Cursor_WhileControlIsOff_IsDropped()
    {
        using var app = new TestApp();
        var (hub, viewer, browser, pc) = await SetUpAsync(app);

        await hub.RelayCursorAsync(viewer.Stream, pc, Cursor(Text));

        Assert.Empty(CursorsOf(browser));
    }

    [Fact]
    public async Task Cursor_WhileControlIsRequested_IsDropped()
    {
        using var app = new TestApp();
        var (hub, viewer, browser, pc) = await SetUpAsync(app);
        await hub.RequestControlAsync(viewer, isServerPc: false);

        await hub.RelayCursorAsync(viewer.Stream, pc, Cursor(Text));

        Assert.Empty(CursorsOf(browser));
    }

    [Fact]
    public async Task Cursor_AfterControlEnded_IsDropped()
    {
        using var app = new TestApp();
        var (hub, viewer, browser, pc) = await SetUpAsync(app);
        await TurnControlOnAsync(hub, viewer, pc);
        await hub.StopControlAsync(viewer);

        await hub.RelayCursorAsync(viewer.Stream, pc, Cursor(Text));

        Assert.Empty(CursorsOf(browser));
    }

    [Fact]
    public async Task Cursor_FromAPausedPc_WhileTheHelperCarriesControl_IsDropped()
    {
        using var app = new TestApp();
        var (hub, viewer, browser, pc) = await SetUpAsync(app);
        await AttachHelperAsync(hub);
        await TurnControlOnAsync(hub, viewer, pc);
        await hub.SetPcPausedAsync(viewer.Stream, pc, isPaused: true);

        await hub.RelayCursorAsync(viewer.Stream, pc, Cursor(Text));

        Assert.Empty(CursorsOf(browser));
    }

    [Fact]
    public async Task Cursor_FromTheHelperSocket_IsDropped()
    {
        using var app = new TestApp();
        var (hub, viewer, browser, pc) = await SetUpAsync(app);
        var helper = await AttachHelperAsync(hub);
        await hub.SetPcPausedAsync(viewer.Stream, pc, isPaused: true);
        await TurnControlOnAsync(hub, viewer, helper.Socket);

        await hub.RelayCursorAsync(viewer.Stream, helper.Socket, Cursor(Text));

        Assert.Empty(CursorsOf(browser));
    }

    [Fact]
    public async Task Cursor_FromAReplacedPcSocket_IsDropped()
    {
        using var app = new TestApp();
        var (hub, viewer, browser, pc) = await SetUpAsync(app);
        await TurnControlOnAsync(hub, viewer, pc);

        await hub.RelayCursorAsync(viewer.Stream, new ScreenSocket(new FakeWebSocket(), ShortTimeout), Cursor(Text));

        Assert.Empty(CursorsOf(browser));
    }

    [Fact]
    public async Task Cursor_After20InOneSecond_IsDropped_UntilTheSecondHasPassed()
    {
        using var app = new TestApp();
        var (hub, viewer, browser, pc) = await SetUpAsync(app);
        await TurnControlOnAsync(hub, viewer, pc);

        for (var i = 0; i < ScreenProtocol.MaxCursorsPerSecond; i++)
        {
            await hub.RelayCursorAsync(viewer.Stream, pc, Cursor(Text));
        }

        await hub.RelayCursorAsync(viewer.Stream, pc, Cursor(Move));
        Assert.Equal(20, CursorsOf(browser).Count);
        Assert.DoesNotContain(Move, CursorsOf(browser));

        app.Time.Advance(TimeSpan.FromSeconds(1));
        await hub.RelayCursorAsync(viewer.Stream, pc, Cursor(Move));

        Assert.Equal(Move, CursorsOf(browser).Last());
    }

    [Fact]
    public async Task ControlOff_AfterACursorWasShown_SendsTheDefault_BeforeControlOff()
    {
        using var app = new TestApp();
        var (hub, viewer, browser, pc) = await SetUpAsync(app);
        await TurnControlOnAsync(hub, viewer, pc);
        await hub.RelayCursorAsync(viewer.Stream, pc, Cursor(Text));

        await hub.StopControlAsync(viewer);

        var texts = TextsOf(browser);
        Assert.Equal(Default, texts[^2]);
        Assert.Equal("{\"control\":\"off\",\"reason\":\"stopped\"}", texts[^1]);
    }

    [Fact]
    public async Task ControlOff_FromThePc_AfterACursorWasShown_SendsTheDefault()
    {
        using var app = new TestApp();
        var (hub, viewer, browser, pc) = await SetUpAsync(app);
        await TurnControlOnAsync(hub, viewer, pc);
        await hub.RelayCursorAsync(viewer.Stream, pc, Cursor(Text));

        await hub.EndControlFromAsync(viewer.Stream, pc);

        Assert.Equal(Default, CursorsOf(browser).Last());
    }

    [Fact]
    public async Task ControlOff_WithNoCursorShown_SendsNoCursor()
    {
        using var app = new TestApp();
        var (hub, viewer, browser, pc) = await SetUpAsync(app);
        await TurnControlOnAsync(hub, viewer, pc);

        await hub.StopControlAsync(viewer);

        Assert.Empty(CursorsOf(browser));
    }

    [Fact]
    public async Task SwitchToTheHelper_AfterACursorWasShown_SendsTheDefault_AndControlCarriesOn()
    {
        using var app = new TestApp();
        var (hub, viewer, browser, pc) = await SetUpAsync(app);
        await AttachHelperAsync(hub);
        await TurnControlOnAsync(hub, viewer, pc);
        await hub.RelayCursorAsync(viewer.Stream, pc, Cursor(Text));

        await hub.SetPcPausedAsync(viewer.Stream, pc, isPaused: true);

        Assert.Equal([Text, Default], CursorsOf(browser));
        Assert.Equal(ScreenControlState.On, viewer.ControlState);
    }

    [Fact]
    public async Task SwitchBackToThePc_RelaysItsCursorsAgain()
    {
        using var app = new TestApp();
        var (hub, viewer, browser, pc) = await SetUpAsync(app);
        await AttachHelperAsync(hub);
        await TurnControlOnAsync(hub, viewer, pc);
        await hub.SetPcPausedAsync(viewer.Stream, pc, isPaused: true);
        await hub.SetPcPausedAsync(viewer.Stream, pc, isPaused: false);

        await hub.RelayCursorAsync(viewer.Stream, pc, Cursor(Move));

        Assert.Equal([Move], CursorsOf(browser));
    }

    [Fact]
    public async Task ControlEndedBySwitch_AfterACursorWasShown_SendsTheDefault()
    {
        using var app = new TestApp();
        var (hub, viewer, browser, pc) = await SetUpAsync(app);
        await TurnControlOnAsync(hub, viewer, pc);
        await hub.RelayCursorAsync(viewer.Stream, pc, Cursor(Text));

        await hub.DetachPcAsync(viewer.Stream, pc);

        Assert.Equal(ScreenControlState.Off, viewer.ControlState);
        Assert.Equal([Text, Default], CursorsOf(browser));
    }
}
