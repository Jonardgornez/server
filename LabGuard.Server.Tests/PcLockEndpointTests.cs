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
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LabGuard.Server.Tests;

public class PcLockEndpointTests
{
    private const string StudentPassword = "student pass 1";
    private const string InvalidRequestMessage = "Invalid request.";
    private const string LockedMessage = "This PC is locked by the administrator.";

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

    private static async Task<int> LoggedInSessionIdAsync(HttpClient client, TestPc pc, string username = "student")
    {
        var response = await LoginAsync(client, pc, username);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await JsonOf(response)).GetProperty("sessionId").GetInt32();
    }

    private static Task<PcSession> StoredSessionAsync(TestApp app, int id) =>
        app.WithDbAsync(db => db.PcSessions.AsNoTracking().SingleAsync(s => s.Id == id));

    private static Task<HttpResponseMessage> LockAsync(HttpClient admin, int pcId) =>
        admin.PostAsJsonAsync($"/api/pcs/{pcId}/lock", new { });

    private static Task<HttpResponseMessage> UnlockAsync(HttpClient admin, int pcId) =>
        admin.PostAsJsonAsync($"/api/pcs/{pcId}/unlock", new { });

    // Every admin route refuses anonymous callers, the PC port, and any request without a JSON body.

    [Theory]
    [InlineData("/api/pcs/1/lock")]
    [InlineData("/api/pcs/1/unlock")]
    [InlineData("/api/pcs/lock-all")]
    [InlineData("/api/pcs/unlock-all")]
    public async Task AdminRoutes_WithoutCookie_Return401(string path)
    {
        using var app = new TestApp();
        using var browser = app.CreateBrowser();

        var response = await browser.PostAsJsonAsync(path, new { });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("/api/pcs/1/lock")]
    [InlineData("/api/pcs/1/unlock")]
    [InlineData("/api/pcs/lock-all")]
    [InlineData("/api/pcs/unlock-all")]
    public async Task AdminRoutes_FromThePcPort_Return404(string path)
    {
        using var app = new TestApp();
        using var pcClient = app.CreatePcClient();
        await AddPcAsync(app);

        var response = await pcClient.PostAsJsonAsync(path, new { });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("/api/pcs/{id}/lock", "text/plain", "{}")]
    [InlineData("/api/pcs/{id}/lock", "application/x-www-form-urlencoded", "")]
    [InlineData("/api/pcs/{id}/lock", "application/json", "")]
    [InlineData("/api/pcs/{id}/lock", "application/json", "null")]
    [InlineData("/api/pcs/{id}/lock", "application/json", "{not json")]
    [InlineData("/api/pcs/{id}/unlock", "text/plain", "{}")]
    [InlineData("/api/pcs/{id}/unlock", "application/x-www-form-urlencoded", "")]
    [InlineData("/api/pcs/{id}/unlock", "application/json", "")]
    [InlineData("/api/pcs/{id}/unlock", "application/json", "null")]
    [InlineData("/api/pcs/{id}/unlock", "application/json", "{not json")]
    [InlineData("/api/pcs/lock-all", "text/plain", "{}")]
    [InlineData("/api/pcs/lock-all", "application/x-www-form-urlencoded", "")]
    [InlineData("/api/pcs/lock-all", "application/json", "")]
    [InlineData("/api/pcs/lock-all", "application/json", "null")]
    [InlineData("/api/pcs/lock-all", "application/json", "{not json")]
    [InlineData("/api/pcs/unlock-all", "text/plain", "{}")]
    [InlineData("/api/pcs/unlock-all", "application/x-www-form-urlencoded", "")]
    [InlineData("/api/pcs/unlock-all", "application/json", "")]
    [InlineData("/api/pcs/unlock-all", "application/json", "null")]
    [InlineData("/api/pcs/unlock-all", "application/json", "{not json")]
    public async Task AdminRoutes_WithoutAJsonBody_Return400AndChangeNothing(string path, string contentType, string body)
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        await AddAccountAsync(app);
        var sessionId = await LoggedInSessionIdAsync(client, pc);

        using var content = new StringContent(body, Encoding.UTF8, contentType);
        var response = await admin.PostAsync(path.Replace("{id}", pc.Id.ToString()), content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(InvalidRequestMessage, (await JsonOf(response)).GetProperty("error").GetString());
        Assert.Null((await StoredSessionAsync(app, sessionId)).EndedAtUtc);
    }

    // Lock

    [Fact]
    public async Task Lock_KeepsTheSession_AndPausesIt()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        await AddAccountAsync(app);
        var sessionId = await LoggedInSessionIdAsync(client, pc);
        app.Time.Advance(TimeSpan.FromSeconds(30));

        var response = await LockAsync(admin, pc.Id);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var session = await StoredSessionAsync(app, sessionId);
        Assert.Null(session.EndedAtUtc);
        Assert.Null(session.EndReason);
        Assert.Equal(app.Time.GetUtcNow().ToUnixTimeMilliseconds(), session.PausedAtUtc!.Value.ToUnixTimeMilliseconds());
        Assert.NotNull((await StoredPcAsync(app, pc.Id)).LockedAtUtc);
    }

    [Fact]
    public async Task Lock_WithoutASession_Returns204AndIsRepeatable()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        var pc = await AddPcAsync(app);

        Assert.Equal(HttpStatusCode.NoContent, (await LockAsync(admin, pc.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await LockAsync(admin, pc.Id)).StatusCode);
        Assert.Equal(0, await app.WithDbAsync(db => db.PcSessions.CountAsync()));
    }

    [Fact]
    public async Task Lock_TwiceKeepsTheOriginalPauseTime()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        await AddAccountAsync(app);
        var sessionId = await LoggedInSessionIdAsync(client, pc);
        await LockAsync(admin, pc.Id);
        var firstPause = (await StoredSessionAsync(app, sessionId)).PausedAtUtc;
        var firstLock = (await StoredPcAsync(app, pc.Id)).LockedAtUtc;
        app.Time.Advance(TimeSpan.FromMinutes(1));

        Assert.Equal(HttpStatusCode.NoContent, (await LockAsync(admin, pc.Id)).StatusCode);

        Assert.Equal(firstPause, (await StoredSessionAsync(app, sessionId)).PausedAtUtc);
        Assert.Equal(firstLock, (await StoredPcAsync(app, pc.Id)).LockedAtUtc);
    }

    [Fact]
    public async Task Lock_TimeDoesNotRunOut_EvenPastTheExpiry()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        await AddAccountAsync(app, minutes: 1);
        var sessionId = await LoggedInSessionIdAsync(client, pc);
        app.Time.Advance(TimeSpan.FromSeconds(20));
        await LockAsync(admin, pc.Id);
        app.Time.Advance(TimeSpan.FromMinutes(5));

        var body = await JsonOf(await HeartbeatAsync(client, pc, sessionId));

        Assert.Equal(sessionId, body.GetProperty("session").GetProperty("sessionId").GetInt32());
        Assert.Equal(40, body.GetProperty("session").GetProperty("secondsRemaining").GetInt32());
        Assert.Null((await StoredSessionAsync(app, sessionId)).EndedAtUtc);
    }

    [Fact]
    public async Task PcList_SecondsRemainingStaysTheSame_WhilePaused()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        await AddAccountAsync(app, minutes: 10);
        await LoggedInSessionIdAsync(client, pc);
        await LockAsync(admin, pc.Id);
        app.Time.Advance(TimeSpan.FromMinutes(12));
        await HeartbeatAsync(client, pc);

        var response = await admin.GetAsync("/api/pcs");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var entries = await JsonOf(response);

        Assert.True(entries[0].GetProperty("isLocked").GetBoolean());
        Assert.Equal(600, entries[0].GetProperty("session").GetProperty("secondsRemaining").GetInt32());
    }

    [Fact]
    public async Task Heartbeat_AfterLock_KeepsReturningThePausedSession_AndTheLock()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        await AddAccountAsync(app);
        var sessionId = await LoggedInSessionIdAsync(client, pc);
        await LockAsync(admin, pc.Id);
        app.Time.Advance(TimeSpan.FromSeconds(2));
        var first = await JsonOf(await HeartbeatAsync(client, pc, sessionId));
        app.Time.Advance(TimeSpan.FromSeconds(2));

        var second = await JsonOf(await HeartbeatAsync(client, pc, sessionId));

        Assert.Equal(sessionId, second.GetProperty("session").GetProperty("sessionId").GetInt32());
        Assert.Equal(JsonValueKind.Null, second.GetProperty("endedReason").ValueKind);
        Assert.True(second.GetProperty("isLocked").GetBoolean());
        Assert.Equal(
            first.GetProperty("session").GetProperty("secondsRemaining").GetInt32(),
            second.GetProperty("session").GetProperty("secondsRemaining").GetInt32());
        Assert.NotNull((await StoredPcAsync(app, pc.Id)).LockedAtUtc);
    }

    [Fact]
    public async Task Lock_MissingPc_Returns404()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);

        var response = await LockAsync(admin, 999);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("PC not found.", (await JsonOf(response)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task Lock_OfflinePc_StillLocksAndPausesItsSession()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        await AddAccountAsync(app);
        var sessionId = await LoggedInSessionIdAsync(client, pc);
        app.Time.Advance(TimeSpan.FromMinutes(1));

        Assert.Equal(HttpStatusCode.NoContent, (await LockAsync(admin, pc.Id)).StatusCode);

        Assert.NotNull((await StoredPcAsync(app, pc.Id)).LockedAtUtc);
        var session = await StoredSessionAsync(app, sessionId);
        Assert.Null(session.EndedAtUtc);
        Assert.NotNull(session.PausedAtUtc);
    }

    [Fact]
    public async Task Lock_ExpiredSession_IsRecordedAsTimeExpired()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        await AddAccountAsync(app, minutes: 1);
        var sessionId = await LoggedInSessionIdAsync(client, pc);
        app.Time.Advance(TimeSpan.FromMinutes(5));

        Assert.Equal(HttpStatusCode.NoContent, (await LockAsync(admin, pc.Id)).StatusCode);

        var session = await StoredSessionAsync(app, sessionId);
        Assert.Equal(SessionLifecycle.TimeExpired, session.EndReason);
        Assert.Equal(session.ExpiresAtUtc, session.EndedAtUtc);
    }

    [Fact]
    public async Task Lock_OnlyPausesThatPcsSession()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var clientA = app.CreatePcClient("192.168.1.50");
        using var clientB = app.CreatePcClient("192.168.1.51");
        var pcA = await AddPcAsync(app, "PC-01");
        var pcB = await AddPcAsync(app, "PC-02");
        await AddAccountAsync(app);
        await LoggedInSessionIdAsync(clientA, pcA);
        var sessionB = await LoggedInSessionIdAsync(clientB, pcB);

        await LockAsync(admin, pcA.Id);

        var session = await StoredSessionAsync(app, sessionB);
        Assert.Null(session.PausedAtUtc);
        Assert.Null((await StoredPcAsync(app, pcB.Id)).LockedAtUtc);
    }

    // Lock all and unlock all

    [Fact]
    public async Task LockAll_EndsEverySession_AndCountsOnlyTheOnesStillRunning()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var clientOnline = app.CreatePcClient("192.168.1.50");
        using var clientOffline = app.CreatePcClient("192.168.1.51");
        using var clientExpired = app.CreatePcClient("192.168.1.52");
        var online = await AddPcAsync(app, "PC-01");
        var offline = await AddPcAsync(app, "PC-02");
        var expired = await AddPcAsync(app, "PC-03");
        var unlocked = await AddPcAsync(app, "PC-04");
        await AddAccountAsync(app);
        await AddAccountAsync(app, minutes: 1, username: "quick");
        await LoggedInSessionIdAsync(clientOnline, online);
        await LoggedInSessionIdAsync(clientOffline, offline);
        await LoggedInSessionIdAsync(clientExpired, expired, "quick");
        Assert.Equal(HttpStatusCode.NoContent, (await UnlockAsync(admin, unlocked.Id)).StatusCode);
        app.Time.Advance(TimeSpan.FromSeconds(60));
        await HeartbeatAsync(clientOnline, online);

        var response = await admin.PostAsJsonAsync("/api/pcs/lock-all", new { });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(3, (await JsonOf(response)).GetProperty("affected").GetInt32());
        var sessions = (await app.WithDbAsync(db => db.PcSessions.AsNoTracking().ToListAsync())).ToDictionary(s => s.PcId);
        Assert.Equal(SessionLifecycle.AdminLock, sessions[online.Id].EndReason);
        Assert.Equal(SessionLifecycle.AdminLock, sessions[offline.Id].EndReason);
        Assert.Equal(SessionLifecycle.TimeExpired, sessions[expired.Id].EndReason);
        Assert.Equal(sessions[expired.Id].ExpiresAtUtc, sessions[expired.Id].EndedAtUtc);
        Assert.Equal(SessionLifecycle.AdminLock, sessions[unlocked.Id].EndReason);
        Assert.Equal(0, await app.WithDbAsync(db => db.PcSessions.CountAsync(s => s.EndedAtUtc == null)));
    }

    [Fact]
    public async Task LockAll_WithNothingRunning_ReturnsZero()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        await AddPcAsync(app);

        var response = await admin.PostAsJsonAsync("/api/pcs/lock-all", new { });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, (await JsonOf(response)).GetProperty("affected").GetInt32());
    }

    [Fact]
    public async Task UnlockAll_OpensOnlyOnlinePcsWithoutARunningSession()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var clientFree = app.CreatePcClient("192.168.1.50");
        using var clientBusy = app.CreatePcClient("192.168.1.51");
        using var clientExpired = app.CreatePcClient("192.168.1.52");
        var free = await AddPcAsync(app, "PC-01");
        var busy = await AddPcAsync(app, "PC-02");
        var offline = await AddPcAsync(app, "PC-03", seenAgo: TimeSpan.FromSeconds(60));
        var expired = await AddPcAsync(app, "PC-04");
        await AddAccountAsync(app);
        await AddAccountAsync(app, minutes: 1, username: "quick");
        await LoggedInSessionIdAsync(clientBusy, busy);
        await LoggedInSessionIdAsync(clientExpired, expired, "quick");
        app.Time.Advance(TimeSpan.FromSeconds(55));
        await HeartbeatAsync(clientFree, free);
        await HeartbeatAsync(clientBusy, busy);
        await HeartbeatAsync(clientExpired, expired);
        app.Time.Advance(TimeSpan.FromSeconds(10));

        var response = await admin.PostAsJsonAsync("/api/pcs/unlock-all", new { });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, (await JsonOf(response)).GetProperty("affected").GetInt32());
        var unlockedPcIds = await app.WithDbAsync(db => db.PcSessions.AsNoTracking().Where(s => s.IsAdminUnlock).Select(s => s.PcId).ToListAsync());
        Assert.Equal(new[] { free.Id, expired.Id }.Order(), unlockedPcIds.Order());
        var expiredLogin = await app.WithDbAsync(db => db.PcSessions.AsNoTracking().SingleAsync(s => s.PcId == expired.Id && !s.IsAdminUnlock));
        Assert.Equal(SessionLifecycle.TimeExpired, expiredLogin.EndReason);
        var busyLogin = await app.WithDbAsync(db => db.PcSessions.AsNoTracking().SingleAsync(s => s.PcId == busy.Id));
        Assert.Null(busyLogin.EndedAtUtc);
        Assert.DoesNotContain(offline.Id, unlockedPcIds);
    }

    [Fact]
    public async Task UnlockAll_AtSixtyPcs_OpensEveryFreeOnlinePc()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        var pcs = new List<TestPc>();
        for (var i = 0; i < 60; i++)
        {
            // Every tenth PC is offline. Every PC whose index is 2 mod 5 is already in use, and those are all online.
            pcs.Add(await AddPcAsync(app, $"PC-{i:00}", seenAgo: i % 10 == 0 ? TimeSpan.FromSeconds(60) : TimeSpan.Zero));
        }

        var now = app.Time.GetUtcNow();
        await app.WithDbAsync(async db =>
        {
            for (var i = 2; i < 60; i += 5)
            {
                db.PcSessions.Add(new PcSession { PcId = pcs[i].Id, PcName = $"PC-{i:00}", UserAccountId = 1, Username = "student", StartedAtUtc = now });
            }

            await db.SaveChangesAsync();
            return 0;
        });

        var response = await admin.PostAsJsonAsync("/api/pcs/unlock-all", new { });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(42, (await JsonOf(response)).GetProperty("affected").GetInt32());
        var unlockedPcIds = await app.WithDbAsync(db => db.PcSessions.AsNoTracking().Where(s => s.IsAdminUnlock).Select(s => s.PcId).ToListAsync());
        Assert.Equal(42, unlockedPcIds.Distinct().Count());
        var skipped = pcs.Where((_, i) => i % 10 == 0 || i % 5 == 2).Select(p => p.Id);
        Assert.Empty(unlockedPcIds.Intersect(skipped));
    }

    [Fact]
    public async Task UnlockAll_SkipsAPcThatLosesARace_AndOpensTheRest()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        var first = await AddPcAsync(app, "PC-01");
        var raced = await AddPcAsync(app, "PC-02");
        var last = await AddPcAsync(app, "PC-03");
        // A login wins the race for the middle PC; the others must still be opened.
        await app.WithDbAsync(db => db.Database.ExecuteSqlRawAsync($"""
            CREATE TRIGGER LoginWinsTheRace BEFORE INSERT ON PcSessions
            WHEN NEW.IsAdminUnlock = 1 AND NEW.PcId = {raced.Id}
            BEGIN
                INSERT INTO PcSessions (PcId, PcName, UserAccountId, Username, StartedAtUtc, IsAdminUnlock)
                VALUES (NEW.PcId, 'PC-02', 1, 'student', NEW.StartedAtUtc, 0);
            END
            """));

        var response = await admin.PostAsJsonAsync("/api/pcs/unlock-all", new { });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, (await JsonOf(response)).GetProperty("affected").GetInt32());
        var unlockedPcIds = await app.WithDbAsync(db => db.PcSessions.AsNoTracking().Where(s => s.IsAdminUnlock).Select(s => s.PcId).ToListAsync());
        Assert.Equal(new[] { first.Id, last.Id }.Order(), unlockedPcIds.Order());
        Assert.Equal(0, await app.WithDbAsync(db => db.PcSessions.CountAsync(s => s.PcId == raced.Id && s.EndedAtUtc == null)));
    }

    // Unlock

    [Fact]
    public async Task Unlock_OnlinePc_OpensAnAdminUnlockSession()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        var pc = await AddPcAsync(app, "PC-07");

        var response = await UnlockAsync(admin, pc.Id);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var session = await app.WithDbAsync(db => db.PcSessions.AsNoTracking().SingleAsync());
        Assert.True(session.IsAdminUnlock);
        Assert.Equal(pc.Id, session.PcId);
        Assert.Equal("PC-07", session.PcName);
        Assert.Equal(0, session.UserAccountId);
        Assert.Equal("Admin unlock", session.Username);
        Assert.Equal(app.Time.GetUtcNow().ToUnixTimeMilliseconds(), session.StartedAtUtc.ToUnixTimeMilliseconds());
        Assert.Null(session.ExpiresAtUtc);
        Assert.Null(session.DefaultTimeMinutes);
        Assert.Null(session.EndedAtUtc);
    }

    [Fact]
    public async Task Unlock_MissingPc_Returns404()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);

        var response = await UnlockAsync(admin, 999);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("PC not found.", (await JsonOf(response)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task Unlock_OfflinePc_Returns409AndOpensNothing()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        var pc = await AddPcAsync(app, seenAgo: TimeSpan.FromSeconds(15));

        var response = await UnlockAsync(admin, pc.Id);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("This PC is offline.", (await JsonOf(response)).GetProperty("error").GetString());
        Assert.Equal(0, await app.WithDbAsync(db => db.PcSessions.CountAsync()));
    }

    [Fact]
    public async Task Unlock_PcWithARunningLogin_Returns409AndKeepsTheLogin()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        await AddAccountAsync(app);
        var sessionId = await LoggedInSessionIdAsync(client, pc);

        var response = await UnlockAsync(admin, pc.Id);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("This PC is already in use.", (await JsonOf(response)).GetProperty("error").GetString());
        var session = await StoredSessionAsync(app, sessionId);
        Assert.False(session.IsAdminUnlock);
        Assert.Null(session.EndedAtUtc);
        Assert.Equal(1, await app.WithDbAsync(db => db.PcSessions.CountAsync()));
    }

    [Fact]
    public async Task Unlock_PcAlreadyUnlocked_Returns409()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        var pc = await AddPcAsync(app);
        Assert.Equal(HttpStatusCode.NoContent, (await UnlockAsync(admin, pc.Id)).StatusCode);

        var response = await UnlockAsync(admin, pc.Id);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("This PC is already in use.", (await JsonOf(response)).GetProperty("error").GetString());
        Assert.Equal(1, await app.WithDbAsync(db => db.PcSessions.CountAsync()));
    }

    [Fact]
    public async Task Unlock_ExpiredLogin_IsClosedAsTimeExpiredAndThePcUnlocks()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        await AddAccountAsync(app, minutes: 1);
        var loginId = await LoggedInSessionIdAsync(client, pc);
        app.Time.Advance(TimeSpan.FromSeconds(55));
        await HeartbeatAsync(client, pc, loginId);
        app.Time.Advance(TimeSpan.FromSeconds(10));

        var response = await UnlockAsync(admin, pc.Id);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var login = await StoredSessionAsync(app, loginId);
        Assert.Equal(SessionLifecycle.TimeExpired, login.EndReason);
        Assert.Equal(login.ExpiresAtUtc, login.EndedAtUtc);
        Assert.Equal(1, await app.WithDbAsync(db => db.PcSessions.CountAsync(s => s.IsAdminUnlock && s.EndedAtUtc == null)));
    }

    [Fact]
    public async Task Unlock_LosingARaceWithALogin_Returns409AndOpensNothing()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        var pc = await AddPcAsync(app);
        // Stands in for a login that starts just before the unlock is saved: this trigger makes that PC busy
        // inside the same insert, so the unique index rejects the unlock.
        await app.WithDbAsync(db => db.Database.ExecuteSqlRawAsync($"""
            CREATE TRIGGER LoginWinsTheRace BEFORE INSERT ON PcSessions
            WHEN NEW.IsAdminUnlock = 1 AND NEW.PcId = {pc.Id}
            BEGIN
                INSERT INTO PcSessions (PcId, PcName, UserAccountId, Username, StartedAtUtc, IsAdminUnlock)
                VALUES (NEW.PcId, 'PC-01', 1, 'student', NEW.StartedAtUtc, 0);
            END
            """));

        var response = await UnlockAsync(admin, pc.Id);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("This PC is already in use.", (await JsonOf(response)).GetProperty("error").GetString());
        Assert.Equal(0, await app.WithDbAsync(db => db.PcSessions.CountAsync(s => s.IsAdminUnlock)));
    }

    [Fact]
    public async Task Unlock_LockedIdlePc_OpensAnAdminUnlock_AndReleasesTheLock()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        var pc = await AddPcAsync(app);
        await LockAsync(admin, pc.Id);

        var response = await UnlockAsync(admin, pc.Id);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var session = await app.WithDbAsync(db => db.PcSessions.AsNoTracking().SingleAsync());
        Assert.True(session.IsAdminUnlock);
        Assert.Null((await StoredPcAsync(app, pc.Id)).LockedAtUtc);
    }

    [Fact]
    public async Task Unlock_LockedPcWithAPausedLogin_ResumesWithTheSameSecondsRemaining()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        await AddAccountAsync(app, minutes: 10);
        var sessionId = await LoggedInSessionIdAsync(client, pc);
        app.Time.Advance(TimeSpan.FromMinutes(2));
        await LockAsync(admin, pc.Id);
        app.Time.Advance(TimeSpan.FromMinutes(20));
        await HeartbeatAsync(client, pc, sessionId);

        var response = await UnlockAsync(admin, pc.Id);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var session = await StoredSessionAsync(app, sessionId);
        Assert.Null(session.PausedAtUtc);
        Assert.Null(session.EndedAtUtc);
        Assert.Null((await StoredPcAsync(app, pc.Id)).LockedAtUtc);
        var body = await JsonOf(await HeartbeatAsync(client, pc, sessionId));
        Assert.Equal(480, body.GetProperty("session").GetProperty("secondsRemaining").GetInt32());
        Assert.False(body.GetProperty("isLocked").GetBoolean());
    }

    [Fact]
    public async Task Unlock_LockedPcWithAPausedAdminUnlock_ResumesIt()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        Assert.Equal(HttpStatusCode.NoContent, (await UnlockAsync(admin, pc.Id)).StatusCode);
        var unlockId = await app.WithDbAsync(db => db.PcSessions.AsNoTracking().Where(s => s.IsAdminUnlock).Select(s => s.Id).SingleAsync());
        app.Time.Advance(TimeSpan.FromSeconds(10));
        await LockAsync(admin, pc.Id);
        app.Time.Advance(TimeSpan.FromMinutes(1));
        await HeartbeatAsync(client, pc, unlockId);

        var response = await UnlockAsync(admin, pc.Id);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var session = await StoredSessionAsync(app, unlockId);
        Assert.Null(session.EndedAtUtc);
        Assert.Null(session.PausedAtUtc);
        Assert.Equal(1, await app.WithDbAsync(db => db.PcSessions.CountAsync(s => s.EndedAtUtc == null)));
        Assert.Null((await StoredPcAsync(app, pc.Id)).LockedAtUtc);
    }

    [Fact]
    public async Task Unlock_OfflineLockedPc_Returns409AndStaysLocked()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        var pc = await AddPcAsync(app, seenAgo: TimeSpan.FromMinutes(1));
        Assert.Equal(HttpStatusCode.NoContent, (await LockAsync(admin, pc.Id)).StatusCode);

        var response = await UnlockAsync(admin, pc.Id);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("This PC is offline.", (await JsonOf(response)).GetProperty("error").GetString());
        Assert.NotNull((await StoredPcAsync(app, pc.Id)).LockedAtUtc);
        Assert.Equal(0, await app.WithDbAsync(db => db.PcSessions.CountAsync()));
    }

    [Fact]
    public async Task Unlock_PauseLaterThanTheClock_NeverShortensTheSession()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        await AddAccountAsync(app, minutes: 10);
        var sessionId = await LoggedInSessionIdAsync(client, pc);
        var expiresAt = (await StoredSessionAsync(app, sessionId)).ExpiresAtUtc;
        await LockAsync(admin, pc.Id);
        // The clock moved back since the pause, so the pause now lies in the future. The fake clock can't go back, so the stored pause is moved forward instead.
        var laterThanClock = app.Time.GetUtcNow().AddMinutes(5);
        await app.WithDbAsync(db => db.PcSessions
            .Where(s => s.Id == sessionId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(s => s.PausedAtUtc, laterThanClock)));
        await HeartbeatAsync(client, pc);

        Assert.Equal(HttpStatusCode.NoContent, (await UnlockAsync(admin, pc.Id)).StatusCode);

        Assert.Equal(expiresAt, (await StoredSessionAsync(app, sessionId)).ExpiresAtUtc);
    }

    [Fact]
    public async Task StaleAdminUnlock_WhileLocked_IsEnded_AndThePcStaysLocked()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        var response = await UnlockAsync(admin, pc.Id);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var unlockId = await app.WithDbAsync(db => db.PcSessions.AsNoTracking().Where(s => s.IsAdminUnlock).Select(s => s.Id).SingleAsync());
        app.Time.Advance(TimeSpan.FromSeconds(5));
        await LockAsync(admin, pc.Id);
        app.Time.Advance(TimeSpan.FromSeconds(60));

        var body = await JsonOf(await HeartbeatAsync(client, pc));

        Assert.Equal(JsonValueKind.Null, body.GetProperty("session").ValueKind);
        Assert.Equal(SessionLifecycle.ConnectionLost, (await StoredSessionAsync(app, unlockId)).EndReason);
        Assert.NotNull((await StoredPcAsync(app, pc.Id)).LockedAtUtc);
        Assert.True((await JsonOf(await HeartbeatAsync(client, pc))).GetProperty("isLocked").GetBoolean());
    }

    [Fact]
    public async Task Logout_WhileLocked_EndsTheSession_AndTheLockStays()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        await AddAccountAsync(app);
        var sessionId = await LoggedInSessionIdAsync(client, pc);
        await LockAsync(admin, pc.Id);

        var response = await PostAsync(client, "/api/client/logout", pc.Token);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var session = await StoredSessionAsync(app, sessionId);
        Assert.Equal("logout", session.EndReason);
        Assert.NotNull(session.EndedAtUtc);
        Assert.NotNull((await StoredPcAsync(app, pc.Id)).LockedAtUtc);
    }

    [Fact]
    public async Task DisablingTheAccount_WhileLocked_EndsTheSession_AndTheLockStays()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        await AddAccountAsync(app);
        var sessionId = await LoggedInSessionIdAsync(client, pc);
        await LockAsync(admin, pc.Id);
        var accountId = await app.WithDbAsync(db => db.UserAccounts.Select(u => u.Id).SingleAsync());

        var response = await admin.PatchAsJsonAsync($"/api/accounts/{accountId}/status", new { isActive = false });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(SessionLifecycle.AccountDisabled, (await StoredSessionAsync(app, sessionId)).EndReason);
        Assert.NotNull((await StoredPcAsync(app, pc.Id)).LockedAtUtc);
    }

    [Fact]
    public async Task Login_AfterUnlock_IsAllowedAgain()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        await AddAccountAsync(app);
        await LockAsync(admin, pc.Id);
        await UnlockAsync(admin, pc.Id);

        var response = await LoginAsync(client, pc);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // Login while locked

    [Theory]
    [InlineData(StudentPassword)]
    [InlineData("wrong guess")]
    public async Task Login_WhileLocked_IsRefusedWithoutCountingAnAttempt(string password)
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        await AddAccountAsync(app);
        await LockDirectlyAsync(app, pc.Id);

        var response = await PostAsync(client, "/api/client/login", pc.Token, JsonContent.Create(new { username = "student", password }));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(LockedMessage, (await JsonOf(response)).GetProperty("error").GetString());
        var stored = await StoredPcAsync(app, pc.Id);
        Assert.Equal(0, stored.FailedLoginCount);
        Assert.Null(stored.LoginLockedUntilUtc);
        Assert.Equal(0, await app.WithDbAsync(db => db.PcSessions.CountAsync()));
    }

    [Fact]
    public async Task Login_LockLandingDuringThePasswordCheck_IsRefusedAndGivesTheAttemptBack()
    {
        var hasher = new HookedPasswordHasher();
        using var app = new TestApp(configureServices: services => UseHasher(services, hasher));
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        await AddAccountAsync(app);
        hasher.BeforeVerify = () => LockDirectlyAsync(app, pc.Id).GetAwaiter().GetResult();

        var response = await LoginAsync(client, pc);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(LockedMessage, (await JsonOf(response)).GetProperty("error").GetString());
        Assert.Equal(0, (await StoredPcAsync(app, pc.Id)).FailedLoginCount);
        Assert.Equal(0, await app.WithDbAsync(db => db.PcSessions.CountAsync()));
    }

    [Fact]
    public async Task Heartbeat_ReportsIsLocked_AndDoesNotClearTheLock()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        Assert.False((await JsonOf(await HeartbeatAsync(client, pc))).GetProperty("isLocked").GetBoolean());
        await LockDirectlyAsync(app, pc.Id);
        var lockedAt = (await StoredPcAsync(app, pc.Id)).LockedAtUtc;

        for (var i = 0; i < 3; i++)
        {
            app.Time.Advance(TimeSpan.FromSeconds(2));
            var body = await JsonOf(await HeartbeatAsync(client, pc));
            Assert.True(body.GetProperty("isLocked").GetBoolean());
        }

        Assert.Equal(lockedAt, (await StoredPcAsync(app, pc.Id)).LockedAtUtc);
    }

    private static async Task LockDirectlyAsync(TestApp app, int pcId)
    {
        var now = app.Time.GetUtcNow();
        await app.WithDbAsync(db => db.Pcs
            .Where(p => p.Id == pcId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(p => p.LockedAtUtc, now)));
    }

    private static Task<Pc> StoredPcAsync(TestApp app, int pcId) =>
        app.WithDbAsync(db => db.Pcs.AsNoTracking().SingleAsync(p => p.Id == pcId));

    private static void UseHasher(IServiceCollection services, IPasswordHasher<UserAccount> hasher)
    {
        services.RemoveAll<IPasswordHasher<UserAccount>>();
        services.AddSingleton(hasher);
    }

    // Runs a hook before each password check, so a test can change the PC while a login is verifying.
    private sealed class HookedPasswordHasher : IPasswordHasher<UserAccount>
    {
        private readonly PasswordHasher<UserAccount> _inner = new();

        public Action? BeforeVerify { get; set; }

        public string HashPassword(UserAccount user, string password) => _inner.HashPassword(user, password);

        public PasswordVerificationResult VerifyHashedPassword(UserAccount user, string hashedPassword, string providedPassword)
        {
            BeforeVerify?.Invoke();
            return _inner.VerifyHashedPassword(user, hashedPassword, providedPassword);
        }
    }
}
