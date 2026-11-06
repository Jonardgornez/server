using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using LabGuard.Server.Pcs;
using static LabGuard.Server.Tests.PcShutdownEndpointTests;

namespace LabGuard.Server.Tests;

// Cancelling a shutdown or restart from the admin console. A pending request is dropped. A delivered one is cancelled on an
// online PC, and its delivery is kept until the PC acknowledges the cancel.
public class PcCancelPowerEndpointTests
{
    private const string CancellingShutdownMessage = "This PC is cancelling its shutdown. Try again once the cancel is done.";
    private const string CancellingRestartMessage = "This PC is cancelling its restart. Try again once the cancel is done.";
    private const string NothingToCancelMessage = "This PC has no shutdown or restart to cancel.";
    private const string CancelOfflineMessage = "This PC is offline. Its shutdown or restart can't be cancelled.";

    private static Task<HttpResponseMessage> CancelAsync(HttpClient admin, int pcId) =>
        admin.PostAsJsonAsync($"/api/pcs/{pcId}/cancel-power", new { });

    private static Task<HttpResponseMessage> CancelManyAsync(HttpClient admin, params int[] ids) =>
        admin.PostAsJsonAsync("/api/pcs/cancel-power", new { ids });

    private static Task<HttpResponseMessage> RebootAsync(HttpClient admin, int pcId) =>
        admin.PostAsJsonAsync($"/api/pcs/{pcId}/reboot", new { });

    private static Task<HttpResponseMessage> PowerAsync(HttpClient admin, string action, int pcId) =>
        action == PcShutdownRules.RestartAction ? RebootAsync(admin, pcId) : ShutdownAsync(admin, pcId);

    private static async Task<string?> ErrorOf(HttpResponseMessage response) =>
        (await JsonOf(response)).GetProperty("error").GetString();

    // Requests the action and has the PC take it on its next heartbeat, so the PC holds a delivery.
    private static async Task DeliverAsync(HttpClient admin, HttpClient client, TestPc pc, string action)
    {
        Assert.Equal(HttpStatusCode.NoContent, (await PowerAsync(admin, action, pc.Id)).StatusCode);
        var body = await HeartbeatBodyAsync(client, pc);
        var deliveredSeconds = action == PcShutdownRules.RestartAction ? body.GetProperty("restartSeconds") : body.GetProperty("shutdownSeconds");
        Assert.Equal(JsonValueKind.Number, deliveredSeconds.ValueKind);
    }

    // Routes

    [Theory]
    [InlineData("/api/pcs/1/cancel-power")]
    [InlineData("/api/pcs/cancel-power")]
    public async Task CancelRoutes_WithoutCookie_Return401(string path)
    {
        using var app = new TestApp();
        using var browser = app.CreateBrowser();

        var response = await browser.PostAsJsonAsync(path, new { });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("/api/pcs/1/cancel-power")]
    [InlineData("/api/pcs/cancel-power")]
    public async Task CancelRoutes_FromThePcPort_Return404(string path)
    {
        using var app = new TestApp();
        using var pcClient = app.CreatePcClient(OtherPcAddress);
        await AddPcAsync(app);

        var response = await pcClient.PostAsJsonAsync(path, new { });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("/api/pcs/{id}/cancel-power", "text/plain", "{}")]
    [InlineData("/api/pcs/{id}/cancel-power", "application/json", "")]
    [InlineData("/api/pcs/{id}/cancel-power", "application/json", "null")]
    [InlineData("/api/pcs/cancel-power", "application/x-www-form-urlencoded", "")]
    [InlineData("/api/pcs/cancel-power", "application/json", "{not json")]
    [InlineData("/api/pcs/cancel-power", "application/json", "{\"ids\":[1,null]}")]
    public async Task CancelRoutes_WithoutAJsonBody_Return400AndCancelNothing(string path, string contentType, string body)
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var client = app.CreatePcClient(OtherPcAddress);
        var pc = await AddPcAsync(app);
        await HeartbeatAsync(client, pc);
        await ShutdownAsync(admin, pc.Id);

        using var content = new StringContent(body, Encoding.UTF8, contentType);
        var response = await admin.PostAsync(path.Replace("{id}", pc.Id.ToString()), content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(InvalidRequestMessage, await ErrorOf(response));
        Assert.NotNull((await StoredPcAsync(app, pc.Id)).ShutdownRequestedAtUtc);
    }

    // Single cancel

    [Fact]
    public async Task Cancel_PendingRequest_Returns204AndDropsIt()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var client = app.CreatePcClient(OtherPcAddress);
        var pc = await AddPcAsync(app);
        await HeartbeatAsync(client, pc);
        await ShutdownAsync(admin, pc.Id);

        var response = await CancelAsync(admin, pc.Id);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var stored = await StoredPcAsync(app, pc.Id);
        Assert.Null(stored.ShutdownRequestedAtUtc);
        Assert.Null(stored.ShutdownDeliveredAtUtc);
        Assert.Null(stored.PowerCancelRequestedAtUtc);
        Assert.Null(ShutdownSecondsOf(await HeartbeatBodyAsync(client, pc)));
    }

    [Fact]
    public async Task Cancel_PendingRequestOfAnOfflinePc_Returns204AndDropsIt()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var client = app.CreatePcClient(OtherPcAddress);
        var pc = await AddPcAsync(app);
        await HeartbeatAsync(client, pc);
        await ShutdownAsync(admin, pc.Id);
        app.Time.Advance(TimeSpan.FromSeconds(20));

        Assert.Equal(HttpStatusCode.NoContent, (await CancelAsync(admin, pc.Id)).StatusCode);

        Assert.Null((await StoredPcAsync(app, pc.Id)).ShutdownRequestedAtUtc);
    }

    [Fact]
    public async Task Cancel_MissingPc_Returns404()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);

        var response = await CancelAsync(admin, 999);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("PC not found.", await ErrorOf(response));
    }

    [Theory]
    [InlineData(PcShutdownRules.ShutdownAction)]
    [InlineData(PcShutdownRules.RestartAction)]
    public async Task Cancel_DeliveredOnlinePc_Returns204_RecordsTheCancel_AndKeepsTheDelivery(string action)
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var client = app.CreatePcClient(OtherPcAddress);
        var pc = await AddPcAsync(app);
        await HeartbeatAsync(client, pc);
        await DeliverAsync(admin, client, pc, action);
        var delivered = (await StoredPcAsync(app, pc.Id)).ShutdownDeliveredAtUtc;

        var response = await CancelAsync(admin, pc.Id);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var stored = await StoredPcAsync(app, pc.Id);
        Assert.Equal(delivered, stored.ShutdownDeliveredAtUtc);
        Assert.Equal(app.Time.GetUtcNow().ToUnixTimeMilliseconds(), stored.PowerCancelRequestedAtUtc!.Value.ToUnixTimeMilliseconds());
        Assert.Equal(action, stored.PowerAction);
        Assert.Equal(PcShutdownRules.CancellingState, (await ListEntryAsync(admin, "PC-01")).GetProperty("shutdownState").GetString());
    }

    [Fact]
    public async Task Cancel_AlreadyCancelling_Returns204AndKeepsTheFirstCancel()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var client = app.CreatePcClient(OtherPcAddress);
        var pc = await AddPcAsync(app);
        await HeartbeatAsync(client, pc);
        await DeliverAsync(admin, client, pc, PcShutdownRules.ShutdownAction);
        await CancelAsync(admin, pc.Id);
        var firstCancel = (await StoredPcAsync(app, pc.Id)).PowerCancelRequestedAtUtc;
        app.Time.Advance(TimeSpan.FromSeconds(5));
        await BringOnlineAsync(app, pc.Id);

        Assert.Equal(HttpStatusCode.NoContent, (await CancelAsync(admin, pc.Id)).StatusCode);

        Assert.Equal(firstCancel, (await StoredPcAsync(app, pc.Id)).PowerCancelRequestedAtUtc);
    }

    [Fact]
    public async Task Cancel_WithNothingToCancel_Returns409AndChangesNothing()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var client = app.CreatePcClient(OtherPcAddress);
        var pc = await AddPcAsync(app);
        await HeartbeatAsync(client, pc);

        var response = await CancelAsync(admin, pc.Id);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(NothingToCancelMessage, await ErrorOf(response));
        var stored = await StoredPcAsync(app, pc.Id);
        Assert.Null(stored.ShutdownRequestedAtUtc);
        Assert.Null(stored.PowerCancelRequestedAtUtc);
    }

    [Fact]
    public async Task Cancel_DeliveryOutsideItsWindow_Returns409AndRecordsNothing()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var client = app.CreatePcClient(OtherPcAddress);
        var pc = await AddPcAsync(app);
        await HeartbeatAsync(client, pc);
        await DeliverAsync(admin, client, pc, PcShutdownRules.ShutdownAction);
        app.Time.Advance(TimeSpan.FromSeconds(60));
        await BringOnlineAsync(app, pc.Id);

        var response = await CancelAsync(admin, pc.Id);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(NothingToCancelMessage, await ErrorOf(response));
        Assert.Null((await StoredPcAsync(app, pc.Id)).PowerCancelRequestedAtUtc);
    }

    [Fact]
    public async Task Cancel_DeliveredPcThatIsOffline_Returns409AndRecordsNothing()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var client = app.CreatePcClient(OtherPcAddress);
        var pc = await AddPcAsync(app);
        await HeartbeatAsync(client, pc);
        await DeliverAsync(admin, client, pc, PcShutdownRules.ShutdownAction);
        app.Time.Advance(TimeSpan.FromSeconds(20));

        var response = await CancelAsync(admin, pc.Id);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(CancelOfflineMessage, await ErrorOf(response));
        var stored = await StoredPcAsync(app, pc.Id);
        Assert.Null(stored.PowerCancelRequestedAtUtc);
        Assert.NotNull(stored.ShutdownDeliveredAtUtc);
    }

    [Fact]
    public async Task Cancel_WhenTheAddressesCannotBeRead_StillCancels()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var client = app.CreatePcClient(OtherPcAddress);
        var pc = await AddPcAsync(app);
        await HeartbeatAsync(client, pc);
        await DeliverAsync(admin, client, pc, PcShutdownRules.ShutdownAction);
        app.LocalAddresses.Fail = true;

        Assert.Equal(HttpStatusCode.NoContent, (await CancelAsync(admin, pc.Id)).StatusCode);

        Assert.NotNull((await StoredPcAsync(app, pc.Id)).PowerCancelRequestedAtUtc);
    }

    // A request while a cancel is recorded is refused, for either action, and says which cancel holds the PC.

    [Theory]
    [InlineData(PcShutdownRules.ShutdownAction, PcShutdownRules.ShutdownAction)]
    [InlineData(PcShutdownRules.ShutdownAction, PcShutdownRules.RestartAction)]
    [InlineData(PcShutdownRules.RestartAction, PcShutdownRules.ShutdownAction)]
    [InlineData(PcShutdownRules.RestartAction, PcShutdownRules.RestartAction)]
    public async Task Request_WhileACancelIsRecorded_IsRefusedWithTheCancelMessage(string cancelledAction, string requestedAction)
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var client = app.CreatePcClient(OtherPcAddress);
        var pc = await AddPcAsync(app);
        await HeartbeatAsync(client, pc);
        await DeliverAsync(admin, client, pc, cancelledAction);
        await CancelAsync(admin, pc.Id);

        var response = await PowerAsync(admin, requestedAction, pc.Id);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var expectedMessage = cancelledAction == PcShutdownRules.RestartAction ? CancellingRestartMessage : CancellingShutdownMessage;
        Assert.Equal(expectedMessage, await ErrorOf(response));
        var stored = await StoredPcAsync(app, pc.Id);
        Assert.Null(stored.ShutdownRequestedAtUtc);
        Assert.NotNull(stored.PowerCancelRequestedAtUtc);
    }

    // The cancel never outlives its delivery

    [Fact]
    public async Task Delivery_ForgottenAfterItsWindowWhileOnline_ClearsTheCancelWithIt()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var client = app.CreatePcClient(OtherPcAddress);
        var pc = await AddPcAsync(app);
        await HeartbeatAsync(client, pc);
        await DeliverAsync(admin, client, pc, PcShutdownRules.ShutdownAction);
        await CancelAsync(admin, pc.Id);
        app.Time.Advance(TimeSpan.FromSeconds(61));
        await BringOnlineAsync(app, pc.Id);

        await HeartbeatAsync(client, pc);

        var stored = await StoredPcAsync(app, pc.Id);
        Assert.Null(stored.ShutdownDeliveredAtUtc);
        Assert.Null(stored.PowerCancelRequestedAtUtc);
    }

    [Fact]
    public async Task PcComingBackFromOffline_ClearsTheCancelAndTheDelivery()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var client = app.CreatePcClient(OtherPcAddress);
        var pc = await AddPcAsync(app);
        await HeartbeatAsync(client, pc);
        await DeliverAsync(admin, client, pc, PcShutdownRules.ShutdownAction);
        await CancelAsync(admin, pc.Id);
        app.Time.Advance(TimeSpan.FromSeconds(20));

        await HeartbeatAsync(client, pc);

        var stored = await StoredPcAsync(app, pc.Id);
        Assert.Null(stored.ShutdownDeliveredAtUtc);
        Assert.Null(stored.PowerCancelRequestedAtUtc);
    }

    [Fact]
    public async Task NewRequestAfterAnExpiredDelivery_DoesNotInheritTheOldCancel()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var client = app.CreatePcClient(OtherPcAddress);
        var pc = await AddPcAsync(app);
        await HeartbeatAsync(client, pc);
        await DeliverAsync(admin, client, pc, PcShutdownRules.ShutdownAction);
        await CancelAsync(admin, pc.Id);
        app.Time.Advance(TimeSpan.FromSeconds(61));
        await BringOnlineAsync(app, pc.Id);

        Assert.Equal(HttpStatusCode.NoContent, (await RebootAsync(admin, pc.Id)).StatusCode);

        var stored = await StoredPcAsync(app, pc.Id);
        Assert.NotNull(stored.ShutdownRequestedAtUtc);
        Assert.Null(stored.PowerCancelRequestedAtUtc);
        Assert.Equal(PcShutdownRules.RestartAction, stored.PowerAction);
    }

    [Theory]
    [InlineData(PcShutdownRules.ShutdownAction, PcShutdownRules.ShutdownState)]
    [InlineData(PcShutdownRules.RestartAction, PcShutdownRules.RebootingState)]
    public async Task DeliveredRow_IsKeptWhenThePcNeverAcknowledgesTheCancel(string action, string stateWhenDown)
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var client = app.CreatePcClient(OtherPcAddress);
        var pc = await AddPcAsync(app);
        await HeartbeatAsync(client, pc);
        await DeliverAsync(admin, client, pc, action);
        await CancelAsync(admin, pc.Id);

        // An older client keeps heartbeating without acting on the cancel, then the PC goes down anyway.
        await HeartbeatAsync(client, pc);
        Assert.Equal(PcShutdownRules.CancellingState, (await ListEntryAsync(admin, "PC-01")).GetProperty("shutdownState").GetString());
        app.Time.Advance(TimeSpan.FromSeconds(20));

        var entry = await ListEntryAsync(admin, "PC-01");
        Assert.False(entry.GetProperty("isOnline").GetBoolean());
        Assert.Equal(stateWhenDown, entry.GetProperty("shutdownState").GetString());
        Assert.NotNull((await StoredPcAsync(app, pc.Id)).ShutdownDeliveredAtUtc);
    }

    // Bulk cancel

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"ids\":null}")]
    [InlineData("{\"ids\":[]}")]
    [InlineData("{\"ids\":[\"one\"]}")]
    public async Task CancelMany_WithAMissingOrEmptyList_Return400AndChangesNothing(string body)
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        var pc = await AddPcAsync(app);

        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        var response = await admin.PostAsync("/api/pcs/cancel-power", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(InvalidRequestMessage, await ErrorOf(response));
        Assert.Null((await StoredPcAsync(app, pc.Id)).PowerCancelRequestedAtUtc);
    }

    [Fact]
    public async Task CancelMany_CountsOnlyThePcsWhereSomethingWasCancelled()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var client = app.CreatePcClient(OtherPcAddress);
        var delivered = await AddPcAsync(app, "PC-01");
        var pending = await AddPcAsync(app, "PC-02");
        var alreadyCancelling = await AddPcAsync(app, "PC-03");
        var idle = await AddPcAsync(app, "PC-04");
        var goneOffline = await AddPcAsync(app, "PC-05");
        foreach (var pc in new[] { delivered, pending, alreadyCancelling, idle, goneOffline })
        {
            await HeartbeatAsync(client, pc);
        }

        await DeliverAsync(admin, client, delivered, PcShutdownRules.ShutdownAction);
        await DeliverAsync(admin, client, alreadyCancelling, PcShutdownRules.ShutdownAction);
        await DeliverAsync(admin, client, goneOffline, PcShutdownRules.ShutdownAction);
        await ShutdownAsync(admin, pending.Id);
        await CancelAsync(admin, alreadyCancelling.Id);
        app.Time.Advance(TimeSpan.FromSeconds(20));
        await BringOnlineAsync(app, delivered.Id, pending.Id, alreadyCancelling.Id, idle.Id);

        var affected = await AffectedAsync(await CancelManyAsync(
            admin, delivered.Id, pending.Id, alreadyCancelling.Id, idle.Id, goneOffline.Id, 999));

        Assert.Equal(2, affected);
        Assert.NotNull((await StoredPcAsync(app, delivered.Id)).PowerCancelRequestedAtUtc);
        Assert.Null((await StoredPcAsync(app, pending.Id)).ShutdownRequestedAtUtc);
        Assert.Null((await StoredPcAsync(app, goneOffline.Id)).PowerCancelRequestedAtUtc);
    }

    [Fact]
    public async Task CancelMany_SecondCall_CountsZero()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var client = app.CreatePcClient(OtherPcAddress);
        var pc = await AddPcAsync(app);
        await HeartbeatAsync(client, pc);
        await DeliverAsync(admin, client, pc, PcShutdownRules.ShutdownAction);

        Assert.Equal(1, await AffectedAsync(await CancelManyAsync(admin, pc.Id)));
        Assert.Equal(0, await AffectedAsync(await CancelManyAsync(admin, pc.Id)));
    }
}
