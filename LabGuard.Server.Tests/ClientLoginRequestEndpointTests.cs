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

public class ClientLoginRequestEndpointTests
{
    private const string StudentPassword = "student pass 1";
    private const string NotRegisteredMessage = "This PC isn't registered.";
    private const string InvalidRequestMessage = "Invalid request.";
    private const string LockedMessage = "This PC is locked by the administrator.";
    private const string InUseMessage = "This PC is already in use.";
    private const string CreatePath = "/api/client/login-request";
    private const string CancelPath = "/api/client/login-request/cancel";

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

    private static async Task<TestPc> AddPcAsync(TestApp app, string name = "PC-01")
    {
        var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
        var now = app.Time.GetUtcNow();
        var id = await app.WithDbAsync(async db =>
        {
            var pc = new Pc { Name = name, TokenHash = hash, RegisteredAtUtc = now, LastSeenUtc = now };
            db.Pcs.Add(pc);
            await db.SaveChangesAsync();
            return pc.Id;
        });
        return new TestPc(id, token);
    }

    private static async Task AddAccountAsync(TestApp app, string username = "student", int? minutes = 60)
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

    private static Task<HttpResponseMessage> CreateRequestAsync(HttpClient client, TestPc pc) =>
        PostAsync(client, CreatePath, pc.Token, JsonContent.Create(new { }));

    private static Task<HttpResponseMessage> CancelRequestAsync(HttpClient client, TestPc pc) =>
        PostAsync(client, CancelPath, pc.Token, JsonContent.Create(new { }));

    private static async Task<int> CreatedRequestIdAsync(HttpClient client, TestPc pc)
    {
        var response = await CreateRequestAsync(client, pc);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await JsonOf(response)).GetProperty("id").GetInt32();
    }

    private static Task<HttpResponseMessage> LoginAsync(HttpClient client, TestPc pc, string password = StudentPassword) =>
        PostAsync(client, "/api/client/login", pc.Token, JsonContent.Create(new { username = "student", password }));

    private static Task<HttpResponseMessage> HeartbeatAsync(HttpClient client, TestPc pc, int? sessionId = null) =>
        PostAsync(client, "/api/client/heartbeat", pc.Token, sessionId is null ? null : JsonContent.Create(new { sessionId }));

    private static async Task<JsonElement?> HeartbeatLoginRequestAsync(HttpClient client, TestPc pc)
    {
        var body = await JsonOf(await HeartbeatAsync(client, pc));
        return body.TryGetProperty("loginRequest", out var report) ? report : null;
    }

    private static Task<LoginRequest> StoredRequestAsync(TestApp app, int id) =>
        app.WithDbAsync(db => db.LoginRequests.AsNoTracking().SingleAsync(r => r.Id == id));

    private static Task<List<LoginRequest>> RequestsOfAsync(TestApp app, int pcId) =>
        app.WithDbAsync(db => db.LoginRequests.AsNoTracking().Where(r => r.PcId == pcId).OrderBy(r => r.Id).ToListAsync());

    private static Task<HttpResponseMessage> DenyAsync(HttpClient admin, int id) =>
        admin.PostAsJsonAsync($"/api/login-requests/{id}/deny", new { });

    // Create

    [Fact]
    public async Task CreateLoginRequest_ForAPcWithNoRequest_StoresAPendingRequestAndReturnsIt()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app, "PC-07");

        var response = await CreateRequestAsync(client, pc);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await JsonOf(response);
        Assert.Equal(new[] { "id", "requestedAt", "status" }, body.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal("pending", body.GetProperty("status").GetString());
        Assert.Equal(app.Time.GetUtcNow().ToUnixTimeMilliseconds(), body.GetProperty("requestedAt").GetDateTimeOffset().ToUnixTimeMilliseconds());
        var stored = Assert.Single(await RequestsOfAsync(app, pc.Id));
        Assert.Equal(body.GetProperty("id").GetInt32(), stored.Id);
        Assert.Equal(LoginRequestStatus.Pending, stored.Status);
        Assert.Equal("PC-07", stored.PcName);
        Assert.Equal(app.Time.GetUtcNow().ToUnixTimeMilliseconds(), stored.RequestedAtUtc.ToUnixTimeMilliseconds());
        Assert.Null(stored.DecidedAtUtc);
        Assert.Null(stored.Reason);
    }

    [Fact]
    public async Task CreateLoginRequest_WhilePending_ReturnsTheSameRequest_WithoutANewRowOrCooldown()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        var firstId = await CreatedRequestIdAsync(client, pc);
        app.Time.Advance(TimeSpan.FromSeconds(5));

        var again = await CreateRequestAsync(client, pc);

        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(firstId, (await JsonOf(again)).GetProperty("id").GetInt32());
        Assert.Single(await RequestsOfAsync(app, pc.Id));
    }

    [Fact]
    public async Task CreateLoginRequest_WithinTheCooldownAfterACancel_Returns429WithRetryAfter()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        await CreatedRequestIdAsync(client, pc);
        Assert.Equal(HttpStatusCode.NoContent, (await CancelRequestAsync(client, pc)).StatusCode);
        app.Time.Advance(TimeSpan.FromSeconds(10));

        var limited = await CreateRequestAsync(client, pc);

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Equal("20", Assert.Single(limited.Headers.GetValues("Retry-After")));
        var body = await JsonOf(limited);
        Assert.Equal("You can send another request in 20 seconds.", body.GetProperty("error").GetString());
        Assert.Equal(20, body.GetProperty("retryAfterSeconds").GetInt32());
        Assert.Single(await RequestsOfAsync(app, pc.Id));
    }

    [Fact]
    public async Task CreateLoginRequest_AfterTheCooldown_StoresANewRequest()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        var firstId = await CreatedRequestIdAsync(client, pc);
        await CancelRequestAsync(client, pc);
        app.Time.Advance(TimeSpan.FromSeconds(30));

        var secondId = await CreatedRequestIdAsync(client, pc);

        Assert.NotEqual(firstId, secondId);
        Assert.Equal(2, (await RequestsOfAsync(app, pc.Id)).Count);
    }

    [Fact]
    public async Task CreateLoginRequest_AfterTheAdministratorDeniedTheLastOne_IsAlsoCoolingDown()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        var id = await CreatedRequestIdAsync(client, pc);
        Assert.Equal(HttpStatusCode.OK, (await DenyAsync(admin, id)).StatusCode);
        app.Time.Advance(TimeSpan.FromSeconds(29));

        var limited = await CreateRequestAsync(client, pc);

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Equal(1, (await JsonOf(limited)).GetProperty("retryAfterSeconds").GetInt32());
    }

    [Fact]
    public async Task CreateLoginRequest_WhenLockedByTheAdministrator_Returns403AndStoresNothing()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsJsonAsync($"/api/pcs/{pc.Id}/lock", new { })).StatusCode);

        var response = await CreateRequestAsync(client, pc);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(LockedMessage, (await JsonOf(response)).GetProperty("error").GetString());
        Assert.Empty(await RequestsOfAsync(app, pc.Id));
    }

    [Fact]
    public async Task CreateLoginRequest_WhileASessionRuns_Returns409AndStoresNothing()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        await AddAccountAsync(app);
        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(client, pc)).StatusCode);

        var response = await CreateRequestAsync(client, pc);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(InUseMessage, (await JsonOf(response)).GetProperty("error").GetString());
        Assert.Empty(await RequestsOfAsync(app, pc.Id));
    }

    [Fact]
    public async Task CreateLoginRequest_WhenTheSessionHasExpired_StoresARequest()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        await AddAccountAsync(app, minutes: 60);
        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(client, pc)).StatusCode);
        app.Time.Advance(TimeSpan.FromMinutes(61));

        var response = await CreateRequestAsync(client, pc);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single(await RequestsOfAsync(app, pc.Id));
    }

    [Theory]
    [InlineData(CreatePath, null)]
    [InlineData(CreatePath, "nope")]
    [InlineData(CancelPath, null)]
    [InlineData(CancelPath, "nope")]
    public async Task LoginRequestRoutes_WithMissingOrUnknownToken_Return401AndChangeNothing(string path, string? token)
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app);

        var response = await PostAsync(client, path, token, JsonContent.Create(new { }));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(NotRegisteredMessage, (await JsonOf(response)).GetProperty("error").GetString());
        Assert.Empty(await RequestsOfAsync(app, pc.Id));
    }

    [Theory]
    [InlineData(CreatePath, null, null)]
    [InlineData(CreatePath, "", "application/json")]
    [InlineData(CreatePath, "{not json", "application/json")]
    [InlineData(CreatePath, "null", "application/json")]
    [InlineData(CreatePath, "{}", "text/plain")]
    [InlineData(CreatePath, "pcName=PC-01", "application/x-www-form-urlencoded")]
    [InlineData(CancelPath, null, null)]
    [InlineData(CancelPath, "{not json", "application/json")]
    [InlineData(CancelPath, "{}", "text/plain")]
    [InlineData(CancelPath, "pcName=PC-01", "application/x-www-form-urlencoded")]
    public async Task LoginRequestRoutes_WithoutAJsonBody_Return400AndChangeNothing(string path, string? body, string? contentType)
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        var content = body is null || contentType is null ? null : new StringContent(body, Encoding.UTF8, contentType);

        var response = await PostAsync(client, path, pc.Token, content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(InvalidRequestMessage, (await JsonOf(response)).GetProperty("error").GetString());
        Assert.Empty(await RequestsOfAsync(app, pc.Id));
    }

    [Fact]
    public async Task CreateLoginRequest_Concurrently_StoresOnePendingRequest()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app);

        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => CreateRequestAsync(client, pc)));

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));
        var ids = (await Task.WhenAll(responses.Select(async r => (await JsonOf(r)).GetProperty("id").GetInt32()))).Distinct().ToArray();
        Assert.Single(ids);
        var stored = Assert.Single(await RequestsOfAsync(app, pc.Id));
        Assert.Equal(ids[0], stored.Id);
        Assert.Equal(LoginRequestStatus.Pending, stored.Status);
    }

    [Theory]
    [InlineData(CreatePath)]
    [InlineData(CancelPath)]
    public async Task LoginRequestRoutes_OnTheAdminPort_Return404AndChangeNothing(string path)
    {
        using var app = new TestApp();
        using var browser = app.CreateBrowser();
        var pc = await AddPcAsync(app);

        var response = await PostAsync(browser, path, pc.Token, JsonContent.Create(new { }));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(await RequestsOfAsync(app, pc.Id));
    }

    // Cancel

    [Fact]
    public async Task CancelLoginRequest_CancelsThePendingRequest_WithCancelledByPcReason()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        var id = await CreatedRequestIdAsync(client, pc);
        app.Time.Advance(TimeSpan.FromSeconds(3));

        var response = await CancelRequestAsync(client, pc);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var stored = await StoredRequestAsync(app, id);
        Assert.Equal(LoginRequestStatus.Cancelled, stored.Status);
        Assert.Equal("cancelled-by-pc", stored.Reason);
        Assert.Equal(app.Time.GetUtcNow().ToUnixTimeMilliseconds(), stored.DecidedAtUtc!.Value.ToUnixTimeMilliseconds());
    }

    [Fact]
    public async Task CancelLoginRequest_WithNothingPending_Returns204AndKeepsDecidedRequests()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        var id = await CreatedRequestIdAsync(client, pc);
        await DenyAsync(admin, id);

        var response = await CancelRequestAsync(client, pc);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var stored = await StoredRequestAsync(app, id);
        Assert.Equal(LoginRequestStatus.Denied, stored.Status);
        Assert.Null(stored.Reason);
    }

    [Fact]
    public async Task CancelLoginRequest_OnlyCancelsThatPcsRequest()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();
        var first = await AddPcAsync(app, "PC-01");
        var second = await AddPcAsync(app, "PC-02");
        await CreatedRequestIdAsync(client, first);
        var otherId = await CreatedRequestIdAsync(client, second);

        await CancelRequestAsync(client, first);

        Assert.Equal(LoginRequestStatus.Pending, (await StoredRequestAsync(app, otherId)).Status);
    }

    // Heartbeat report

    [Fact]
    public async Task Heartbeat_WithAPendingRequest_ReportsItAsPending()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        var id = await CreatedRequestIdAsync(client, pc);

        var report = await HeartbeatLoginRequestAsync(client, pc);

        Assert.NotNull(report);
        Assert.Equal(new[] { "id", "reason", "status" }, report.Value.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(id, report.Value.GetProperty("id").GetInt32());
        Assert.Equal("pending", report.Value.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, report.Value.GetProperty("reason").ValueKind);
    }

    [Fact]
    public async Task Heartbeat_WithoutAnyRequest_HasNoLoginRequestField()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app);

        Assert.Null(await HeartbeatLoginRequestAsync(client, pc));
    }

    [Fact]
    public async Task Heartbeat_AfterADenial_ReportsItForTwoMinutesOnly()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        var id = await CreatedRequestIdAsync(client, pc);
        await DenyAsync(admin, id);

        app.Time.Advance(TimeSpan.FromSeconds(119));
        var justBefore = await HeartbeatLoginRequestAsync(client, pc);
        app.Time.Advance(TimeSpan.FromSeconds(1));
        var atTwoMinutes = await HeartbeatLoginRequestAsync(client, pc);

        Assert.Equal("denied", justBefore!.Value.GetProperty("status").GetString());
        Assert.Null(atTwoMinutes);
    }

    [Fact]
    public async Task Heartbeat_AfterThePcCancelled_ReportsTheCancelWithItsReason()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        var id = await CreatedRequestIdAsync(client, pc);
        await CancelRequestAsync(client, pc);

        var report = await HeartbeatLoginRequestAsync(client, pc);

        Assert.Equal(id, report!.Value.GetProperty("id").GetInt32());
        Assert.Equal("cancelled", report.Value.GetProperty("status").GetString());
        Assert.Equal("cancelled-by-pc", report.Value.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Heartbeat_ReportsOnlyThePcsLatestRequest()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        var denied = await CreatedRequestIdAsync(client, pc);
        await DenyAsync(admin, denied);
        app.Time.Advance(TimeSpan.FromSeconds(31));
        var latest = await CreatedRequestIdAsync(client, pc);

        var report = await HeartbeatLoginRequestAsync(client, pc);

        Assert.Equal(latest, report!.Value.GetProperty("id").GetInt32());
        Assert.Equal("pending", report.Value.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Heartbeat_NeverReportsAnotherPcsRequest()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();
        var requesting = await AddPcAsync(app, "PC-01");
        var other = await AddPcAsync(app, "PC-02");
        await CreatedRequestIdAsync(client, requesting);

        Assert.Null(await HeartbeatLoginRequestAsync(client, other));
    }

    // Login and removal

    [Fact]
    public async Task Login_Success_CancelsThePendingRequest_WithLoggedInReason()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        await AddAccountAsync(app);
        var id = await CreatedRequestIdAsync(client, pc);

        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(client, pc)).StatusCode);

        var stored = await StoredRequestAsync(app, id);
        Assert.Equal(LoginRequestStatus.Cancelled, stored.Status);
        Assert.Equal("logged-in", stored.Reason);
        Assert.Equal(app.Time.GetUtcNow().ToUnixTimeMilliseconds(), stored.DecidedAtUtc!.Value.ToUnixTimeMilliseconds());
        var report = await HeartbeatLoginRequestAsync(client, pc);
        Assert.Equal("cancelled", report!.Value.GetProperty("status").GetString());
        Assert.Equal("logged-in", report.Value.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task AdminUnlock_CancelsThePendingRequest_WithLoggedInReason()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();
        using var admin = await SignedInAsync(app);
        var pc = await AddPcAsync(app);
        var id = await CreatedRequestIdAsync(client, pc);

        var unlock = await admin.PostAsJsonAsync($"/api/pcs/{pc.Id}/unlock", new { });

        Assert.True(unlock.IsSuccessStatusCode);
        var stored = await StoredRequestAsync(app, id);
        Assert.Equal(LoginRequestStatus.Cancelled, stored.Status);
        Assert.Equal("logged-in", stored.Reason);
    }

    [Fact]
    public async Task Login_Failure_LeavesThePendingRequestAlone()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        await AddAccountAsync(app);
        var id = await CreatedRequestIdAsync(client, pc);

        Assert.Equal(HttpStatusCode.Forbidden, (await LoginAsync(client, pc, "wrong password")).StatusCode);

        Assert.Equal(LoginRequestStatus.Pending, (await StoredRequestAsync(app, id)).Status);
    }

    [Fact]
    public async Task PcRemoval_CancelsItsPendingRequest_WithPcRemovedReason()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        var id = await CreatedRequestIdAsync(client, pc);
        app.Time.Advance(TimeSpan.FromSeconds(20));

        var response = await admin.DeleteAsync($"/api/pcs/{pc.Id}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var stored = await StoredRequestAsync(app, id);
        Assert.Equal(LoginRequestStatus.Cancelled, stored.Status);
        Assert.Equal("pc-removed", stored.Reason);
    }

    // A session that has run out but was never closed. Creating a request must not close it on a refusal.
    private static async Task<int> AddExpiredSessionAsync(TestApp app, TestPc pc)
    {
        var now = app.Time.GetUtcNow();
        return await app.WithDbAsync(async db =>
        {
            var session = new PcSession
            {
                PcId = pc.Id,
                PcName = "PC-01",
                UserAccountId = 1,
                Username = "student",
                StartedAtUtc = now.AddHours(-1),
                ExpiresAtUtc = now.AddMinutes(-1),
                DefaultTimeMinutes = 60,
            };
            db.PcSessions.Add(session);
            await db.SaveChangesAsync();
            return session.Id;
        });
    }

    [Fact]
    public async Task CreateLoginRequest_ReturningThePendingOne_DoesNotCloseAnExpiredSession()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        var id = await CreatedRequestIdAsync(client, pc);
        var sessionId = await AddExpiredSessionAsync(app, pc);

        var again = await CreateRequestAsync(client, pc);

        Assert.Equal(id, (await JsonOf(again)).GetProperty("id").GetInt32());
        var session = await app.WithDbAsync(db => db.PcSessions.AsNoTracking().SingleAsync(s => s.Id == sessionId));
        Assert.Null(session.EndedAtUtc);
    }

    [Fact]
    public async Task CreateLoginRequest_RefusedByTheCooldown_DoesNotCloseAnExpiredSession()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        await CreatedRequestIdAsync(client, pc);
        await CancelRequestAsync(client, pc);
        var sessionId = await AddExpiredSessionAsync(app, pc);
        app.Time.Advance(TimeSpan.FromSeconds(10));

        var limited = await CreateRequestAsync(client, pc);

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        var session = await app.WithDbAsync(db => db.PcSessions.AsNoTracking().SingleAsync(s => s.Id == sessionId));
        Assert.Null(session.EndedAtUtc);
    }

    [Fact]
    public async Task CreateLoginRequest_AfterTheSessionRanOut_StoresARequestAndClosesIt()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        var sessionId = await AddExpiredSessionAsync(app, pc);

        var response = await CreateRequestAsync(client, pc);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var session = await app.WithDbAsync(db => db.PcSessions.AsNoTracking().SingleAsync(s => s.Id == sessionId));
        Assert.Equal(SessionLifecycle.TimeExpired, session.EndReason);
    }

    [Fact]
    public async Task Heartbeat_StopsReportingAnApprovedRequest_WhenItsSessionIsLoggedOut()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        var id = await CreatedRequestIdAsync(client, pc);
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync($"/api/login-requests/{id}/approve", new { time = new { mode = "open-time" } })).StatusCode);
        Assert.Equal("approved", (await HeartbeatLoginRequestAsync(client, pc))!.Value.GetProperty("status").GetString());

        Assert.Equal(HttpStatusCode.NoContent, (await PostAsync(client, "/api/client/logout", pc.Token)).StatusCode);

        Assert.Null(await HeartbeatLoginRequestAsync(client, pc));
    }

    [Fact]
    public async Task Heartbeat_StopsReportingAnApprovedRequest_WhenItsTimeRunsOut()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        var id = await CreatedRequestIdAsync(client, pc);
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync($"/api/login-requests/{id}/approve", new { time = new { mode = "time-limit", minutes = 1 } })).StatusCode);
        app.Time.Advance(TimeSpan.FromMinutes(2));

        var body = await JsonOf(await HeartbeatAsync(client, pc));

        Assert.Equal(JsonValueKind.Null, body.GetProperty("session").ValueKind);
        Assert.Null(await HeartbeatLoginRequestAsync(client, pc));
    }

    // Schema

    [Fact]
    public async Task Migration_AddLoginRequestPcs_IsAppliedAndAddsTheColumnsAndPendingIndex()
    {
        using var app = new TestApp();

        var (applied, pending, sessionColumns, pendingIndexSql) = await app.WithDbAsync(async db => (
            await db.Database.GetAppliedMigrationsAsync(),
            await db.Database.GetPendingMigrationsAsync(),
            await db.Database.SqlQueryRaw<string>("SELECT name AS \"Value\" FROM pragma_table_info('PcSessions')").ToListAsync(),
            await db.Database.SqlQueryRaw<string>("SELECT sql AS \"Value\" FROM sqlite_master WHERE type = 'index' AND name = 'IX_LoginRequests_PcId_Pending'").SingleAsync()));

        Assert.Contains(applied, id => id.EndsWith("_AddLoginRequestPcs", StringComparison.Ordinal));
        Assert.Empty(pending);
        Assert.Contains("LoginRequestId", sessionColumns);
        Assert.Contains("UNIQUE", pendingIndexSql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("'Pending'", pendingIndexSql, StringComparison.Ordinal);
    }
}
