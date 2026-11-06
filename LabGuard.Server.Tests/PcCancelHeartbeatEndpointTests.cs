using System.Net;
using System.Text;
using System.Text.Json;
using LabGuard.Server.Pcs;
using static LabGuard.Server.Tests.PcCancelPowerEndpointTests;
using static LabGuard.Server.Tests.PcShutdownEndpointTests;

namespace LabGuard.Server.Tests;

// The heartbeat's side of a cancel. The PC is told about a recorded cancel on every heartbeat until it acknowledges it, and the
// acknowledgement clears the cancel with its delivery. An acknowledgement with no cancel recorded changes nothing.
public class PcCancelHeartbeatEndpointTests
{
    private static bool PowerCancelledOf(JsonElement heartbeat) => heartbeat.GetProperty("powerCancelled").GetBoolean();

    private static Task<HttpResponseMessage> HeartbeatWithBodyAsync(HttpClient client, TestPc pc, string body) =>
        PostAsync(client, "/api/client/heartbeat", pc.Token, new StringContent(body, Encoding.UTF8, "application/json"));

    [Fact]
    public async Task Heartbeat_WhileACancelIsRecorded_ReportsPowerCancelled_UntilAcknowledged()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var client = app.CreatePcClient(OtherPcAddress);
        var pc = await AddPcAsync(app);
        await HeartbeatAsync(client, pc);
        await DeliverAsync(admin, client, pc, PcShutdownRules.ShutdownAction);
        await CancelAsync(admin, pc.Id);

        var first = await HeartbeatBodyAsync(client, pc);
        var second = await HeartbeatBodyAsync(client, pc);

        Assert.True(PowerCancelledOf(first));
        Assert.True(PowerCancelledOf(second));
        Assert.Null(ShutdownSecondsOf(second));
        Assert.NotNull((await StoredPcAsync(app, pc.Id)).PowerCancelRequestedAtUtc);
    }

    [Fact]
    public async Task Heartbeat_WithoutACancel_ReportsPowerCancelledFalse()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var client = app.CreatePcClient(OtherPcAddress);
        var pc = await AddPcAsync(app);
        await HeartbeatAsync(client, pc);
        Assert.False(PowerCancelledOf(await HeartbeatBodyAsync(client, pc)));
        await ShutdownAsync(admin, pc.Id);

        var delivered = await HeartbeatBodyAsync(client, pc);

        Assert.Equal(30, ShutdownSecondsOf(delivered));
        Assert.False(PowerCancelledOf(delivered));
    }

    [Theory]
    [InlineData(PcShutdownRules.ShutdownAction)]
    [InlineData(PcShutdownRules.RestartAction)]
    public async Task Heartbeat_AcknowledgingTheCancel_ClearsTheCancelAndTheDelivery_AndReportsFalse(string action)
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var client = app.CreatePcClient(OtherPcAddress);
        var pc = await AddPcAsync(app);
        await HeartbeatAsync(client, pc);
        await DeliverAsync(admin, client, pc, action);
        await CancelAsync(admin, pc.Id);

        var acknowledged = await HeartbeatWithBodyAsync(client, pc, "{\"powerCancelHandled\":true}");

        Assert.False(PowerCancelledOf(await JsonOf(acknowledged)));
        var stored = await StoredPcAsync(app, pc.Id);
        Assert.Null(stored.ShutdownDeliveredAtUtc);
        Assert.Null(stored.PowerCancelRequestedAtUtc);

        var later = await HeartbeatBodyAsync(client, pc);
        Assert.False(PowerCancelledOf(later));
        Assert.Equal(JsonValueKind.Null, later.GetProperty("shutdownSeconds").ValueKind);
        Assert.Equal(JsonValueKind.Null, later.GetProperty("restartSeconds").ValueKind);
        Assert.Equal(JsonValueKind.Null, (await ListEntryAsync(admin, "PC-01")).GetProperty("shutdownState").ValueKind);
    }

    [Fact]
    public async Task Heartbeat_AfterAcknowledgedCancel_LetsTheAdminRequestAgain()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var client = app.CreatePcClient(OtherPcAddress);
        var pc = await AddPcAsync(app);
        await HeartbeatAsync(client, pc);
        await DeliverAsync(admin, client, pc, PcShutdownRules.ShutdownAction);
        await CancelAsync(admin, pc.Id);
        await HeartbeatWithBodyAsync(client, pc, "{\"powerCancelHandled\":true}");

        Assert.Equal(HttpStatusCode.NoContent, (await ShutdownAsync(admin, pc.Id)).StatusCode);

        Assert.NotNull((await StoredPcAsync(app, pc.Id)).ShutdownRequestedAtUtc);
    }

    [Theory]
    [InlineData("{\"powerCancelHandled\":false}")]
    [InlineData("{}")]
    [InlineData("{not json")]
    [InlineData("")]
    public async Task Heartbeat_WithoutAnAcknowledgement_LeavesTheCancelInPlace(string body)
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var client = app.CreatePcClient(OtherPcAddress);
        var pc = await AddPcAsync(app);
        await HeartbeatAsync(client, pc);
        await DeliverAsync(admin, client, pc, PcShutdownRules.ShutdownAction);
        await CancelAsync(admin, pc.Id);

        var reply = await JsonOf(await HeartbeatWithBodyAsync(client, pc, body));

        Assert.True(PowerCancelledOf(reply));
        var stored = await StoredPcAsync(app, pc.Id);
        Assert.NotNull(stored.ShutdownDeliveredAtUtc);
        Assert.NotNull(stored.PowerCancelRequestedAtUtc);
    }

    [Fact]
    public async Task Heartbeat_AcknowledgementWithNoCancel_ChangesNothing()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var client = app.CreatePcClient(OtherPcAddress);
        var pc = await AddPcAsync(app);
        await HeartbeatAsync(client, pc);
        await DeliverAsync(admin, client, pc, PcShutdownRules.ShutdownAction);
        var delivered = (await StoredPcAsync(app, pc.Id)).ShutdownDeliveredAtUtc;

        var reply = await JsonOf(await HeartbeatWithBodyAsync(client, pc, "{\"powerCancelHandled\":true}"));

        Assert.False(PowerCancelledOf(reply));
        Assert.Null(ShutdownSecondsOf(reply));
        var stored = await StoredPcAsync(app, pc.Id);
        Assert.Equal(delivered, stored.ShutdownDeliveredAtUtc);
        Assert.Null(stored.PowerCancelRequestedAtUtc);
        Assert.Equal(PcShutdownRules.ShuttingDownState, (await ListEntryAsync(admin, "PC-01")).GetProperty("shutdownState").GetString());
    }

    [Fact]
    public async Task Heartbeat_ComingBackFromOffline_ReportsNoCancel()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var client = app.CreatePcClient(OtherPcAddress);
        var pc = await AddPcAsync(app);
        await HeartbeatAsync(client, pc);
        await DeliverAsync(admin, client, pc, PcShutdownRules.ShutdownAction);
        await CancelAsync(admin, pc.Id);
        app.Time.Advance(TimeSpan.FromSeconds(20));

        var back = await HeartbeatBodyAsync(client, pc);

        Assert.False(PowerCancelledOf(back));
        var stored = await StoredPcAsync(app, pc.Id);
        Assert.Null(stored.ShutdownDeliveredAtUtc);
        Assert.Null(stored.PowerCancelRequestedAtUtc);
    }
}
