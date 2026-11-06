using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using LabGuard.Server.Pcs;
using LabGuard.Server.Sessions;
using Microsoft.EntityFrameworkCore;
using static LabGuard.Server.Tests.PcShutdownEndpointTests;

namespace LabGuard.Server.Tests;

// The restart side of the shutdown pipeline. A restart shares every rule with a shutdown, so these tests cover what
// differs: the reboot routes, the restartSeconds field, the states and the session end reason. Shared helpers come from
// PcShutdownEndpointTests.
public class PcRebootEndpointTests
{
    private const string RestartServerPcMessage = "This PC runs the server. Restart it by hand.";

    private static Task<HttpResponseMessage> RebootAsync(HttpClient admin, int pcId) =>
        admin.PostAsJsonAsync($"/api/pcs/{pcId}/reboot", new { });

    private static Task<HttpResponseMessage> RebootManyAsync(HttpClient admin, params int[] ids) =>
        admin.PostAsJsonAsync("/api/pcs/reboot", new { ids });

    // The single route for an action: shutdown or reboot.
    private static Task<HttpResponseMessage> PowerAsync(HttpClient admin, string action, int pcId) =>
        action == PcShutdownRules.RestartAction ? RebootAsync(admin, pcId) : ShutdownAsync(admin, pcId);

    private static int? RestartSecondsOf(JsonElement heartbeat) =>
        heartbeat.GetProperty("restartSeconds").ValueKind == JsonValueKind.Null
            ? null
            : heartbeat.GetProperty("restartSeconds").GetInt32();

    // Only the field of the delivered action carries the seconds. The other is null, so an older client that reads only
    // shutdownSeconds never shuts down for a restart.
    private static void AssertDeliveredOnly(JsonElement heartbeat, string action)
    {
        int? expectedShutdown = action == PcShutdownRules.ShutdownAction ? 30 : null;
        int? expectedRestart = action == PcShutdownRules.RestartAction ? 30 : null;
        Assert.Equal(expectedShutdown, ShutdownSecondsOf(heartbeat));
        Assert.Equal(expectedRestart, RestartSecondsOf(heartbeat));
    }

    private static void AssertNothingDelivered(JsonElement heartbeat)
    {
        Assert.Null(ShutdownSecondsOf(heartbeat));
        Assert.Null(RestartSecondsOf(heartbeat));
    }

    // Routes

    [Theory]
    [InlineData("/api/pcs/1/reboot")]
    [InlineData("/api/pcs/reboot")]
    public async Task RebootRoutes_WithoutCookie_Return401(string path)
    {
        using var app = new TestApp();
        using var browser = app.CreateBrowser();

        var response = await browser.PostAsJsonAsync(path, new { });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("/api/pcs/1/reboot")]
    [InlineData("/api/pcs/reboot")]
    public async Task RebootRoutes_FromThePcPort_Return404(string path)
    {
        using var app = new TestApp();
        using var pcClient = app.CreatePcClient(OtherPcAddress);
        await AddPcAsync(app);

        var response = await pcClient.PostAsJsonAsync(path, new { });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("/api/pcs/{id}/reboot", "text/plain", "{}")]
    [InlineData("/api/pcs/{id}/reboot", "application/json", "")]
    [InlineData("/api/pcs/{id}/reboot", "application/json", "null")]
    [InlineData("/api/pcs/reboot", "application/x-www-form-urlencoded", "")]
    [InlineData("/api/pcs/reboot", "application/json", "{not json")]
    public async Task RebootRoutes_WithoutAJsonBody_Return400AndChangeNothing(string path, string contentType, string body)
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var client = app.CreatePcClient(OtherPcAddress);
        var pc = await AddPcAsync(app);
        await HeartbeatAsync(client, pc);

        using var content = new StringContent(body, Encoding.UTF8, contentType);
        var response = await admin.PostAsync(path.Replace("{id}", pc.Id.ToString()), content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(InvalidRequestMessage, (await JsonOf(response)).GetProperty("error").GetString());
        Assert.Null((await StoredPcAsync(app, pc.Id)).ShutdownRequestedAtUtc);
    }

    [Theory]
    [InlineData(PcShutdownRules.ShutdownAction)]
    [InlineData(PcShutdownRules.RestartAction)]
    public async Task Request_StoresTheActionOfItsRoute(string action)
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var client = app.CreatePcClient(OtherPcAddress);
        var pc = await AddPcAsync(app);
        await HeartbeatAsync(client, pc);

        Assert.Equal(HttpStatusCode.NoContent, (await PowerAsync(admin, action, pc.Id)).StatusCode);

        Assert.Equal(action, (await StoredPcAsync(app, pc.Id)).PowerAction);
    }

    // Single reboot

    [Fact]
    public async Task Reboot_OnlinePc_Returns204AndRequestsARestart()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var client = app.CreatePcClient(OtherPcAddress);
        var pc = await AddPcAsync(app);
        await HeartbeatAsync(client, pc);

        var response = await RebootAsync(admin, pc.Id);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var stored = await StoredPcAsync(app, pc.Id);
        Assert.Equal(app.Time.GetUtcNow().ToUnixTimeMilliseconds(), stored.ShutdownRequestedAtUtc!.Value.ToUnixTimeMilliseconds());
        Assert.Equal(PcShutdownRules.RestartAction, stored.PowerAction);
        Assert.Null(stored.ShutdownDeliveredAtUtc);
    }

    [Fact]
    public async Task Reboot_MissingPc_Returns404()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);

        var response = await RebootAsync(admin, 999);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("PC not found.", (await JsonOf(response)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task Reboot_OfflinePc_Returns409AndChangesNothing()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var client = app.CreatePcClient(OtherPcAddress);
        var pc = await AddPcAsync(app);
        await HeartbeatAsync(client, pc);
        app.Time.Advance(TimeSpan.FromMinutes(1));

        var response = await RebootAsync(admin, pc.Id);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(OfflineMessage, (await JsonOf(response)).GetProperty("error").GetString());
        Assert.Null((await StoredPcAsync(app, pc.Id)).ShutdownRequestedAtUtc);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData(TestApp.MachineLanAddress)]
    public async Task Reboot_ServerPc_Returns409WithTheRestartMessageAndChangesNothing(string address)
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var client = app.CreatePcClient(address);
        var pc = await AddPcAsync(app);
        await HeartbeatAsync(client, pc);

        var response = await RebootAsync(admin, pc.Id);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(RestartServerPcMessage, (await JsonOf(response)).GetProperty("error").GetString());
        Assert.Null((await StoredPcAsync(app, pc.Id)).ShutdownRequestedAtUtc);
    }

    [Fact]
    public async Task Reboot_WhenTheAddressesCannotBeRead_Returns409AndChangesNothing()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var client = app.CreatePcClient(OtherPcAddress);
        var pc = await AddPcAsync(app);
        await HeartbeatAsync(client, pc);
        app.LocalAddresses.Fail = true;

        var response = await RebootAsync(admin, pc.Id);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(AddressUnreadableMessage, (await JsonOf(response)).GetProperty("error").GetString());
        Assert.Null((await StoredPcAsync(app, pc.Id)).ShutdownRequestedAtUtc);
    }

    [Fact]
    public async Task Reboot_WithNoLocalAddresses_IsNotTreatedAsAFailedRead()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var client = app.CreatePcClient(OtherPcAddress);
        var pc = await AddPcAsync(app);
        await HeartbeatAsync(client, pc);
        app.LocalAddresses.SetAddresses([]);

        var response = await RebootAsync(admin, pc.Id);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(PcShutdownRules.RestartAction, (await StoredPcAsync(app, pc.Id)).PowerAction);
    }

    // A pending or recently delivered request keeps its first action.

    [Fact]
    public async Task Reboot_AlreadyPendingShutdown_Returns409AndKeepsTheShutdown()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var client = app.CreatePcClient(OtherPcAddress);
        var pc = await AddPcAsync(app);
        await HeartbeatAsync(client, pc);
        await ShutdownAsync(admin, pc.Id);
        var before = await StoredPcAsync(app, pc.Id);
        app.Time.Advance(TimeSpan.FromSeconds(10));
        await BringOnlineAsync(app, pc.Id);

        var response = await RebootAsync(admin, pc.Id);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var stored = await StoredPcAsync(app, pc.Id);
        Assert.Equal(before.ShutdownRequestedAtUtc, stored.ShutdownRequestedAtUtc);
        Assert.Equal(PcShutdownRules.ShutdownAction, stored.PowerAction);
    }

    [Fact]
    public async Task Shutdown_AlreadyPendingReboot_Returns409AndKeepsTheRestart()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var client = app.CreatePcClient(OtherPcAddress);
        var pc = await AddPcAsync(app);
        await HeartbeatAsync(client, pc);
        await RebootAsync(admin, pc.Id);
        var before = await StoredPcAsync(app, pc.Id);
        app.Time.Advance(TimeSpan.FromSeconds(10));
        await BringOnlineAsync(app, pc.Id);

        var response = await ShutdownAsync(admin, pc.Id);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var stored = await StoredPcAsync(app, pc.Id);
        Assert.Equal(before.ShutdownRequestedAtUtc, stored.ShutdownRequestedAtUtc);
        Assert.Equal(PcShutdownRules.RestartAction, stored.PowerAction);
    }

    [Fact]
    public async Task Reboot_WithinTheDeliveryWindowOfAShutdown_Returns409AndStoresNothing()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var client = app.CreatePcClient(OtherPcAddress);
        var pc = await AddPcAsync(app);
        await HeartbeatAsync(client, pc);
        await ShutdownAsync(admin, pc.Id);
        AssertDeliveredOnly(await HeartbeatBodyAsync(client, pc), PcShutdownRules.ShutdownAction);
        var delivered = (await StoredPcAsync(app, pc.Id)).ShutdownDeliveredAtUtc;
        app.Time.Advance(TimeSpan.FromSeconds(10));
        await BringOnlineAsync(app, pc.Id);

        var response = await RebootAsync(admin, pc.Id);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var stored = await StoredPcAsync(app, pc.Id);
        Assert.Null(stored.ShutdownRequestedAtUtc);
        Assert.Equal(delivered, stored.ShutdownDeliveredAtUtc);
        Assert.Equal(PcShutdownRules.ShutdownAction, stored.PowerAction);
    }

    [Fact]
    public async Task Reboot_AfterTheDeliveryWindowOfAShutdown_StoresARestartAndForgetsTheShutdown()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var client = app.CreatePcClient(OtherPcAddress);
        var pc = await AddPcAsync(app);
        await HeartbeatAsync(client, pc);
        await ShutdownAsync(admin, pc.Id);
        await HeartbeatAsync(client, pc);
        app.Time.Advance(TimeSpan.FromSeconds(60));
        await BringOnlineAsync(app, pc.Id);

        Assert.Equal(HttpStatusCode.NoContent, (await RebootAsync(admin, pc.Id)).StatusCode);

        var stored = await StoredPcAsync(app, pc.Id);
        Assert.NotNull(stored.ShutdownRequestedAtUtc);
        Assert.Null(stored.ShutdownDeliveredAtUtc);
        Assert.Equal(PcShutdownRules.RestartAction, stored.PowerAction);
    }

    // Bulk reboot

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"ids\":null}")]
    [InlineData("{\"ids\":[]}")]
    [InlineData("{\"ids\":[\"one\"]}")]
    public async Task RebootMany_WithAMissingOrEmptyList_Return400AndChangesNothing(string body)
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        var pc = await AddPcAsync(app);

        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        var response = await admin.PostAsync("/api/pcs/reboot", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(InvalidRequestMessage, (await JsonOf(response)).GetProperty("error").GetString());
        Assert.Null((await StoredPcAsync(app, pc.Id)).ShutdownRequestedAtUtc);
    }

    [Fact]
    public async Task Reboot_RightAfterADeliveredShutdown_Returns409()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var client = app.CreatePcClient(OtherPcAddress);
        var pc = await AddPcAsync(app);
        await HeartbeatAsync(client, pc);
        await ShutdownAsync(admin, pc.Id);
        await HeartbeatAsync(client, pc);

        Assert.Equal(HttpStatusCode.Conflict, (await RebootAsync(admin, pc.Id)).StatusCode);
    }

    [Fact]
    public async Task Reboot_Repeated_Returns204AndKeepsTheFirstRequest()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var client = app.CreatePcClient(OtherPcAddress);
        var pc = await AddPcAsync(app);
        await HeartbeatAsync(client, pc);
        await RebootAsync(admin, pc.Id);
        var before = await StoredPcAsync(app, pc.Id);

        Assert.Equal(HttpStatusCode.NoContent, (await RebootAsync(admin, pc.Id)).StatusCode);
        Assert.Equal(before.ShutdownRequestedAtUtc, (await StoredPcAsync(app, pc.Id)).ShutdownRequestedAtUtc);
    }

    [Fact]
    public async Task RebootMany_CountsOnlyNewlyRequestedPcs_AndSkipsOfflineServerPendingAndUnknown()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var clientServer = app.CreatePcClient("127.0.0.1");
        var online = await AddPcAsync(app, "PC-01");
        var otherOnline = await AddPcAsync(app, "PC-02");
        var pendingShutdown = await AddPcAsync(app, "PC-03");
        var pendingRestart = await AddPcAsync(app, "PC-04");
        var offline = await AddPcAsync(app, "PC-05", seenAgo: TimeSpan.FromMinutes(1));
        var serverPc = await AddPcAsync(app, "PC-06");
        await ShutdownAsync(admin, pendingShutdown.Id);
        await RebootAsync(admin, pendingRestart.Id);
        var shutdownRequest = (await StoredPcAsync(app, pendingShutdown.Id)).ShutdownRequestedAtUtc;
        var restartRequest = (await StoredPcAsync(app, pendingRestart.Id)).ShutdownRequestedAtUtc;
        app.Time.Advance(TimeSpan.FromSeconds(10));
        await BringOnlineAsync(app, online.Id, otherOnline.Id, pendingShutdown.Id, pendingRestart.Id);
        await HeartbeatAsync(clientServer, serverPc);

        var affected = await AffectedAsync(await RebootManyAsync(
            admin, online.Id, otherOnline.Id, pendingShutdown.Id, pendingRestart.Id, offline.Id, serverPc.Id, 999, online.Id));

        Assert.Equal(2, affected);
        Assert.Equal(PcShutdownRules.RestartAction, (await StoredPcAsync(app, online.Id)).PowerAction);
        Assert.Equal(PcShutdownRules.RestartAction, (await StoredPcAsync(app, otherOnline.Id)).PowerAction);
        var shutdownStored = await StoredPcAsync(app, pendingShutdown.Id);
        Assert.Equal(shutdownRequest, shutdownStored.ShutdownRequestedAtUtc);
        Assert.Equal(PcShutdownRules.ShutdownAction, shutdownStored.PowerAction);
        Assert.Equal(restartRequest, (await StoredPcAsync(app, pendingRestart.Id)).ShutdownRequestedAtUtc);
        Assert.Null((await StoredPcAsync(app, offline.Id)).ShutdownRequestedAtUtc);
        Assert.Null((await StoredPcAsync(app, serverPc.Id)).ShutdownRequestedAtUtc);
    }

    [Fact]
    public async Task RebootMany_SecondCall_CountsZero()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        var first = await AddPcAsync(app, "PC-01");
        var second = await AddPcAsync(app, "PC-02");

        Assert.Equal(2, await AffectedAsync(await RebootManyAsync(admin, first.Id, second.Id)));
        Assert.Equal(0, await AffectedAsync(await RebootManyAsync(admin, first.Id, second.Id)));
    }

    [Fact]
    public async Task RebootMany_WithinTheDeliveryWindowOfAShutdown_CountsZero()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var client = app.CreatePcClient(OtherPcAddress);
        var pc = await AddPcAsync(app);
        await HeartbeatAsync(client, pc);
        await ShutdownManyAsync(admin, pc.Id);
        AssertDeliveredOnly(await HeartbeatBodyAsync(client, pc), PcShutdownRules.ShutdownAction);
        app.Time.Advance(TimeSpan.FromSeconds(10));
        await BringOnlineAsync(app, pc.Id);

        Assert.Equal(0, await AffectedAsync(await RebootManyAsync(admin, pc.Id)));
        Assert.Null((await StoredPcAsync(app, pc.Id)).ShutdownRequestedAtUtc);
    }

    [Fact]
    public async Task RebootMany_WhenTheAddressesCannotBeRead_RequestsNothing()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var client = app.CreatePcClient(OtherPcAddress);
        var pc = await AddPcAsync(app);
        await HeartbeatAsync(client, pc);
        app.LocalAddresses.Fail = true;

        Assert.Equal(0, await AffectedAsync(await RebootManyAsync(admin, pc.Id)));
        Assert.Null((await StoredPcAsync(app, pc.Id)).ShutdownRequestedAtUtc);
    }

    // Delivery on the heartbeat

    [Theory]
    [InlineData(PcShutdownRules.ShutdownAction)]
    [InlineData(PcShutdownRules.RestartAction)]
    public async Task Heartbeat_AfterARequest_DeliversItsActionOnly(string action)
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var client = app.CreatePcClient(OtherPcAddress);
        var pc = await AddPcAsync(app);
        await HeartbeatAsync(client, pc);
        await PowerAsync(admin, action, pc.Id);

        var body = await HeartbeatBodyAsync(client, pc);

        AssertDeliveredOnly(body, action);
        var stored = await StoredPcAsync(app, pc.Id);
        Assert.Null(stored.ShutdownRequestedAtUtc);
        Assert.Equal(app.Time.GetUtcNow().ToUnixTimeMilliseconds(), stored.ShutdownDeliveredAtUtc!.Value.ToUnixTimeMilliseconds());
        Assert.Equal(action, stored.PowerAction);
    }

    [Theory]
    [InlineData(PcShutdownRules.ShutdownAction)]
    [InlineData(PcShutdownRules.RestartAction)]
    public async Task Heartbeat_TwoInARow_DeliversTheActionExactlyOnce(string action)
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var client = app.CreatePcClient(OtherPcAddress);
        var pc = await AddPcAsync(app);
        await HeartbeatAsync(client, pc);
        await PowerAsync(admin, action, pc.Id);

        var first = await HeartbeatBodyAsync(client, pc);
        var second = await HeartbeatBodyAsync(client, pc);

        AssertDeliveredOnly(first, action);
        AssertNothingDelivered(second);
    }

    [Fact]
    public async Task Heartbeat_WithoutARequest_ReturnsANullRestartSeconds()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient(OtherPcAddress);
        var pc = await AddPcAsync(app);

        var body = await HeartbeatBodyAsync(client, pc);

        Assert.Equal(JsonValueKind.Null, body.GetProperty("restartSeconds").ValueKind);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("shutdownSeconds").ValueKind);
    }

    [Fact]
    public async Task Heartbeat_60SecondsAfterARestartRequest_DropsItUndelivered()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var client = app.CreatePcClient(OtherPcAddress);
        var pc = await AddPcAsync(app);
        await HeartbeatAsync(client, pc);
        await RebootAsync(admin, pc.Id);
        app.Time.Advance(TimeSpan.FromSeconds(60));

        AssertNothingDelivered(await HeartbeatBodyAsync(client, pc));
        var stored = await StoredPcAsync(app, pc.Id);
        Assert.Null(stored.ShutdownRequestedAtUtc);
        Assert.Null(stored.ShutdownDeliveredAtUtc);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData(TestApp.MachineLanAddress)]
    public async Task Heartbeat_LiveAddressIsThisMachine_DeliversNoRestart(string liveAddress)
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var otherPc = app.CreatePcClient(OtherPcAddress);
        var pc = await AddPcAsync(app);
        await HeartbeatAsync(otherPc, pc);
        await RebootAsync(admin, pc.Id);
        using var serverPc = app.CreatePcClient(liveAddress);

        AssertNothingDelivered(await HeartbeatBodyAsync(serverPc, pc));
        var stored = await StoredPcAsync(app, pc.Id);
        Assert.Null(stored.ShutdownRequestedAtUtc);
        Assert.Null(stored.ShutdownDeliveredAtUtc);
    }

    [Fact]
    public async Task Heartbeat_WithoutALiveAddress_DeliversNoRestart()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var otherPc = app.CreatePcClient(OtherPcAddress);
        var pc = await AddPcAsync(app);
        await HeartbeatAsync(otherPc, pc);
        await RebootAsync(admin, pc.Id);
        using var noAddress = app.CreatePcClient(SimulatedConnectionFilter.NoAddress);

        AssertNothingDelivered(await HeartbeatBodyAsync(noAddress, pc));
        Assert.Null((await StoredPcAsync(app, pc.Id)).ShutdownDeliveredAtUtc);
    }

    [Fact]
    public async Task Heartbeat_WhenTheAddressesCannotBeRead_WaitsForALaterHeartbeat()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var client = app.CreatePcClient(OtherPcAddress);
        var pc = await AddPcAsync(app);
        await HeartbeatAsync(client, pc);
        await RebootAsync(admin, pc.Id);
        app.LocalAddresses.Fail = true;

        AssertNothingDelivered(await HeartbeatBodyAsync(client, pc));
        Assert.NotNull((await StoredPcAsync(app, pc.Id)).ShutdownRequestedAtUtc);

        app.LocalAddresses.Fail = false;
        AssertDeliveredOnly(await HeartbeatBodyAsync(client, pc), PcShutdownRules.RestartAction);
    }

    // A PC that wasn't online before a heartbeat has only just started. It is never handed the action, and the first
    // heartbeat clears whatever it was going through.

    [Theory]
    [InlineData(PcShutdownRules.ShutdownAction)]
    [InlineData(PcShutdownRules.RestartAction)]
    public async Task Heartbeat_BackWithinTheWindowAfterGoingOffline_DeliversNothingAndDropsTheRequest(string action)
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var client = app.CreatePcClient(OtherPcAddress);
        var pc = await AddPcAsync(app);
        await HeartbeatAsync(client, pc);
        await PowerAsync(admin, action, pc.Id);
        app.Time.Advance(TimeSpan.FromSeconds(20));

        AssertNothingDelivered(await HeartbeatBodyAsync(client, pc));
        var stored = await StoredPcAsync(app, pc.Id);
        Assert.Null(stored.ShutdownRequestedAtUtc);
        Assert.Null(stored.ShutdownDeliveredAtUtc);
    }

    [Theory]
    [InlineData(PcShutdownRules.ShutdownAction)]
    [InlineData(PcShutdownRules.RestartAction)]
    public async Task FirstHeartbeatAfterComingBack_ClearsTheDelivery_AndGetsNothing(string action)
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var client = app.CreatePcClient(OtherPcAddress);
        var pc = await AddPcAsync(app);
        await HeartbeatAsync(client, pc);
        await PowerAsync(admin, action, pc.Id);
        AssertDeliveredOnly(await HeartbeatBodyAsync(client, pc), action);
        app.Time.Advance(TimeSpan.FromSeconds(20));

        AssertNothingDelivered(await HeartbeatBodyAsync(client, pc));
        Assert.Null((await StoredPcAsync(app, pc.Id)).ShutdownDeliveredAtUtc);
        Assert.Null((await ListEntryAsync(admin, "PC-01")).GetProperty("shutdownState").GetString());
    }

    [Theory]
    [InlineData(PcShutdownRules.ShutdownAction)]
    [InlineData(PcShutdownRules.RestartAction)]
    public async Task Offline_ClearsAPendingRequestOfEitherAction(string action)
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var client = app.CreatePcClient(OtherPcAddress);
        var pc = await AddPcAsync(app);
        await HeartbeatAsync(client, pc);
        await PowerAsync(admin, action, pc.Id);

        var response = await PostAsync(client, "/api/client/offline", pc.Token);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Null((await StoredPcAsync(app, pc.Id)).ShutdownRequestedAtUtc);
        AssertNothingDelivered(await HeartbeatBodyAsync(client, pc));
    }

    // Rows written before restarts existed have no action. A stored request or delivery with no action is a shutdown.

    [Fact]
    public async Task OldRowWithARequestAndNoAction_IsDeliveredAsAShutdown()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient(OtherPcAddress);
        var pc = await AddPcAsync(app);
        await HeartbeatAsync(client, pc);
        await app.WithDbAsync(db => db.Pcs
            .Where(p => p.Id == pc.Id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(p => p.ShutdownRequestedAtUtc, (DateTimeOffset?)app.Time.GetUtcNow())));

        var body = await HeartbeatBodyAsync(client, pc);

        AssertDeliveredOnly(body, PcShutdownRules.ShutdownAction);
    }

    [Fact]
    public async Task OldDeliveryWithNoAction_ShowsAsShutdownWhenOffline()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var client = app.CreatePcClient(OtherPcAddress);
        var pc = await AddPcAsync(app);
        await HeartbeatAsync(client, pc);
        await app.WithDbAsync(db => db.Pcs
            .Where(p => p.Id == pc.Id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(p => p.ShutdownDeliveredAtUtc, (DateTimeOffset?)app.Time.GetUtcNow())));
        app.Time.Advance(TimeSpan.FromMinutes(10));

        var entry = await ListEntryAsync(admin, "PC-01");

        Assert.False(entry.GetProperty("isOnline").GetBoolean());
        Assert.Equal(PcShutdownRules.ShutdownState, entry.GetProperty("shutdownState").GetString());
    }

    [Fact]
    public async Task OldDeliveryWithNoAction_LogoutWithinTwoMinutes_EndsTheSessionAsAShutdown()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient(OtherPcAddress);
        var pc = await AddPcAsync(app);
        await AddAccountAsync(app, minutes: null);
        await HeartbeatAsync(client, pc);
        var sessionId = await LoggedInSessionIdAsync(client, pc);
        await app.WithDbAsync(db => db.Pcs
            .Where(p => p.Id == pc.Id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(p => p.ShutdownDeliveredAtUtc, (DateTimeOffset?)app.Time.GetUtcNow())));
        app.Time.Advance(TimeSpan.FromSeconds(30));

        await PostAsync(client, "/api/client/logout", pc.Token);

        Assert.Equal(SessionLifecycle.AdminShutdown, (await StoredSessionAsync(app, sessionId)).EndReason);
    }

    // PC list state

    [Fact]
    public async Task List_PendingRestart_IsRestarting()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var client = app.CreatePcClient(OtherPcAddress);
        var pc = await AddPcAsync(app);
        await HeartbeatAsync(client, pc);
        await RebootAsync(admin, pc.Id);

        var entry = await ListEntryAsync(admin, "PC-01");

        Assert.Equal("restarting", entry.GetProperty("shutdownState").GetString());
    }

    [Fact]
    public async Task List_PendingRestartOfAnOfflinePc_IsStillRestarting()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var client = app.CreatePcClient(OtherPcAddress);
        var pc = await AddPcAsync(app);
        await HeartbeatAsync(client, pc);
        await RebootAsync(admin, pc.Id);
        app.Time.Advance(TimeSpan.FromSeconds(20));

        var entry = await ListEntryAsync(admin, "PC-01");

        Assert.False(entry.GetProperty("isOnline").GetBoolean());
        Assert.Equal("restarting", entry.GetProperty("shutdownState").GetString());
    }

    [Fact]
    public async Task List_DeliveredRestartWhileOnline_IsRestarting()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var client = app.CreatePcClient(OtherPcAddress);
        var pc = await AddPcAsync(app);
        await HeartbeatAsync(client, pc);
        await RebootAsync(admin, pc.Id);
        await HeartbeatAsync(client, pc);

        var entry = await ListEntryAsync(admin, "PC-01");

        Assert.True(entry.GetProperty("isOnline").GetBoolean());
        Assert.Equal("restarting", entry.GetProperty("shutdownState").GetString());
    }

    [Fact]
    public async Task List_DeliveredRestartAndNowOffline_IsRebooting()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var client = app.CreatePcClient(OtherPcAddress);
        var pc = await AddPcAsync(app);
        await HeartbeatAsync(client, pc);
        await RebootAsync(admin, pc.Id);
        await HeartbeatAsync(client, pc);
        app.Time.Advance(TimeSpan.FromSeconds(20));

        var entry = await ListEntryAsync(admin, "PC-01");

        Assert.False(entry.GetProperty("isOnline").GetBoolean());
        Assert.Equal("rebooting", entry.GetProperty("shutdownState").GetString());
    }

    [Fact]
    public async Task List_DeliveredRestartOffline_IsRebootingUntilFiveMinutesAndThenPlainOffline()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var client = app.CreatePcClient(OtherPcAddress);
        var pc = await AddPcAsync(app);
        await HeartbeatAsync(client, pc);
        await RebootAsync(admin, pc.Id);
        await HeartbeatAsync(client, pc);
        app.Time.Advance(TimeSpan.FromMinutes(5) - TimeSpan.FromSeconds(1));

        var rebooting = await ListEntryAsync(admin, "PC-01");
        Assert.Equal("rebooting", rebooting.GetProperty("shutdownState").GetString());

        app.Time.Advance(TimeSpan.FromSeconds(1));

        var plainOffline = await ListEntryAsync(admin, "PC-01");
        Assert.False(plainOffline.GetProperty("isOnline").GetBoolean());
        Assert.Equal(JsonValueKind.Null, plainOffline.GetProperty("shutdownState").ValueKind);
    }

    [Fact]
    public async Task List_DeliveredShutdownOffline_StaysShutdownPastFiveMinutes()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var client = app.CreatePcClient(OtherPcAddress);
        var pc = await AddPcAsync(app);
        await HeartbeatAsync(client, pc);
        await ShutdownAsync(admin, pc.Id);
        await HeartbeatAsync(client, pc);
        app.Time.Advance(TimeSpan.FromMinutes(10));

        var entry = await ListEntryAsync(admin, "PC-01");

        Assert.Equal(PcShutdownRules.ShutdownState, entry.GetProperty("shutdownState").GetString());
    }

    // Session end reason on logout

    [Fact]
    public async Task Logout_WithinTwoMinutesOfARestart_EndsTheSessionAsAReboot()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var client = app.CreatePcClient(OtherPcAddress);
        var pc = await AddPcAsync(app);
        await AddAccountAsync(app, minutes: null);
        await HeartbeatAsync(client, pc);
        var sessionId = await LoggedInSessionIdAsync(client, pc);
        await RebootAsync(admin, pc.Id);
        AssertDeliveredOnly(await HeartbeatBodyAsync(client, pc), PcShutdownRules.RestartAction);
        app.Time.Advance(TimeSpan.FromSeconds(110));

        var response = await PostAsync(client, "/api/client/logout", pc.Token);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(SessionLifecycle.AdminReboot, (await StoredSessionAsync(app, sessionId)).EndReason);
    }

    [Fact]
    public async Task Logout_TwoMinutesAfterARestart_EndsTheSessionAsALogout()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var client = app.CreatePcClient(OtherPcAddress);
        var pc = await AddPcAsync(app);
        await AddAccountAsync(app, minutes: null);
        await HeartbeatAsync(client, pc);
        var sessionId = await LoggedInSessionIdAsync(client, pc);
        await RebootAsync(admin, pc.Id);
        AssertDeliveredOnly(await HeartbeatBodyAsync(client, pc), PcShutdownRules.RestartAction);
        app.Time.Advance(TimeSpan.FromMinutes(2));

        await PostAsync(client, "/api/client/logout", pc.Token);

        Assert.Equal("logout", (await StoredSessionAsync(app, sessionId)).EndReason);
    }
}
