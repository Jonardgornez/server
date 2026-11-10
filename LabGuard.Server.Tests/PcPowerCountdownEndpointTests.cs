using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LabGuard.Server.Pcs;
using LabGuard.Server.Sessions;
using static LabGuard.Server.Tests.PcShutdownEndpointTests;

namespace LabGuard.Server.Tests;

// The countdown set in the settings. A delivery keeps the countdown it was given, so its windows are its own, and a later
// change to the setting never alters a shutdown or restart already under way.
public class PcPowerCountdownEndpointTests
{
    private static async Task SetCountdownAsync(HttpClient admin, int seconds)
    {
        var response = await admin.PutAsJsonAsync("/api/settings", new
        {
            defaultApproval = new { mode = "time-limit", minutes = 60 },
            notice = new { isEnabled = true, text = "Notice" },
            idleTimeoutMinutes = 30,
            defaultViewMode = "notified",
            powerCountdownSeconds = seconds,
            lockMessage = "This computer has been locked by the administrator.",
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static int? RestartSecondsOf(JsonElement heartbeat) =>
        heartbeat.GetProperty("restartSeconds").ValueKind == JsonValueKind.Null
            ? null
            : heartbeat.GetProperty("restartSeconds").GetInt32();

    [Fact]
    public async Task Heartbeat_DeliversTheCountdownFromTheSettings_AndStoresIt()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var client = app.CreatePcClient(OtherPcAddress);
        var pc = await AddPcAsync(app);
        await HeartbeatAsync(client, pc);
        await SetCountdownAsync(admin, 600);
        await ShutdownAsync(admin, pc.Id);

        var body = await HeartbeatBodyAsync(client, pc);

        Assert.Equal(600, ShutdownSecondsOf(body));
        Assert.Equal(600, (await StoredPcAsync(app, pc.Id)).PowerCountdownSeconds);
    }

    [Fact]
    public async Task Heartbeat_ARestartDeliversTheCountdownAsRestartSeconds()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var client = app.CreatePcClient(OtherPcAddress);
        var pc = await AddPcAsync(app);
        await HeartbeatAsync(client, pc);
        await SetCountdownAsync(admin, 120);
        await admin.PostAsJsonAsync($"/api/pcs/{pc.Id}/reboot", new { });

        var body = await HeartbeatBodyAsync(client, pc);

        Assert.Equal(120, RestartSecondsOf(body));
        Assert.Null(ShutdownSecondsOf(body));
    }

    [Theory]
    [InlineData(629, false)]
    [InlineData(630, true)]
    public async Task Request_WithALongCountdown_IsStoredOnlyOnceItsDeliveryWindowHasPassed(int secondsAfterDelivery, bool isStored)
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var client = app.CreatePcClient(OtherPcAddress);
        var pc = await AddPcAsync(app);
        await HeartbeatAsync(client, pc);
        await SetCountdownAsync(admin, 600);
        await ShutdownAsync(admin, pc.Id);
        Assert.Equal(600, ShutdownSecondsOf(await HeartbeatBodyAsync(client, pc)));
        app.Time.Advance(TimeSpan.FromSeconds(secondsAfterDelivery));
        await BringOnlineAsync(app, pc.Id);

        Assert.Equal(HttpStatusCode.NoContent, (await ShutdownAsync(admin, pc.Id)).StatusCode);

        Assert.Equal(isStored, (await StoredPcAsync(app, pc.Id)).ShutdownRequestedAtUtc is not null);
    }

    [Theory]
    [InlineData(689, true)]
    [InlineData(690, false)]
    public async Task Logout_AtTheEdgeOfALongLogoutWindow_EndsTheSessionAsAnAdminShutdownOnlyInsideIt(int secondsAfterDelivery, bool isAdminShutdown)
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var client = app.CreatePcClient(OtherPcAddress);
        var pc = await AddPcAsync(app);
        await AddAccountAsync(app, minutes: null);
        await HeartbeatAsync(client, pc);
        var sessionId = await LoggedInSessionIdAsync(client, pc);
        await SetCountdownAsync(admin, 600);
        await ShutdownAsync(admin, pc.Id);
        Assert.Equal(600, ShutdownSecondsOf(await HeartbeatBodyAsync(client, pc)));
        app.Time.Advance(TimeSpan.FromSeconds(secondsAfterDelivery));

        await PostAsync(client, "/api/client/logout", pc.Token);

        var expectedReason = isAdminShutdown ? SessionLifecycle.AdminShutdown : SessionLifecycle.Logout;
        Assert.Equal(expectedReason, (await StoredSessionAsync(app, sessionId)).EndReason);
    }

    [Theory]
    [InlineData(869, true)]
    [InlineData(870, false)]
    public async Task List_RestartOfALongCountdown_IsRebootingOnlyInsideItsWindow(int secondsAfterDelivery, bool isRebooting)
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var client = app.CreatePcClient(OtherPcAddress);
        var pc = await AddPcAsync(app);
        await HeartbeatAsync(client, pc);
        await SetCountdownAsync(admin, 600);
        await admin.PostAsJsonAsync($"/api/pcs/{pc.Id}/reboot", new { });
        await HeartbeatAsync(client, pc);
        app.Time.Advance(TimeSpan.FromSeconds(secondsAfterDelivery));

        var entry = await ListEntryAsync(admin, "PC-01");

        Assert.False(entry.GetProperty("isOnline").GetBoolean());
        Assert.Equal(isRebooting ? PcShutdownRules.RebootingState : null, entry.GetProperty("shutdownState").GetString());
    }

    [Fact]
    public async Task RunningCountdown_KeepsItsOwnWindow_WhenTheSettingChangesLater()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var client = app.CreatePcClient(OtherPcAddress);
        var pc = await AddPcAsync(app);
        await HeartbeatAsync(client, pc);
        await ShutdownAsync(admin, pc.Id);
        Assert.Equal(30, ShutdownSecondsOf(await HeartbeatBodyAsync(client, pc)));
        await SetCountdownAsync(admin, 600);
        app.Time.Advance(TimeSpan.FromSeconds(61));
        await BringOnlineAsync(app, pc.Id);

        var response = await ShutdownAsync(admin, pc.Id);

        // The delivery was given 30 seconds, so its window ended at 60 seconds. A window from the new 600 would still hold the PC.
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var stored = await StoredPcAsync(app, pc.Id);
        Assert.Equal(30, stored.PowerCountdownSeconds);
        Assert.NotNull(stored.ShutdownRequestedAtUtc);
    }
}
