using System.Text;
using System.Text.Json;
using LabGuard.Server.Pcs;
using static LabGuard.Server.Tests.PcCancelPowerEndpointTests;
using static LabGuard.Server.Tests.PcShutdownEndpointTests;

namespace LabGuard.Server.Tests;

// What the PC's powerCountdownRunning flag changes. A PC that missed heartbeats while counting down keeps its delivery and
// cancel; a PC that is not counting down loses them, as before. Null, from an older client, changes nothing. No value the PC
// sends can start, extend or re-create a delivery.
public class PcCountdownRunningHeartbeatEndpointTests
{
    private const string RunningJson = "{\"powerCountdownRunning\":true}";

    private static bool PowerCancelledOf(JsonElement heartbeat) => heartbeat.GetProperty("powerCancelled").GetBoolean();

    // An empty body is sent as no body at all, which is what an older client does.
    private static Task<HttpResponseMessage> HeartbeatJsonAsync(HttpClient client, TestPc pc, string json) =>
        PostAsync(client, "/api/client/heartbeat", pc.Token, json.Length == 0 ? null : new StringContent(json, Encoding.UTF8, "application/json"));

    // Gap: the PC is offline, as if it missed heartbeats for 20 seconds, and then reports in again.

    [Fact]
    public async Task Gap_WithCountdownRunning_KeepsTheDeliveryAndTheCancel_AndTheNextHeartbeatStillReportsIt()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var client = app.CreatePcClient(OtherPcAddress);
        var pc = await AddPcAsync(app);
        await HeartbeatAsync(client, pc);
        await DeliverAsync(admin, client, pc, PcShutdownRules.ShutdownAction);
        await CancelAsync(admin, pc.Id);
        var delivered = (await StoredPcAsync(app, pc.Id)).ShutdownDeliveredAtUtc;
        app.Time.Advance(TimeSpan.FromSeconds(20));

        var gap = await JsonOf(await HeartbeatJsonAsync(client, pc, RunningJson));

        Assert.True(PowerCancelledOf(gap));
        var stored = await StoredPcAsync(app, pc.Id);
        Assert.Equal(delivered, stored.ShutdownDeliveredAtUtc);
        Assert.NotNull(stored.PowerCancelRequestedAtUtc);

        app.Time.Advance(TimeSpan.FromSeconds(2));
        var next = await JsonOf(await HeartbeatJsonAsync(client, pc, RunningJson));

        Assert.True(PowerCancelledOf(next));
        Assert.NotNull((await StoredPcAsync(app, pc.Id)).PowerCancelRequestedAtUtc);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("{\"powerCountdownRunning\":null}")]
    [InlineData("{\"powerCountdownRunning\":false}")]
    public async Task Gap_WithoutCountdownRunning_ClearsTheDeliveryAndTheCancel(string json)
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var client = app.CreatePcClient(OtherPcAddress);
        var pc = await AddPcAsync(app);
        await HeartbeatAsync(client, pc);
        await DeliverAsync(admin, client, pc, PcShutdownRules.ShutdownAction);
        await CancelAsync(admin, pc.Id);
        app.Time.Advance(TimeSpan.FromSeconds(20));

        var back = await JsonOf(await HeartbeatJsonAsync(client, pc, json));

        Assert.False(PowerCancelledOf(back));
        var stored = await StoredPcAsync(app, pc.Id);
        Assert.Null(stored.ShutdownDeliveredAtUtc);
        Assert.Null(stored.PowerCancelRequestedAtUtc);
    }

    [Fact]
    public async Task Gap_WithCountdownRunning_DropsAPendingRequest()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var client = app.CreatePcClient(OtherPcAddress);
        var pc = await AddPcAsync(app);
        await HeartbeatAsync(client, pc);
        await ShutdownAsync(admin, pc.Id);
        app.Time.Advance(TimeSpan.FromSeconds(20));

        await HeartbeatJsonAsync(client, pc, RunningJson);

        var stored = await StoredPcAsync(app, pc.Id);
        Assert.Null(stored.ShutdownRequestedAtUtc);
        Assert.Null(stored.ShutdownDeliveredAtUtc);
    }

    [Fact]
    public async Task Gap_WithCountdownRunning_PastTheDeliveryWindow_ForgetsTheDelivery()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var client = app.CreatePcClient(OtherPcAddress);
        var pc = await AddPcAsync(app);
        await HeartbeatAsync(client, pc);
        await DeliverAsync(admin, client, pc, PcShutdownRules.ShutdownAction);
        await CancelAsync(admin, pc.Id);
        app.Time.Advance(TimeSpan.FromSeconds(61));

        var back = await JsonOf(await HeartbeatJsonAsync(client, pc, RunningJson));

        Assert.False(PowerCancelledOf(back));
        var stored = await StoredPcAsync(app, pc.Id);
        Assert.Null(stored.ShutdownDeliveredAtUtc);
        Assert.Null(stored.PowerCancelRequestedAtUtc);
    }

    // Online: the flag never creates or extends anything.

    [Fact]
    public async Task CountdownRunning_WithNoDelivery_CreatesNothing()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient(OtherPcAddress);
        var pc = await AddPcAsync(app);
        await HeartbeatAsync(client, pc);

        var body = await JsonOf(await HeartbeatJsonAsync(client, pc, RunningJson));

        Assert.Null(ShutdownSecondsOf(body));
        Assert.False(PowerCancelledOf(body));
        var stored = await StoredPcAsync(app, pc.Id);
        Assert.Null(stored.ShutdownDeliveredAtUtc);
        Assert.Null(stored.ShutdownRequestedAtUtc);
    }

    [Fact]
    public async Task CountdownRunning_WhileOffline_CreatesNothing()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient(OtherPcAddress);
        var pc = await AddPcAsync(app);
        await HeartbeatAsync(client, pc);
        app.Time.Advance(TimeSpan.FromSeconds(20));

        var body = await JsonOf(await HeartbeatJsonAsync(client, pc, RunningJson));

        Assert.Null(ShutdownSecondsOf(body));
        var stored = await StoredPcAsync(app, pc.Id);
        Assert.Null(stored.ShutdownDeliveredAtUtc);
        Assert.Null(stored.ShutdownRequestedAtUtc);
    }

    [Fact]
    public async Task CountdownRunning_DoesNotRefreshAWindowInProgress()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var client = app.CreatePcClient(OtherPcAddress);
        var pc = await AddPcAsync(app);
        await HeartbeatAsync(client, pc);
        await DeliverAsync(admin, client, pc, PcShutdownRules.ShutdownAction);
        var delivered = (await StoredPcAsync(app, pc.Id)).ShutdownDeliveredAtUtc;
        app.Time.Advance(TimeSpan.FromSeconds(30));
        await BringOnlineAsync(app, pc.Id);

        await HeartbeatJsonAsync(client, pc, RunningJson);

        Assert.Equal(delivered, (await StoredPcAsync(app, pc.Id)).ShutdownDeliveredAtUtc);
    }

    [Fact]
    public async Task CountdownRunning_OnAnOnlinePc_ForgetsADeliveryPastItsWindow()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var client = app.CreatePcClient(OtherPcAddress);
        var pc = await AddPcAsync(app);
        await HeartbeatAsync(client, pc);
        await DeliverAsync(admin, client, pc, PcShutdownRules.ShutdownAction);
        app.Time.Advance(TimeSpan.FromSeconds(61));
        await BringOnlineAsync(app, pc.Id);

        await HeartbeatJsonAsync(client, pc, RunningJson);

        Assert.Null((await StoredPcAsync(app, pc.Id)).ShutdownDeliveredAtUtc);
    }
}
