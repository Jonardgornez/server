using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LabGuard.Server.Accounts;
using LabGuard.Server.Data;
using LabGuard.Server.Sessions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LabGuard.Server.Tests;

public class PcTimeEndpointTests
{
    private const string StudentPassword = "student pass 1";
    private const string InvalidRequestMessage = "Invalid request.";
    private const string ActionMessage = "Action must be \"add-time\", \"open-time\" or \"time-out\".";
    private const string MinutesMessage = "Minutes must be a whole number from 1 to 1440.";
    private const string TooMuchTimeMessage = "A session can have at most 24 hours left.";
    private const string OfflineMessage = "This PC is offline.";
    private const string NoSessionMessage = "This PC has no session.";
    private const string UnlockFirstMessage = "Unlock this PC before timing it out.";

    private sealed record TestPc(int Id, string Token);

    private sealed record ReportedTime(string Mode, int? Minutes, int? SecondsRemaining);

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

    // A null minutes value is an open-time account.
    private static async Task AddAccountAsync(TestApp app, int? minutes = null, string username = "student")
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

    private static Task<UserAccount> StoredAccountAsync(TestApp app) =>
        app.WithDbAsync(db => db.UserAccounts.AsNoTracking().SingleAsync());

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

    private static async Task<int> LoggedInSessionIdAsync(HttpClient client, TestPc pc, string username = "student")
    {
        var response = await LoginAsync(client, pc, username);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await JsonOf(response)).GetProperty("sessionId").GetInt32();
    }

    private static Task<PcSession> StoredSessionAsync(TestApp app, int id) =>
        app.WithDbAsync(db => db.PcSessions.AsNoTracking().SingleAsync(s => s.Id == id));

    private static Task<Pc> StoredPcAsync(TestApp app, int pcId) =>
        app.WithDbAsync(db => db.Pcs.AsNoTracking().SingleAsync(p => p.Id == pcId));

    private static Task<HttpResponseMessage> LockAsync(HttpClient admin, int pcId) =>
        admin.PostAsJsonAsync($"/api/pcs/{pcId}/lock", new { });

    private static Task<HttpResponseMessage> UnlockAsync(HttpClient admin, int pcId) =>
        admin.PostAsJsonAsync($"/api/pcs/{pcId}/unlock", new { });

    private static Task<HttpResponseMessage> TimeAsync(HttpClient admin, int pcId, object body) =>
        admin.PostAsJsonAsync($"/api/pcs/{pcId}/time", body);

    private static Task<HttpResponseMessage> TimeManyAsync(HttpClient admin, object body) =>
        admin.PostAsJsonAsync("/api/pcs/time", body);

    private static async Task<int> AffectedAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await JsonOf(response)).GetProperty("affected").GetInt32();
    }

    // A PC with a running session. It logs in from its own address, so several PCs can be signed in at once.
    private static async Task<(TestPc Pc, int SessionId)> PcWithSessionAsync(TestApp app, string name, string remoteIp, string username = "student")
    {
        using var client = app.CreatePcClient(remoteIp);
        var pc = await AddPcAsync(app, name);
        var sessionId = await LoggedInSessionIdAsync(client, pc, username);
        return (pc, sessionId);
    }

    // Marks PCs as seen now, so they are online at this moment, without a heartbeat's side effects on their sessions.
    private static Task BringOnlineAsync(TestApp app, params int[] pcIds) =>
        app.WithDbAsync(db => db.Pcs
            .Where(p => pcIds.Contains(p.Id))
            .ExecuteUpdateAsync(setters => setters.SetProperty(p => p.LastSeenUtc, app.Time.GetUtcNow())));

    private static int? IntOrNull(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null ? value.GetInt32() : null;

    // The time the PC is shown on its heartbeat.
    private static async Task<ReportedTime> HeartbeatTimeAsync(HttpClient client, TestPc pc, int sessionId)
    {
        var session = (await JsonOf(await HeartbeatAsync(client, pc, sessionId))).GetProperty("session");
        return new ReportedTime(
            session.GetProperty("time").GetProperty("mode").GetString()!,
            IntOrNull(session.GetProperty("time"), "minutes"),
            IntOrNull(session, "secondsRemaining"));
    }

    // Every admin route refuses anonymous callers, the PC port, and any request without a JSON body.

    [Fact]
    public async Task TimeRoute_WithoutCookie_Returns401()
    {
        using var app = new TestApp();
        using var browser = app.CreateBrowser();

        var response = await browser.PostAsJsonAsync("/api/pcs/1/time", new { action = "time-out" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task TimeRoute_FromThePcPort_Returns404AndChangesNothing()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        await AddAccountAsync(app, minutes: 10);
        var sessionId = await LoggedInSessionIdAsync(client, pc);

        var response = await PostAsync(client, $"/api/pcs/{pc.Id}/time", null, JsonContent.Create(new { action = "time-out" }));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Null((await StoredSessionAsync(app, sessionId)).EndedAtUtc);
    }

    [Theory]
    [InlineData("text/plain", "{}")]
    [InlineData("application/x-www-form-urlencoded", "")]
    [InlineData("application/json", "")]
    [InlineData("application/json", "null")]
    [InlineData("application/json", "{not json")]
    public async Task TimeRoute_WithoutAJsonBody_Returns400AndChangesNothing(string contentType, string body)
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        await AddAccountAsync(app, minutes: 10);
        var sessionId = await LoggedInSessionIdAsync(client, pc);

        using var content = new StringContent(body, Encoding.UTF8, contentType);
        var response = await admin.PostAsync($"/api/pcs/{pc.Id}/time", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(InvalidRequestMessage, (await JsonOf(response)).GetProperty("error").GetString());
        Assert.Null((await StoredSessionAsync(app, sessionId)).EndedAtUtc);
    }

    [Theory]
    [InlineData("{}", ActionMessage)]
    [InlineData("{\"action\":\"rewind\",\"minutes\":5}", ActionMessage)]
    [InlineData("{\"action\":\"ADD-TIME\",\"minutes\":5}", ActionMessage)]
    [InlineData("{\"action\":\"add-time\"}", MinutesMessage)]
    [InlineData("{\"action\":\"add-time\",\"minutes\":null}", MinutesMessage)]
    [InlineData("{\"action\":\"add-time\",\"minutes\":0}", MinutesMessage)]
    [InlineData("{\"action\":\"add-time\",\"minutes\":-5}", MinutesMessage)]
    [InlineData("{\"action\":\"add-time\",\"minutes\":1441}", MinutesMessage)]
    [InlineData("{\"action\":\"add-time\",\"minutes\":1.5}", MinutesMessage)]
    public async Task TimeRoute_WithAnInvalidBody_Returns400WithTheReason_AndChangesNothing(string body, string error)
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        await AddAccountAsync(app, minutes: 10);
        var sessionId = await LoggedInSessionIdAsync(client, pc);
        var before = await StoredSessionAsync(app, sessionId);

        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        var response = await admin.PostAsync($"/api/pcs/{pc.Id}/time", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(error, (await JsonOf(response)).GetProperty("error").GetString());
        var after = await StoredSessionAsync(app, sessionId);
        Assert.Equal(before.ExpiresAtUtc, after.ExpiresAtUtc);
        Assert.Equal(before.DefaultTimeMinutes, after.DefaultTimeMinutes);
        Assert.Null(after.EndedAtUtc);
    }

    // Errors

    [Fact]
    public async Task TimeRoute_MissingPc_Returns404()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);

        var response = await TimeAsync(admin, 999, new { action = "time-out" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("PC not found.", (await JsonOf(response)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task TimeRoute_OfflinePc_Returns409AndChangesNothing()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        await AddAccountAsync(app, minutes: 10);
        var sessionId = await LoggedInSessionIdAsync(client, pc);
        var before = await StoredSessionAsync(app, sessionId);
        app.Time.Advance(TimeSpan.FromMinutes(1));

        var response = await TimeAsync(admin, pc.Id, new { action = "add-time", minutes = 5 });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(OfflineMessage, (await JsonOf(response)).GetProperty("error").GetString());
        var after = await StoredSessionAsync(app, sessionId);
        Assert.Equal(before.ExpiresAtUtc, after.ExpiresAtUtc);
        Assert.Null(after.EndedAtUtc);
    }

    [Theory]
    [InlineData("{\"action\":\"add-time\",\"minutes\":5}")]
    [InlineData("{\"action\":\"open-time\"}")]
    [InlineData("{\"action\":\"time-out\"}")]
    public async Task TimeRoute_WithoutASession_Returns409(string body)
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        var pc = await AddPcAsync(app);

        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        var response = await admin.PostAsync($"/api/pcs/{pc.Id}/time", content);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(NoSessionMessage, (await JsonOf(response)).GetProperty("error").GetString());
        Assert.Equal(0, await app.WithDbAsync(db => db.PcSessions.CountAsync()));
    }

    [Fact]
    public async Task TimeOut_OnAPausedSession_Returns409AndKeepsTheSession()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        await AddAccountAsync(app, minutes: 10);
        var sessionId = await LoggedInSessionIdAsync(client, pc);
        await BringOnlineAsync(app, pc.Id);
        await LockAsync(admin, pc.Id);
        var pausedAt = (await StoredSessionAsync(app, sessionId)).PausedAtUtc;

        var response = await TimeAsync(admin, pc.Id, new { action = "time-out" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(UnlockFirstMessage, (await JsonOf(response)).GetProperty("error").GetString());
        var session = await StoredSessionAsync(app, sessionId);
        Assert.Null(session.EndedAtUtc);
        Assert.Equal(pausedAt, session.PausedAtUtc);
    }

    // Add time

    [Fact]
    public async Task AddTime_OnATimedSession_MovesTheExpiryLater_AndHeartbeatReportsIt()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        await AddAccountAsync(app, minutes: 10);
        var sessionId = await LoggedInSessionIdAsync(client, pc);
        app.Time.Advance(TimeSpan.FromMinutes(2));
        await BringOnlineAsync(app, pc.Id);

        var response = await TimeAsync(admin, pc.Id, new { action = "add-time", minutes = 5 });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var session = await StoredSessionAsync(app, sessionId);
        Assert.Equal(app.Time.GetUtcNow().AddMinutes(13).ToUnixTimeMilliseconds(), session.ExpiresAtUtc!.Value.ToUnixTimeMilliseconds());
        Assert.Null(session.EndedAtUtc);
        var shown = await HeartbeatTimeAsync(client, pc, sessionId);
        Assert.Equal(new ReportedTime(AccountEndpoints.TimeLimitMode, 13, 780), shown);
    }

    [Fact]
    public async Task AddTime_OnAnOpenTimeSession_TurnsItIntoATimeLimitFromNow()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        await AddAccountAsync(app);
        var sessionId = await LoggedInSessionIdAsync(client, pc);
        app.Time.Advance(TimeSpan.FromMinutes(1));
        await BringOnlineAsync(app, pc.Id);

        var response = await TimeAsync(admin, pc.Id, new { action = "add-time", minutes = 15 });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var session = await StoredSessionAsync(app, sessionId);
        Assert.Equal(15, session.DefaultTimeMinutes);
        Assert.Equal(app.Time.GetUtcNow().AddMinutes(15).ToUnixTimeMilliseconds(), session.ExpiresAtUtc!.Value.ToUnixTimeMilliseconds());
        Assert.Equal(new ReportedTime(AccountEndpoints.TimeLimitMode, 15, 900), await HeartbeatTimeAsync(client, pc, sessionId));
    }

    [Fact]
    public async Task AddTime_OnAPausedTimedSession_ShowsAtOnce_AndResumesWithIt()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        await AddAccountAsync(app, minutes: 10);
        var sessionId = await LoggedInSessionIdAsync(client, pc);
        app.Time.Advance(TimeSpan.FromMinutes(2));
        await BringOnlineAsync(app, pc.Id);
        await LockAsync(admin, pc.Id);

        Assert.Equal(HttpStatusCode.NoContent, (await TimeAsync(admin, pc.Id, new { action = "add-time", minutes = 5 })).StatusCode);

        // Paused: the 8 minutes left plus the 5 added show at once, and the same while the pause lasts.
        var listed = await JsonOf(await admin.GetAsync("/api/pcs"));
        Assert.Equal(780, listed[0].GetProperty("session").GetProperty("secondsRemaining").GetInt32());
        app.Time.Advance(TimeSpan.FromMinutes(3));
        await BringOnlineAsync(app, pc.Id);
        Assert.Equal(HttpStatusCode.NoContent, (await UnlockAsync(admin, pc.Id)).StatusCode);

        Assert.Equal(new ReportedTime(AccountEndpoints.TimeLimitMode, 13, 780), await HeartbeatTimeAsync(client, pc, sessionId));
        Assert.True((await StoredPcAsync(app, pc.Id)).LockedAtUtc is null);
    }

    [Fact]
    public async Task AddTime_OnAPausedOpenTimeSession_GivesExactlyTheMinutes_AndKeepsThePause()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        await AddAccountAsync(app);
        var sessionId = await LoggedInSessionIdAsync(client, pc);
        await BringOnlineAsync(app, pc.Id);
        await LockAsync(admin, pc.Id);
        var pausedAt = (await StoredSessionAsync(app, sessionId)).PausedAtUtc;
        app.Time.Advance(TimeSpan.FromMinutes(3));
        await BringOnlineAsync(app, pc.Id);

        Assert.Equal(HttpStatusCode.NoContent, (await TimeAsync(admin, pc.Id, new { action = "add-time", minutes = 15 })).StatusCode);

        var session = await StoredSessionAsync(app, sessionId);
        Assert.Equal(pausedAt, session.PausedAtUtc);
        Assert.Equal(pausedAt!.Value.AddMinutes(15).ToUnixTimeMilliseconds(), session.ExpiresAtUtc!.Value.ToUnixTimeMilliseconds());
        Assert.Equal(new ReportedTime(AccountEndpoints.TimeLimitMode, 15, 900), await HeartbeatTimeAsync(client, pc, sessionId));
        Assert.Equal(HttpStatusCode.NoContent, (await UnlockAsync(admin, pc.Id)).StatusCode);
        Assert.Equal(new ReportedTime(AccountEndpoints.TimeLimitMode, 15, 900), await HeartbeatTimeAsync(client, pc, sessionId));
    }

    [Fact]
    public async Task AddTime_OnAnAdminUnlock_GivesItATimeLimit()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        Assert.Equal(HttpStatusCode.NoContent, (await UnlockAsync(admin, pc.Id)).StatusCode);
        var unlockId = await app.WithDbAsync(db => db.PcSessions.AsNoTracking().Where(s => s.IsAdminUnlock).Select(s => s.Id).SingleAsync());

        var response = await TimeAsync(admin, pc.Id, new { action = "add-time", minutes = 30 });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(new ReportedTime(AccountEndpoints.TimeLimitMode, 30, 1800), await HeartbeatTimeAsync(client, pc, unlockId));
    }

    // Open time

    [Fact]
    public async Task OpenTime_OnATimedSession_ClearsTheLimit_AndHeartbeatReportsOpenTime()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        await AddAccountAsync(app, minutes: 10);
        var sessionId = await LoggedInSessionIdAsync(client, pc);
        app.Time.Advance(TimeSpan.FromMinutes(2));
        await BringOnlineAsync(app, pc.Id);

        var response = await TimeAsync(admin, pc.Id, new { action = "open-time" });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var session = await StoredSessionAsync(app, sessionId);
        Assert.Null(session.ExpiresAtUtc);
        Assert.Null(session.DefaultTimeMinutes);
        Assert.Null(session.EndedAtUtc);
        Assert.Equal(new ReportedTime(AccountEndpoints.OpenTimeMode, null, null), await HeartbeatTimeAsync(client, pc, sessionId));
    }

    [Fact]
    public async Task OpenTime_WhilePaused_KeepsThePause_AndTheLock()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        await AddAccountAsync(app, minutes: 10);
        var sessionId = await LoggedInSessionIdAsync(client, pc);
        await BringOnlineAsync(app, pc.Id);
        await LockAsync(admin, pc.Id);
        var pausedAt = (await StoredSessionAsync(app, sessionId)).PausedAtUtc;
        app.Time.Advance(TimeSpan.FromMinutes(3));
        await BringOnlineAsync(app, pc.Id);

        Assert.Equal(HttpStatusCode.NoContent, (await TimeAsync(admin, pc.Id, new { action = "open-time" })).StatusCode);

        var session = await StoredSessionAsync(app, sessionId);
        Assert.Equal(pausedAt, session.PausedAtUtc);
        Assert.Null(session.EndedAtUtc);
        Assert.NotNull((await StoredPcAsync(app, pc.Id)).LockedAtUtc);
        Assert.Equal(new ReportedTime(AccountEndpoints.OpenTimeMode, null, null), await HeartbeatTimeAsync(client, pc, sessionId));
    }

    // Time out

    [Fact]
    public async Task TimeOut_EndsTheSession_AndTheHeartbeatReportsTimeExpired()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        await AddAccountAsync(app, minutes: 10);
        var sessionId = await LoggedInSessionIdAsync(client, pc);
        app.Time.Advance(TimeSpan.FromMinutes(2));
        await BringOnlineAsync(app, pc.Id);

        var response = await TimeAsync(admin, pc.Id, new { action = "time-out" });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var session = await StoredSessionAsync(app, sessionId);
        Assert.Equal(SessionLifecycle.TimeExpired, session.EndReason);
        Assert.Equal(app.Time.GetUtcNow().ToUnixTimeMilliseconds(), session.EndedAtUtc!.Value.ToUnixTimeMilliseconds());
        var body = await JsonOf(await HeartbeatAsync(client, pc, sessionId));
        Assert.Equal(JsonValueKind.Null, body.GetProperty("session").ValueKind);
        Assert.Equal("time-expired", body.GetProperty("endedReason").GetString());
    }

    [Fact]
    public async Task TimeOut_OnAnAdminUnlock_EndsIt()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        await UnlockAsync(admin, pc.Id);
        var unlockId = await app.WithDbAsync(db => db.PcSessions.AsNoTracking().Where(s => s.IsAdminUnlock).Select(s => s.Id).SingleAsync());

        var response = await TimeAsync(admin, pc.Id, new { action = "time-out" });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(SessionLifecycle.TimeExpired, (await StoredSessionAsync(app, unlockId)).EndReason);
        Assert.Equal("time-expired", (await JsonOf(await HeartbeatAsync(client, pc, unlockId))).GetProperty("endedReason").GetString());
    }

    // The 24-hour limit

    [Fact]
    public async Task AddTime_ToExactly1440MinutesLeft_IsAccepted_AndOneMoreIsRefused()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        await AddAccountAsync(app, minutes: 1439);
        var sessionId = await LoggedInSessionIdAsync(client, pc);
        await BringOnlineAsync(app, pc.Id);

        Assert.Equal(HttpStatusCode.NoContent, (await TimeAsync(admin, pc.Id, new { action = "add-time", minutes = 1 })).StatusCode);
        Assert.Equal(86400, (await HeartbeatTimeAsync(client, pc, sessionId)).SecondsRemaining);
        var expiresAt = (await StoredSessionAsync(app, sessionId)).ExpiresAtUtc;

        var refused = await TimeAsync(admin, pc.Id, new { action = "add-time", minutes = 1 });

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal(TooMuchTimeMessage, (await JsonOf(refused)).GetProperty("error").GetString());
        Assert.Equal(expiresAt, (await StoredSessionAsync(app, sessionId)).ExpiresAtUtc);
    }

    [Fact]
    public async Task AddTime_OnOpenTime_OfExactly1440Minutes_IsAccepted_AsExactly24Hours()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        await AddAccountAsync(app);
        var sessionId = await LoggedInSessionIdAsync(client, pc);
        await BringOnlineAsync(app, pc.Id);

        Assert.Equal(HttpStatusCode.NoContent, (await TimeAsync(admin, pc.Id, new { action = "add-time", minutes = 1440 })).StatusCode);

        Assert.Equal(new ReportedTime(AccountEndpoints.TimeLimitMode, 1440, 86400), await HeartbeatTimeAsync(client, pc, sessionId));
    }

    [Fact]
    public async Task AddTime_OnAPausedSessionWithLittleLeft_RefusesAnAmountThatWouldPassTheLimit()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        await AddAccountAsync(app, minutes: 1439);
        var sessionId = await LoggedInSessionIdAsync(client, pc);
        await BringOnlineAsync(app, pc.Id);
        await LockAsync(admin, pc.Id);

        var response = await TimeAsync(admin, pc.Id, new { action = "add-time", minutes = 2 });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(TooMuchTimeMessage, (await JsonOf(response)).GetProperty("error").GetString());
        Assert.Equal(86340, (await HeartbeatTimeAsync(client, pc, sessionId)).SecondsRemaining);
    }

    // The account's default time

    [Fact]
    public async Task AllActions_LeaveTheAccountsDefaultTimeAlone()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        await AddAccountAsync(app, minutes: 10);
        await LoggedInSessionIdAsync(client, pc);
        await BringOnlineAsync(app, pc.Id);

        Assert.Equal(HttpStatusCode.NoContent, (await TimeAsync(admin, pc.Id, new { action = "add-time", minutes = 5 })).StatusCode);
        Assert.Equal(10, (await StoredAccountAsync(app)).DefaultTimeMinutes);
        Assert.Equal(HttpStatusCode.NoContent, (await TimeAsync(admin, pc.Id, new { action = "open-time" })).StatusCode);
        Assert.Equal(10, (await StoredAccountAsync(app)).DefaultTimeMinutes);
        Assert.Equal(HttpStatusCode.NoContent, (await TimeAsync(admin, pc.Id, new { action = "time-out" })).StatusCode);

        var account = await StoredAccountAsync(app);
        Assert.Equal(10, account.DefaultTimeMinutes);
        Assert.Equal(SessionLifecycle.TimeExpired, (await app.WithDbAsync(db => db.PcSessions.AsNoTracking().SingleAsync())).EndReason);
    }

    // Bulk time: POST /api/pcs/time

    [Fact]
    public async Task TimeManyRoute_WithoutCookie_Returns401()
    {
        using var app = new TestApp();
        using var browser = app.CreateBrowser();

        var response = await browser.PostAsJsonAsync("/api/pcs/time", new { ids = new[] { 1 }, action = "time-out" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task TimeManyRoute_FromThePcPort_Returns404AndChangesNothing()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var pcClient = app.CreatePcClient();
        await AddAccountAsync(app, minutes: 10);
        var (pc, sessionId) = await PcWithSessionAsync(app, "PC-01", "192.168.1.50");

        var response = await PostAsync(pcClient, "/api/pcs/time", null, JsonContent.Create(new { ids = new[] { pc.Id }, action = "time-out" }));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Null((await StoredSessionAsync(app, sessionId)).EndedAtUtc);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"action\":\"time-out\"}")]
    [InlineData("{\"ids\":null,\"action\":\"time-out\"}")]
    [InlineData("{\"ids\":[],\"action\":\"time-out\"}")]
    [InlineData("{\"ids\":[\"one\"],\"action\":\"time-out\"}")]
    [InlineData("{\"ids\":[1,null],\"action\":\"time-out\"}")]
    [InlineData("{not json")]
    public async Task TimeManyRoute_WithAMissingOrEmptyList_Returns400AndChangesNothing(string body)
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        await AddAccountAsync(app, minutes: 10);
        var (pc, sessionId) = await PcWithSessionAsync(app, "PC-01", "192.168.1.50");

        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        var response = await admin.PostAsync("/api/pcs/time", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(InvalidRequestMessage, (await JsonOf(response)).GetProperty("error").GetString());
        Assert.Null((await StoredSessionAsync(app, sessionId)).EndedAtUtc);
    }

    [Theory]
    [InlineData("text/plain", "{}")]
    [InlineData("application/x-www-form-urlencoded", "")]
    [InlineData("application/json", "")]
    [InlineData("application/json", "null")]
    public async Task TimeManyRoute_WithoutAJsonBody_Returns400AndChangesNothing(string contentType, string body)
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        await AddAccountAsync(app, minutes: 10);
        var (_, sessionId) = await PcWithSessionAsync(app, "PC-01", "192.168.1.50");

        using var content = new StringContent(body, Encoding.UTF8, contentType);
        var response = await admin.PostAsync("/api/pcs/time", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(InvalidRequestMessage, (await JsonOf(response)).GetProperty("error").GetString());
        Assert.Null((await StoredSessionAsync(app, sessionId)).EndedAtUtc);
    }

    [Fact]
    public async Task TimeManyRoute_WithMoreThan1000Ids_Returns400AndChangesNothing()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        await AddAccountAsync(app, minutes: 10);
        var (_, sessionId) = await PcWithSessionAsync(app, "PC-01", "192.168.1.50");

        var response = await TimeManyAsync(admin, new { ids = Enumerable.Range(1, 1001).ToArray(), action = "time-out" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(InvalidRequestMessage, (await JsonOf(response)).GetProperty("error").GetString());
        Assert.Null((await StoredSessionAsync(app, sessionId)).EndedAtUtc);
    }

    [Theory]
    [InlineData("rewind", 5.0, ActionMessage)]
    [InlineData("ADD-TIME", 5.0, ActionMessage)]
    [InlineData("add-time", null, MinutesMessage)]
    [InlineData("add-time", 0.0, MinutesMessage)]
    [InlineData("add-time", 1441.0, MinutesMessage)]
    [InlineData("add-time", 1.5, MinutesMessage)]
    public async Task TimeManyRoute_WithAnInvalidActionOrMinutes_Returns400WithTheReason_AndChangesNothing(string action, double? minutes, string error)
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        await AddAccountAsync(app, minutes: 10);
        var (pc, sessionId) = await PcWithSessionAsync(app, "PC-01", "192.168.1.50");
        var before = await StoredSessionAsync(app, sessionId);

        var response = await TimeManyAsync(admin, new { ids = new[] { pc.Id }, action, minutes });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(error, (await JsonOf(response)).GetProperty("error").GetString());
        var after = await StoredSessionAsync(app, sessionId);
        Assert.Equal(before.ExpiresAtUtc, after.ExpiresAtUtc);
        Assert.Equal(before.DefaultTimeMinutes, after.DefaultTimeMinutes);
        Assert.Null(after.EndedAtUtc);
    }

    [Fact]
    public async Task TimeMany_AddTime_MovesEverySelectedSession_AndCountsEachOnce()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        await AddAccountAsync(app, minutes: 10);
        var first = await PcWithSessionAsync(app, "PC-01", "192.168.1.50");
        var second = await PcWithSessionAsync(app, "PC-02", "192.168.1.51");
        var third = await PcWithSessionAsync(app, "PC-03", "192.168.1.52");
        var expiresBefore = (await StoredSessionAsync(app, first.SessionId)).ExpiresAtUtc!.Value;
        app.Time.Advance(TimeSpan.FromMinutes(2));
        await BringOnlineAsync(app, first.Pc.Id, second.Pc.Id, third.Pc.Id);

        var ids = new[] { first.Pc.Id, second.Pc.Id, third.Pc.Id, first.Pc.Id };
        var affected = await AffectedAsync(await TimeManyAsync(admin, new { ids, action = "add-time", minutes = 5 }));

        Assert.Equal(3, affected);
        foreach (var sessionId in new[] { first.SessionId, second.SessionId, third.SessionId })
        {
            var session = await StoredSessionAsync(app, sessionId);
            Assert.Equal(expiresBefore.AddMinutes(5).ToUnixTimeMilliseconds(), session.ExpiresAtUtc!.Value.ToUnixTimeMilliseconds());
            Assert.Null(session.EndedAtUtc);
        }
    }

    [Fact]
    public async Task TimeMany_OpenTime_TurnsEverySelectedSessionIntoOpenTime()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        await AddAccountAsync(app, minutes: 10);
        var first = await PcWithSessionAsync(app, "PC-01", "192.168.1.50");
        var second = await PcWithSessionAsync(app, "PC-02", "192.168.1.51");
        await BringOnlineAsync(app, first.Pc.Id, second.Pc.Id);

        var affected = await AffectedAsync(await TimeManyAsync(admin, new { ids = new[] { first.Pc.Id, second.Pc.Id }, action = "open-time" }));

        Assert.Equal(2, affected);
        foreach (var sessionId in new[] { first.SessionId, second.SessionId })
        {
            var session = await StoredSessionAsync(app, sessionId);
            Assert.Null(session.ExpiresAtUtc);
            Assert.Null(session.DefaultTimeMinutes);
            Assert.Null(session.EndedAtUtc);
        }
    }

    [Fact]
    public async Task TimeMany_TimeOut_EndsEverySelectedSession()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        await AddAccountAsync(app, minutes: 10);
        var first = await PcWithSessionAsync(app, "PC-01", "192.168.1.50");
        var second = await PcWithSessionAsync(app, "PC-02", "192.168.1.51");
        await BringOnlineAsync(app, first.Pc.Id, second.Pc.Id);

        var affected = await AffectedAsync(await TimeManyAsync(admin, new { ids = new[] { first.Pc.Id, second.Pc.Id }, action = "time-out" }));

        Assert.Equal(2, affected);
        foreach (var sessionId in new[] { first.SessionId, second.SessionId })
        {
            var session = await StoredSessionAsync(app, sessionId);
            Assert.Equal(SessionLifecycle.TimeExpired, session.EndReason);
            Assert.NotNull(session.EndedAtUtc);
        }
    }

    [Fact]
    public async Task TimeMany_SkipsOfflinePcs_PcsWithoutASession_AndUnknownIds()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        await AddAccountAsync(app, minutes: 10);
        var online = await PcWithSessionAsync(app, "PC-01", "192.168.1.50");
        var offline = await PcWithSessionAsync(app, "PC-02", "192.168.1.51");
        var noSession = await AddPcAsync(app, "PC-03");
        app.Time.Advance(TimeSpan.FromMinutes(1));
        await BringOnlineAsync(app, online.Pc.Id, noSession.Id);

        var ids = new[] { online.Pc.Id, offline.Pc.Id, noSession.Id, 999 };
        var affected = await AffectedAsync(await TimeManyAsync(admin, new { ids, action = "time-out" }));

        Assert.Equal(1, affected);
        Assert.NotNull((await StoredSessionAsync(app, online.SessionId)).EndedAtUtc);
        Assert.Null((await StoredSessionAsync(app, offline.SessionId)).EndedAtUtc);
        Assert.Equal(2, await app.WithDbAsync(db => db.PcSessions.CountAsync()));
    }

    [Fact]
    public async Task TimeMany_AddTime_SkipsAPcOverTheDayLimit_WhileTheOthersApply()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        await AddAccountAsync(app, minutes: 1439, username: "long");
        await AddAccountAsync(app, minutes: 10);
        var capped = await PcWithSessionAsync(app, "PC-01", "192.168.1.50", "long");
        var normal = await PcWithSessionAsync(app, "PC-02", "192.168.1.51");
        var cappedBefore = (await StoredSessionAsync(app, capped.SessionId)).ExpiresAtUtc!.Value;
        var normalBefore = (await StoredSessionAsync(app, normal.SessionId)).ExpiresAtUtc!.Value;
        await BringOnlineAsync(app, capped.Pc.Id, normal.Pc.Id);

        var ids = new[] { capped.Pc.Id, normal.Pc.Id };
        var affected = await AffectedAsync(await TimeManyAsync(admin, new { ids, action = "add-time", minutes = 2 }));

        Assert.Equal(1, affected);
        Assert.Equal(cappedBefore.ToUnixTimeMilliseconds(), (await StoredSessionAsync(app, capped.SessionId)).ExpiresAtUtc!.Value.ToUnixTimeMilliseconds());
        Assert.Equal(normalBefore.AddMinutes(2).ToUnixTimeMilliseconds(), (await StoredSessionAsync(app, normal.SessionId)).ExpiresAtUtc!.Value.ToUnixTimeMilliseconds());
    }

    [Fact]
    public async Task TimeMany_TimeOut_SkipsAPausedPc_WhileTheOthersEnd()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        await AddAccountAsync(app, minutes: 10);
        var paused = await PcWithSessionAsync(app, "PC-01", "192.168.1.50");
        var running = await PcWithSessionAsync(app, "PC-02", "192.168.1.51");
        await BringOnlineAsync(app, paused.Pc.Id, running.Pc.Id);
        await LockAsync(admin, paused.Pc.Id);
        var pausedAt = (await StoredSessionAsync(app, paused.SessionId)).PausedAtUtc;

        var affected = await AffectedAsync(await TimeManyAsync(admin, new { ids = new[] { paused.Pc.Id, running.Pc.Id }, action = "time-out" }));

        Assert.Equal(1, affected);
        var pausedSession = await StoredSessionAsync(app, paused.SessionId);
        Assert.Null(pausedSession.EndedAtUtc);
        Assert.Equal(pausedAt, pausedSession.PausedAtUtc);
        Assert.Equal(SessionLifecycle.TimeExpired, (await StoredSessionAsync(app, running.SessionId)).EndReason);
    }
}
