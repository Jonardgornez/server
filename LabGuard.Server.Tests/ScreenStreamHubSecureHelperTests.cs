using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using LabGuard.Server.Screen;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace LabGuard.Server.Tests;

// The hub's ordering rules for the secure-desktop helper, with fake sockets. The end-to-end wire is in ScreenSecureHelperEndpointTests.
public class ScreenStreamHubSecureHelperTests
{
    private const string Stop = "{\"t\":\"stop\"}";
    private const string Release = "{\"t\":\"release\"}";
    private const string KeyDown = "{\"t\":\"key\",\"c\":\"KeyA\",\"d\":true}";

    private static readonly TimeSpan ShortTimeout = TimeSpan.FromMilliseconds(100);

    private static ScreenStreamHub NewHub(TestApp app) =>
        new(
            new ScreenViewAudit(app.Services.GetRequiredService<IServiceScopeFactory>(), NullLogger<ScreenViewAudit>.Instance),
            new ScreenControlAudit(app.Services.GetRequiredService<IServiceScopeFactory>(), NullLogger<ScreenControlAudit>.Instance),
            app.Time);

    private static IReadOnlyList<string> TextsOf(FakeWebSocket socket) =>
        socket.Sent.Where(message => message.Type == WebSocketMessageType.Text).Select(message => Encoding.UTF8.GetString(message.Payload)).ToArray();

    private static async Task<(ScreenStreamHub Hub, ScreenViewer Viewer, ScreenSocket Pc, FakeWebSocket PcSocket, SecureHelperConnection Helper, FakeWebSocket HelperSocket)> SetUpAsync(TestApp app)
    {
        var hub = NewHub(app);
        var viewer = (await hub.OpenAsync(1, ScreenProtocol.NotifiedMode, app.Time.GetUtcNow().AddHours(1), 0,
            new ScreenSocket(new FakeWebSocket(), ShortTimeout), hub.Epoch, new ScreenViewOwner(1, 1, "tester"), "PC-01"))!;
        var pcSocket = new FakeWebSocket();
        var pc = new ScreenSocket(pcSocket, ShortTimeout);
        await hub.AttachPcAsync(viewer, pc);
        var helperSocket = new FakeWebSocket();
        var helper = new SecureHelperConnection(1, "hash", new ScreenSocket(helperSocket, ShortTimeout));
        await hub.AttachHelperAsync(helper);
        return (hub, viewer, pc, pcSocket, helper, helperSocket);
    }

    private static async Task TurnControlOnAsync(ScreenStreamHub hub, ScreenViewer viewer, ScreenSocket pc)
    {
        await hub.RequestControlAsync(viewer, isServerPc: false);
        await hub.ConfirmControlAsync(viewer, pc);
        Assert.Equal(ScreenControlState.On, viewer.ControlState);
    }

    [Fact]
    public async Task InputRacingASwitch_NeverReachesThePcAfterItsRelease()
    {
        using var app = new TestApp();
        var (hub, viewer, pc, pcSocket, _, _) = await SetUpAsync(app);
        await TurnControlOnAsync(hub, viewer, pc);

        await viewer.InputGate.WaitAsync();
        var input = hub.RelayInputAsync(viewer, ScreenControlMessages.Parse(WebSocketMessageType.Text, Encoding.UTF8.GetBytes(KeyDown)));
        var pause = hub.SetPcPausedAsync(viewer, pc, isPaused: true);
        await Task.Delay(100);
        viewer.InputGate.Release();
        await Task.WhenAll(input, pause).WaitAsync(LiveViewClient.Patience);

        var texts = TextsOf(pcSocket);
        var released = Array.IndexOf(texts.ToArray(), Release);
        Assert.True(released >= 0);
        Assert.DoesNotContain(KeyDown, texts.Skip(released + 1));
    }

    [Fact]
    public async Task ClosingTheView_StopsAStreamingHelper_WithinTheBound_EvenWhileTheInputGateIsHeld()
    {
        using var app = new TestApp();
        var (hub, viewer, pc, _, _, helperSocket) = await SetUpAsync(app);
        await hub.SetPcPausedAsync(viewer, pc, isPaused: true);
        await viewer.InputGate.WaitAsync();

        var clock = Stopwatch.StartNew();
        await hub.CloseAsync(viewer, WebSocketCloseStatus.NormalClosure, "bye").WaitAsync(LiveViewClient.Patience);

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5));
        Assert.Equal(Stop, TextsOf(helperSocket).Last());
        viewer.InputGate.Release();
    }

    [Fact]
    public async Task ClosingTheView_WhileASwitchWaitsToStartTheHelper_LeavesTheHelperStopped()
    {
        using var app = new TestApp();
        var (hub, viewer, pc, _, _, helperSocket) = await SetUpAsync(app);

        await viewer.InputGate.WaitAsync();
        var pause = hub.SetPcPausedAsync(viewer, pc, isPaused: true);
        await Task.Delay(100);
        var close = hub.CloseAsync(viewer, WebSocketCloseStatus.NormalClosure, "bye");
        await Task.Delay(100);
        viewer.InputGate.Release();
        await Task.WhenAll(pause, close).WaitAsync(LiveViewClient.Patience);

        var texts = TextsOf(helperSocket);
        Assert.True(texts.Count == 0 || texts[^1] == Stop);
    }

    [Fact]
    public async Task AStoppedHelper_GetsAGraceForFramesOnTheWay_ThenItsFramesAreRefused()
    {
        using var app = new TestApp();
        var (hub, viewer, pc, _, helper, _) = await SetUpAsync(app);
        Assert.False(hub.IsHelperFrameAllowed(helper));
        await hub.SetPcPausedAsync(viewer, pc, isPaused: true);
        Assert.True(hub.IsHelperFrameAllowed(helper));

        await hub.SetPcPausedAsync(viewer, pc, isPaused: false);
        Assert.True(hub.IsHelperFrameAllowed(helper));
        app.Time.Advance(SecureHelperProtocol.StopGrace);

        Assert.False(hub.IsHelperFrameAllowed(helper));
    }

    [Fact]
    public async Task AStreamingHelper_IsRefusedOverTheFrameCap()
    {
        using var app = new TestApp();
        var (hub, viewer, pc, _, helper, _) = await SetUpAsync(app);
        await hub.SetPcPausedAsync(viewer, pc, isPaused: true);

        var allowed = Enumerable.Range(0, SecureHelperProtocol.MaxFramesPerSecond + 1).Count(_ => hub.IsHelperFrameAllowed(helper));
        app.Time.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal(SecureHelperProtocol.MaxFramesPerSecond, allowed);
        Assert.True(hub.IsHelperFrameAllowed(helper));
    }
}
