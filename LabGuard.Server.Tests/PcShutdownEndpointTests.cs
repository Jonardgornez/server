using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LabGuard.Server.Data;
using LabGuard.Server.Sessions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LabGuard.Server.Tests;

public class PcShutdownEndpointTests
{
    private const string StudentPassword = "student pass 1";
    private const string InvalidRequestMessage = "Invalid request.";
    private const string OfflineMessage = "This PC is offline.";
    private const string ServerPcMessage = "This PC runs the server. Shut it down by hand.";
    // Other PCs in these tests use a documentation address, never one of the test machine's own.
    private const string OtherPcAddress = "203.0.113.5";

    private sealed record TestPc(int Id, string Token);

    private static async Task<HttpClient> SignedInAsync(TestApp app)
    {
        var client = app.CreateBrowser();
        var response = await client.PostAsJsonAsync("/api/auth/login", new { username = TestApp.Username, password = TestApp.Password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }

    private static async Task<JsonElement> JsonOf(HttpResponseMessage response)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.Clone();
    }

    // A PC that has never reported its address; its first heartbeat sets the address and brings it online.
    private static async Task<TestPc> AddPcAsync(TestApp app, string name = "PC-01", TimeSpan? seenAgo = null)
    {
        var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
        var now = app.Time.GetUtcNow();
        var id = await app.WithDbAsync(async db =>
        {
            var pc = new Pc { Name = name, TokenHash = hash, RegisteredAtUtc = now, LastSeenUtc = now - (seenAgo ?? TimeSpan.Zero) };
            db.Pcs.Add(pc);
            await db.SaveChangesAsync();
            return pc.Id;
        });
        return new TestPc(id, token);
    }

    private static async Task AddAccountAsync(TestApp app, int? minutes = 60, string username = "student")
    {
        var hasher = app.Services.GetRequiredService<IPasswordHasher<UserAccount>>();
        await app.WithDbAsync(async db =>
        {
            var account = new UserAccount { Username = username, IsActive = true, DefaultTimeMinutes = minutes };
            account.PasswordHash = hasher.HashPassword(account, StudentPassword);
            db.UserAccounts.Add(account);
            await db.SaveChangesAsync();
            return 0;
        });
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string path, string? token, HttpContent? content = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = content };
        if (token is not null)
        {
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
        }

        return await client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> LoginAsync(HttpClient client, TestPc pc, string username = "student") =>
        PostAsync(client, "/api/client/login", pc.Token, JsonContent.Create(new { username, password = StudentPassword }));

    private static Task<HttpResponseMessage> HeartbeatAsync(HttpClient client, TestPc pc, int? sessionId = null) =>
        PostAsync(client, "/api/client/heartbeat", pc.Token, sessionId is null ? null : JsonContent.Create(new { sessionId }));

    private static async Task<JsonElement> HeartbeatBodyAsync(HttpClient client, TestPc pc) =>
        await JsonOf(await HeartbeatAsync(client, pc));

    // Null when the heartbeat carries no shutdown, otherwise the seconds it was given.
    private static int? ShutdownSecondsOf(JsonElement heartbeat) =>
        heartbeat.GetProperty("shutdownSeconds").ValueKind == JsonValueKind.Null
            ? null
            : heartbeat.GetProperty("shutdownSeconds").GetInt32();

    private static async Task<int> LoggedInSessionIdAsync(HttpClient client, TestPc pc, string username = "student")
    {
        var response = await LoginAsync(client, pc, username);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await JsonOf(response)).GetProperty("sessionId").GetInt32();
    }

    private static Task<Pc> StoredPcAsync(TestApp app, int pcId) =>
        app.WithDbAsync(db => db.Pcs.AsNoTracking().SingleAsync(p => p.Id == pcId));

    private static Task<PcSession> StoredSessionAsync(TestApp app, int id) =>
        app.WithDbAsync(db => db.PcSessions.AsNoTracking().SingleAsync(s => s.Id == id));

    private static Task<HttpResponseMessage> ShutdownAsync(HttpClient admin, int pcId) =>
        admin.PostAsJsonAsync($"/api/pcs/{pcId}/shutdown", new { });

    private static Task<HttpResponseMessage> ShutdownManyAsync(HttpClient admin, params int[] ids) =>
        admin.PostAsJsonAsync("/api/pcs/shutdown", new { ids });

    private static Task<HttpResponseMessage> LockAsync(HttpClient admin, int pcId) =>
        admin.PostAsJsonAsync($"/api/pcs/{pcId}/lock", new { });

    private static async Task<int> AffectedAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await JsonOf(response)).GetProperty("affected").GetInt32();
    }

    // Marks PCs as seen now, so they are online, without a heartbeat's side effects.
    private static Task BringOnlineAsync(TestApp app, params int[] pcIds) =>
        app.WithDbAsync(db => db.Pcs
            .Where(p => pcIds.Contains(p.Id))
            .ExecuteUpdateAsync(setters => setters.SetProperty(p => p.LastSeenUtc, app.Time.GetUtcNow())));

    private static async Task<JsonElement> ListEntryAsync(HttpClient admin, string name)
    {
        var entries = await JsonOf(await admin.GetAsync("/api/pcs"));
        return entries.EnumerateArray().Single(entry => entry.GetProperty("name").GetString() == name);
    }

    // Every admin route refuses anonymous callers, the PC port, and any request without a JSON body.

    [Theory]
    [InlineData("/api/pcs/1/shutdown")]
    [InlineData("/api/pcs/shutdown")]
    public async Task AdminRoutes_WithoutCookie_Return401(string path)
    {
        using var app = new TestApp();
        using var browser = app.CreateBrowser();

        var response = await browser.PostAsJsonAsync(path, new { });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("/api/pcs/1/shutdown")]
    [InlineData("/api/pcs/shutdown")]
    public async Task AdminRoutes_FromThePcPort_Return404(string path)
    {
        using var app = new TestApp();
        using var pcClient = app.CreatePcClient(OtherPcAddress);
        await AddPcAsync(app);

        var response = await pcClient.PostAsJsonAsync(path, new { });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("/api/pcs/{id}/shutdown", "text/plain", "{}")]
    [InlineData("/api/pcs/{id}/shutdown", "application/x-www-form-urlencoded", "")]
    [InlineData("/api/pcs/{id}/shutdown", "application/json", "")]
    [InlineData("/api/pcs/{id}/shutdown", "application/json", "null")]
    [InlineData("/api/pcs/{id}/shutdown", "application/json", "{not json")]
    [InlineData("/api/pcs/shutdown", "text/plain", "{}")]
    [InlineData("/api/pcs/shutdown", "application/x-www-form-urlencoded", "")]
    [InlineData("/api/pcs/shutdown", "application/json", "")]
    [InlineData("/api/pcs/shutdown", "application/json", "null")]
    [InlineData("/api/pcs/shutdown", "application/json", "{not json")]
    public async Task ShutdownRoutes_WithoutAJsonBody_Return400AndChangeNothing(string path, string contentType, string body)
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

    // Single shutdown

    [Fact]
    public async Task Shutdown_OnlinePc_Returns204AndRequestsIt()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var client = app.CreatePcClient(OtherPcAddress);
        var pc = await AddPcAsync(app);
        await HeartbeatAsync(client, pc);

        var response = await ShutdownAsync(admin, pc.Id);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var stored = await StoredPcAsync(app, pc.Id);
        Assert.Equal(app.Time.GetUtcNow().ToUnixTimeMilliseconds(), stored.ShutdownRequestedAtUtc!.Value.ToUnixTimeMilliseconds());
        Assert.Null(stored.ShutdownDeliveredAtUtc);
    }

    [Fact]
    public async Task Shutdown_MissingPc_Returns404()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);

        var response = await ShutdownAsync(admin, 999);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("PC not found.", (await JsonOf(response)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task Shutdown_OfflinePc_Returns409AndChangesNothing()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var client = app.CreatePcClient(OtherPcAddress);
        var pc = await AddPcAsync(app);
        await HeartbeatAsync(client, pc);
        app.Time.Advance(TimeSpan.FromMinutes(1));

        var response = await ShutdownAsync(admin, pc.Id);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(OfflineMessage, (await JsonOf(response)).GetProperty("error").GetString());
        Assert.Null((await StoredPcAsync(app, pc.Id)).ShutdownRequestedAtUtc);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData(TestApp.MachineLanAddress)]
    public async Task Shutdown_ServerPc_Returns409AndChangesNothing(string address)
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var client = app.CreatePcClient(address);
        var pc = await AddPcAsync(app);
        await HeartbeatAsync(client, pc);

        var response = await ShutdownAsync(admin, pc.Id);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(ServerPcMessage, (await JsonOf(response)).GetProperty("error").GetString());
        Assert.Null((await StoredPcAsync(app, pc.Id)).ShutdownRequestedAtUtc);
    }

    [Fact]
    public async Task Shutdown_AlreadyPending_Returns204AndKeepsTheFirstRequest()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var client = app.CreatePcClient(OtherPcAddress);
        var pc = await AddPcAsync(app);
        await HeartbeatAsync(client, pc);
        await ShutdownAsync(admin, pc.Id);
        var firstRequest = (await StoredPcAsync(app, pc.Id)).ShutdownRequestedAtUtc;
        app.Time.Advance(TimeSpan.FromSeconds(10));
        await BringOnlineAsync(app, pc.Id);

        var response = await ShutdownAsync(admin, pc.Id);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(firstRequest, (await StoredPcAsync(app, pc.Id)).ShutdownRequestedAtUtc);
    }

    [Fact]
    public async Task Shutdown_OfLockedPc_KeepsTheLock()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var client = app.CreatePcClient(OtherPcAddress);
        var pc = await AddPcAsync(app);
        await HeartbeatAsync(client, pc);
        Assert.Equal(HttpStatusCode.NoContent, (await LockAsync(admin, pc.Id)).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await ShutdownAsync(admin, pc.Id)).StatusCode);

        var body = await HeartbeatBodyAsync(client, pc);
        Assert.Equal(30, ShutdownSecondsOf(body));
        Assert.True(body.GetProperty("isLocked").GetBoolean());
        Assert.NotNull((await StoredPcAsync(app, pc.Id)).LockedAtUtc);
    }

    // Bulk shutdown

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"ids\":null}")]
    [InlineData("{\"ids\":[]}")]
    [InlineData("{\"ids\":[\"one\"]}")]
    [InlineData("{\"ids\":[1,null]}")]
    public async Task ShutdownMany_WithAMissingOrEmptyList_Return400AndChangesNothing(string body)
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        var pc = await AddPcAsync(app);

        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        var response = await admin.PostAsync("/api/pcs/shutdown", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(InvalidRequestMessage, (await JsonOf(response)).GetProperty("error").GetString());
        Assert.Null((await StoredPcAsync(app, pc.Id)).ShutdownRequestedAtUtc);
    }

    [Fact]
    public async Task ShutdownMany_WithMoreThan1000Ids_Returns400()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        var pc = await AddPcAsync(app);

        var response = await ShutdownManyAsync(admin, Enumerable.Range(1, 1001).ToArray());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(InvalidRequestMessage, (await JsonOf(response)).GetProperty("error").GetString());
        Assert.Null((await StoredPcAsync(app, pc.Id)).ShutdownRequestedAtUtc);
    }

    [Fact]
    public async Task ShutdownMany_With1000Ids_IsAccepted()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);

        Assert.Equal(0, await AffectedAsync(await ShutdownManyAsync(admin, Enumerable.Range(1, 1000).ToArray())));
    }

    [Fact]
    public async Task ShutdownMany_CountsOnlyNewlyRequestedPcs_AndSkipsOfflineServerPendingAndUnknown()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var clientServer = app.CreatePcClient("127.0.0.1");
        var online = await AddPcAsync(app, "PC-01");
        var otherOnline = await AddPcAsync(app, "PC-02");
        var pending = await AddPcAsync(app, "PC-03");
        var offline = await AddPcAsync(app, "PC-04", seenAgo: TimeSpan.FromMinutes(1));
        var serverPc = await AddPcAsync(app, "PC-05");
        await ShutdownAsync(admin, pending.Id);
        var pendingRequest = (await StoredPcAsync(app, pending.Id)).ShutdownRequestedAtUtc;
        app.Time.Advance(TimeSpan.FromSeconds(10));
        await BringOnlineAsync(app, online.Id, otherOnline.Id, pending.Id);
        await HeartbeatAsync(clientServer, serverPc);

        var affected = await AffectedAsync(await ShutdownManyAsync(
            admin, online.Id, otherOnline.Id, pending.Id, offline.Id, serverPc.Id, 999, online.Id));

        Assert.Equal(2, affected);
        Assert.NotNull((await StoredPcAsync(app, online.Id)).ShutdownRequestedAtUtc);
        Assert.NotNull((await StoredPcAsync(app, otherOnline.Id)).ShutdownRequestedAtUtc);
        Assert.Equal(pendingRequest, (await StoredPcAsync(app, pending.Id)).ShutdownRequestedAtUtc);
        Assert.Null((await StoredPcAsync(app, offline.Id)).ShutdownRequestedAtUtc);
        Assert.Null((await StoredPcAsync(app, serverPc.Id)).ShutdownRequestedAtUtc);
    }

    [Fact]
    public async Task ShutdownMany_SecondCall_CountsZero()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        var first = await AddPcAsync(app, "PC-01");
        var second = await AddPcAsync(app, "PC-02");

        Assert.Equal(2, await AffectedAsync(await ShutdownManyAsync(admin, first.Id, second.Id)));
        Assert.Equal(0, await AffectedAsync(await ShutdownManyAsync(admin, first.Id, second.Id)));
    }

    // Delivery on the heartbeat

    [Fact]
    public async Task Heartbeat_AfterARequest_DeliversTheShutdownWith30Seconds()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var client = app.CreatePcClient(OtherPcAddress);
        var pc = await AddPcAsync(app);
        await HeartbeatAsync(client, pc);
        await ShutdownAsync(admin, pc.Id);

        var body = await HeartbeatBodyAsync(client, pc);

        Assert.Equal(30, ShutdownSecondsOf(body));
        var stored = await StoredPcAsync(app, pc.Id);
        Assert.Null(stored.ShutdownRequestedAtUtc);
        Assert.Equal(app.Time.GetUtcNow().ToUnixTimeMilliseconds(), stored.ShutdownDeliveredAtUtc!.Value.ToUnixTimeMilliseconds());
    }

    [Fact]
    public async Task Heartbeat_TwoInARow_DeliversTheShutdownOnlyOnTheFirst()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var client = app.CreatePcClient(OtherPcAddress);
        var pc = await AddPcAsync(app);
        await HeartbeatAsync(client, pc);
        await ShutdownAsync(admin, pc.Id);

        var first = await HeartbeatBodyAsync(client, pc);
        var second = await HeartbeatBodyAsync(client, pc);

        Assert.Equal(30, ShutdownSecondsOf(first));
        Assert.Null(ShutdownSecondsOf(second));
    }

    [Fact]
    public async Task Heartbeat_WithoutARequest_ReturnsANullShutdownSeconds()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient(OtherPcAddress);
        var pc = await AddPcAsync(app);

        var body = await HeartbeatBodyAsync(client, pc);

        Assert.Equal(JsonValueKind.Null, body.GetProperty("shutdownSeconds").ValueKind);
    }

    [Fact]
    public async Task Heartbeat_59SecondsAfterTheRequest_DeliversIt()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var client = app.CreatePcClient(OtherPcAddress);
        var pc = await AddPcAsync(app);
        await HeartbeatAsync(client, pc);
        await ShutdownAsync(admin, pc.Id);
        app.Time.Advance(TimeSpan.FromSeconds(59));

        Assert.Equal(30, ShutdownSecondsOf(await HeartbeatBodyAsync(client, pc)));
    }

    [Fact]
    public async Task Heartbeat_60SecondsAfterTheRequest_DropsItUndelivered()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var client = app.CreatePcClient(OtherPcAddress);
        var pc = await AddPcAsync(app);
        await HeartbeatAsync(client, pc);
        await ShutdownAsync(admin, pc.Id);
        app.Time.Advance(TimeSpan.FromSeconds(60));

        Assert.Null(ShutdownSecondsOf(await HeartbeatBodyAsync(client, pc)));
        var stored = await StoredPcAsync(app, pc.Id);
        Assert.Null(stored.ShutdownRequestedAtUtc);
        Assert.Null(stored.ShutdownDeliveredAtUtc);
    }

    [Fact]
    public async Task Heartbeat_AfterARestartWithinTheWindow_DoesNotDeliverAgain()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var client = app.CreatePcClient(OtherPcAddress);
        var pc = await AddPcAsync(app);
        await HeartbeatAsync(client, pc);
        await ShutdownAsync(admin, pc.Id);
        Assert.Equal(30, ShutdownSecondsOf(await HeartbeatBodyAsync(client, pc)));

        // The PC restarts a few seconds later and reports in again.
        app.Time.Advance(TimeSpan.FromSeconds(5));

        Assert.Null(ShutdownSecondsOf(await HeartbeatBodyAsync(client, pc)));
    }

    [Fact]
    public async Task Heartbeat_AfterThePcComesBack_ForgetsTheDelivery_AndNeverDeliversItAgain()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var client = app.CreatePcClient(OtherPcAddress);
        var pc = await AddPcAsync(app);
        await HeartbeatAsync(client, pc);
        await ShutdownAsync(admin, pc.Id);
        Assert.Equal(30, ShutdownSecondsOf(await HeartbeatBodyAsync(client, pc)));
        app.Time.Advance(TimeSpan.FromMinutes(2));

        var back = await HeartbeatBodyAsync(client, pc);
        app.Time.Advance(TimeSpan.FromSeconds(2));
        var later = await HeartbeatBodyAsync(client, pc);

        Assert.Null(ShutdownSecondsOf(back));
        Assert.Null(ShutdownSecondsOf(later));
        Assert.Null((await StoredPcAsync(app, pc.Id)).ShutdownDeliveredAtUtc);
    }

    // Shutdown state in the PC list

    [Fact]
    public async Task List_PendingRequest_IsShuttingDown()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var client = app.CreatePcClient(OtherPcAddress);
        var pc = await AddPcAsync(app);
        await HeartbeatAsync(client, pc);
        await ShutdownAsync(admin, pc.Id);

        var entry = await ListEntryAsync(admin, "PC-01");

        Assert.Equal("shutting-down", entry.GetProperty("shutdownState").GetString());
    }

    [Fact]
    public async Task List_PendingRequestOfAnOfflinePc_IsStillShuttingDown()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var client = app.CreatePcClient(OtherPcAddress);
        var pc = await AddPcAsync(app);
        await HeartbeatAsync(client, pc);
        await ShutdownAsync(admin, pc.Id);
        app.Time.Advance(TimeSpan.FromSeconds(20));

        var entry = await ListEntryAsync(admin, "PC-01");

        Assert.False(entry.GetProperty("isOnline").GetBoolean());
        Assert.Equal("shutting-down", entry.GetProperty("shutdownState").GetString());
    }

    [Fact]
    public async Task List_DeliveredWhileOnline_IsShuttingDown()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var client = app.CreatePcClient(OtherPcAddress);
        var pc = await AddPcAsync(app);
        await HeartbeatAsync(client, pc);
        await ShutdownAsync(admin, pc.Id);
        await HeartbeatAsync(client, pc);

        var entry = await ListEntryAsync(admin, "PC-01");

        Assert.True(entry.GetProperty("isOnline").GetBoolean());
        Assert.Equal("shutting-down", entry.GetProperty("shutdownState").GetString());
    }

    [Fact]
    public async Task List_DeliveredAndNowOffline_IsShutdown()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var client = app.CreatePcClient(OtherPcAddress);
        var pc = await AddPcAsync(app);
        await HeartbeatAsync(client, pc);
        await ShutdownAsync(admin, pc.Id);
        await HeartbeatAsync(client, pc);
        app.Time.Advance(TimeSpan.FromSeconds(20));

        var entry = await ListEntryAsync(admin, "PC-01");

        Assert.False(entry.GetProperty("isOnline").GetBoolean());
        Assert.Equal("shutdown", entry.GetProperty("shutdownState").GetString());
    }

    [Fact]
    public async Task List_PowerLossWithoutACommand_StaysPlainOffline()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var client = app.CreatePcClient(OtherPcAddress);
        var pc = await AddPcAsync(app);
        await HeartbeatAsync(client, pc);
        app.Time.Advance(TimeSpan.FromMinutes(1));

        var entry = await ListEntryAsync(admin, "PC-01");

        Assert.False(entry.GetProperty("isOnline").GetBoolean());
        Assert.Equal(JsonValueKind.Null, entry.GetProperty("shutdownState").ValueKind);
    }

    [Fact]
    public async Task List_OnlinePcWithoutACommand_HasNoShutdownState()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var client = app.CreatePcClient(OtherPcAddress);
        var pc = await AddPcAsync(app);
        await HeartbeatAsync(client, pc);

        var entry = await ListEntryAsync(admin, "PC-01");

        Assert.True(entry.GetProperty("isOnline").GetBoolean());
        Assert.Equal(JsonValueKind.Null, entry.GetProperty("shutdownState").ValueKind);
    }

    [Fact]
    public async Task List_RequestThatExpiredWhileOffline_IsPlainOffline()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var client = app.CreatePcClient(OtherPcAddress);
        var pc = await AddPcAsync(app);
        await HeartbeatAsync(client, pc);
        await ShutdownAsync(admin, pc.Id);
        app.Time.Advance(TimeSpan.FromSeconds(60));

        var entry = await ListEntryAsync(admin, "PC-01");

        Assert.False(entry.GetProperty("isOnline").GetBoolean());
        Assert.Equal(JsonValueKind.Null, entry.GetProperty("shutdownState").ValueKind);
    }

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("::1", true)]
    [InlineData(TestApp.MachineLanAddress, true)]
    [InlineData(OtherPcAddress, false)]
    public async Task List_IsServerPc_IsTrueOnlyForThisMachinesAddresses(string address, bool expected)
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var client = app.CreatePcClient(address);
        var pc = await AddPcAsync(app);
        await HeartbeatAsync(client, pc);

        var entry = await ListEntryAsync(admin, "PC-01");

        Assert.Equal(expected, entry.GetProperty("isServerPc").GetBoolean());
    }

    // Session end reason on logout

    [Fact]
    public async Task Logout_WithinTwoMinutesOfADelivery_EndsTheSessionAsAdminShutdown()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var client = app.CreatePcClient(OtherPcAddress);
        var pc = await AddPcAsync(app);
        await AddAccountAsync(app, minutes: null);
        await HeartbeatAsync(client, pc);
        var sessionId = await LoggedInSessionIdAsync(client, pc);
        await ShutdownAsync(admin, pc.Id);
        Assert.Equal(30, ShutdownSecondsOf(await HeartbeatBodyAsync(client, pc)));
        app.Time.Advance(TimeSpan.FromSeconds(110));

        var response = await PostAsync(client, "/api/client/logout", pc.Token);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(SessionLifecycle.AdminShutdown, (await StoredSessionAsync(app, sessionId)).EndReason);
    }

    [Fact]
    public async Task Logout_TwoMinutesAfterADelivery_EndsTheSessionAsALogout()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var client = app.CreatePcClient(OtherPcAddress);
        var pc = await AddPcAsync(app);
        await AddAccountAsync(app, minutes: null);
        await HeartbeatAsync(client, pc);
        var sessionId = await LoggedInSessionIdAsync(client, pc);
        await ShutdownAsync(admin, pc.Id);
        Assert.Equal(30, ShutdownSecondsOf(await HeartbeatBodyAsync(client, pc)));
        app.Time.Advance(TimeSpan.FromMinutes(2));

        await PostAsync(client, "/api/client/logout", pc.Token);

        Assert.Equal("logout", (await StoredSessionAsync(app, sessionId)).EndReason);
    }

    [Fact]
    public async Task Logout_WithoutADelivery_EndsTheSessionAsALogout()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient(OtherPcAddress);
        var pc = await AddPcAsync(app);
        await AddAccountAsync(app, minutes: null);
        await HeartbeatAsync(client, pc);
        var sessionId = await LoggedInSessionIdAsync(client, pc);

        await PostAsync(client, "/api/client/logout", pc.Token);

        Assert.Equal("logout", (await StoredSessionAsync(app, sessionId)).EndReason);
    }
}
