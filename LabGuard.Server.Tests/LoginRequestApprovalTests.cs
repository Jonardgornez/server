using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LabGuard.Server.Data;
using LabGuard.Server.Sessions;
using Microsoft.EntityFrameworkCore;

namespace LabGuard.Server.Tests;

public class LoginRequestApprovalTests
{
    private const string OfflineMessage = "That PC is offline, so the request was closed.";
    private const string RemovedMessage = "That PC is no longer registered, so the request was closed.";
    private const string UnknownMessage = "This request came from an older version and can't be approved. Ask the student to request again.";
    private const string LockedMessage = "That PC is locked. Unlock it first, then ask the student to request again.";
    private const string InUseMessage = "Someone is already using that PC, so the request was closed.";
    private const string AlreadyDecidedMessage = "This request has already been decided.";
    private const string OpenFailedMessage = "Couldn't open that session. Try again.";
    private static readonly object ThirtyMinutes = new { mode = "time-limit", minutes = 30 };
    private static readonly object OpenTime = new { mode = "open-time" };

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

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string path, string? token, HttpContent? content = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = content };
        if (token is not null)
        {
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
        }

        return await client.SendAsync(request);
    }

    // The PC asks for a login, as it does from the lock screen, and returns the new request's id.
    private static async Task<int> RequestAsync(HttpClient pc, TestPc pcInfo)
    {
        var response = await PostAsync(pc, "/api/client/login-request", pcInfo.Token, JsonContent.Create(new { }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await JsonOf(response)).GetProperty("id").GetInt32();
    }

    private static Task<HttpResponseMessage> HeartbeatAsync(HttpClient client, TestPc pc, int? sessionId = null) =>
        PostAsync(client, "/api/client/heartbeat", pc.Token, sessionId is null ? null : JsonContent.Create(new { sessionId }));

    private static Task<HttpResponseMessage> ApproveAsync(HttpClient admin, int id, object time) =>
        admin.PostAsJsonAsync($"/api/login-requests/{id}/approve", new { time });

    private static Task<HttpResponseMessage> DenyAsync(HttpClient admin, int id) =>
        admin.PostAsJsonAsync($"/api/login-requests/{id}/deny", new { });

    private static Task<LoginRequest> StoredRequestAsync(TestApp app, int id) =>
        app.WithDbAsync(db => db.LoginRequests.AsNoTracking().SingleAsync(r => r.Id == id));

    private static Task<List<PcSession>> SessionsOfAsync(TestApp app, int pcId) =>
        app.WithDbAsync(db => db.PcSessions.AsNoTracking().Where(s => s.PcId == pcId).OrderBy(s => s.Id).ToListAsync());

    private static Task<List<PcSession>> OpenSessionsOfAsync(TestApp app, int pcId) =>
        app.WithDbAsync(db => db.PcSessions.AsNoTracking().Where(s => s.PcId == pcId && s.EndedAtUtc == null).ToListAsync());

    private static async Task AssertRefusedAsync(TestApp app, HttpResponseMessage response, int requestId, string message, string reason)
    {
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(message, (await JsonOf(response)).GetProperty("error").GetString());
        var stored = await StoredRequestAsync(app, requestId);
        Assert.Equal(LoginRequestStatus.Denied, stored.Status);
        Assert.Equal(reason, stored.Reason);
        Assert.Null(stored.ApprovedMinutes);
        Assert.Equal(app.Time.GetUtcNow().ToUnixTimeMilliseconds(), stored.DecidedAtUtc!.Value.ToUnixTimeMilliseconds());
    }

    // Opening the session

    [Fact]
    public async Task Approve_WithATimeLimit_OpensASessionThatThePcAdopts()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var pcClient = app.CreatePcClient();
        var pc = await AddPcAsync(app, "PC-07");
        var requestId = await RequestAsync(pcClient, pc);

        var response = await ApproveAsync(admin, requestId, ThirtyMinutes);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("approved", (await JsonOf(response)).GetProperty("status").GetString());
        var session = Assert.Single(await SessionsOfAsync(app, pc.Id));
        Assert.Equal(requestId, session.LoginRequestId);
        Assert.Equal(0, session.UserAccountId);
        Assert.Equal("Login request", session.Username);
        Assert.Equal("PC-07", session.PcName);
        Assert.False(session.IsAdminUnlock);
        Assert.Equal(30, session.DefaultTimeMinutes);
        Assert.Equal(app.Time.GetUtcNow().AddMinutes(30).ToUnixTimeMilliseconds(), session.ExpiresAtUtc!.Value.ToUnixTimeMilliseconds());
        Assert.Null(session.EndedAtUtc);
        var stored = await StoredRequestAsync(app, requestId);
        Assert.Equal(LoginRequestStatus.Approved, stored.Status);
        Assert.Equal(30, stored.ApprovedMinutes);
        Assert.Null(stored.Reason);

        var listed = (await JsonOf(await admin.GetAsync("/api/pcs"))).EnumerateArray().Single(e => e.GetProperty("id").GetInt32() == pc.Id);
        Assert.Equal("Login request", listed.GetProperty("session").GetProperty("username").GetString());

        app.Time.Advance(TimeSpan.FromSeconds(2));
        var body = await JsonOf(await HeartbeatAsync(pcClient, pc));
        var heartbeatSession = body.GetProperty("session");
        Assert.Equal(session.Id, heartbeatSession.GetProperty("sessionId").GetInt32());
        Assert.True(heartbeatSession.GetProperty("isLoginRequest").GetBoolean());
        Assert.False(heartbeatSession.GetProperty("isAdminUnlock").GetBoolean());
        Assert.Equal("time-limit", heartbeatSession.GetProperty("time").GetProperty("mode").GetString());
        var report = body.GetProperty("loginRequest");
        Assert.Equal(requestId, report.GetProperty("id").GetInt32());
        Assert.Equal("approved", report.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, report.GetProperty("reason").ValueKind);
    }

    [Fact]
    public async Task Approve_WithOpenTime_OpensAnOpenTimeSession()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var pcClient = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        var requestId = await RequestAsync(pcClient, pc);

        var response = await ApproveAsync(admin, requestId, OpenTime);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var session = Assert.Single(await SessionsOfAsync(app, pc.Id));
        Assert.Null(session.ExpiresAtUtc);
        Assert.Null(session.DefaultTimeMinutes);
        Assert.Null(session.EndedAtUtc);
        var body = await JsonOf(await HeartbeatAsync(pcClient, pc));
        var heartbeatSession = body.GetProperty("session");
        Assert.Equal("open-time", heartbeatSession.GetProperty("time").GetProperty("mode").GetString());
        Assert.Equal(JsonValueKind.Null, heartbeatSession.GetProperty("secondsRemaining").ValueKind);
        Assert.True(heartbeatSession.GetProperty("isLoginRequest").GetBoolean());
    }

    [Fact]
    public async Task Approve_AnOfflinePcThatDoesNotKnowTheSession_EndsItAsConnectionLost()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var pcClient = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        var requestId = await RequestAsync(pcClient, pc);
        Assert.Equal(HttpStatusCode.OK, (await ApproveAsync(admin, requestId, ThirtyMinutes)).StatusCode);
        var sessionId = Assert.Single(await SessionsOfAsync(app, pc.Id)).Id;
        app.Time.Advance(TimeSpan.FromSeconds(60));

        var body = await JsonOf(await HeartbeatAsync(pcClient, pc));

        Assert.Equal(JsonValueKind.Null, body.GetProperty("session").ValueKind);
        var stored = await app.WithDbAsync(db => db.PcSessions.AsNoTracking().SingleAsync(s => s.Id == sessionId));
        Assert.Equal(SessionLifecycle.ConnectionLost, stored.EndReason);
    }

    [Fact]
    public async Task Approve_AnOfflinePcThatReportsTheSession_KeepsIt()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var pcClient = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        var requestId = await RequestAsync(pcClient, pc);
        Assert.Equal(HttpStatusCode.OK, (await ApproveAsync(admin, requestId, ThirtyMinutes)).StatusCode);
        var sessionId = Assert.Single(await SessionsOfAsync(app, pc.Id)).Id;
        app.Time.Advance(TimeSpan.FromSeconds(20));

        var body = await JsonOf(await HeartbeatAsync(pcClient, pc, sessionId));

        Assert.Equal(sessionId, body.GetProperty("session").GetProperty("sessionId").GetInt32());
        Assert.Null((await app.WithDbAsync(db => db.PcSessions.AsNoTracking().SingleAsync(s => s.Id == sessionId))).EndedAtUtc);
    }

    [Fact]
    public async Task Approve_OverAnExpiredSession_EndsItAndOpensTheNewOne()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var pcClient = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        var requestId = await RequestAsync(pcClient, pc);
        var now = app.Time.GetUtcNow();
        var expiredId = await app.WithDbAsync(async db =>
        {
            var expired = new PcSession
            {
                PcId = pc.Id,
                PcName = "PC-01",
                UserAccountId = 1,
                Username = "student",
                StartedAtUtc = now.AddHours(-1),
                ExpiresAtUtc = now.AddMinutes(-1),
                DefaultTimeMinutes = 60,
            };
            db.PcSessions.Add(expired);
            await db.SaveChangesAsync();
            return expired.Id;
        });

        var response = await ApproveAsync(admin, requestId, ThirtyMinutes);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var expiredSession = await app.WithDbAsync(db => db.PcSessions.AsNoTracking().SingleAsync(s => s.Id == expiredId));
        Assert.Equal(SessionLifecycle.TimeExpired, expiredSession.EndReason);
        var open = Assert.Single(await OpenSessionsOfAsync(app, pc.Id));
        Assert.Equal(requestId, open.LoginRequestId);
    }

    [Fact]
    public async Task Approve_Twice_Returns409AndOpensOnlyOneSession()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var pcClient = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        var requestId = await RequestAsync(pcClient, pc);
        Assert.Equal(HttpStatusCode.OK, (await ApproveAsync(admin, requestId, ThirtyMinutes)).StatusCode);

        var second = await ApproveAsync(admin, requestId, OpenTime);

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal(AlreadyDecidedMessage, (await JsonOf(second)).GetProperty("error").GetString());
        Assert.Single(await SessionsOfAsync(app, pc.Id));
        Assert.Equal(30, (await StoredRequestAsync(app, requestId)).ApprovedMinutes);
    }

    [Fact]
    public async Task Approve_AfterThePcCancelled_Returns409AndOpensNothing()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var pcClient = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        var requestId = await RequestAsync(pcClient, pc);
        await PostAsync(pcClient, "/api/client/login-request/cancel", pc.Token, JsonContent.Create(new { }));

        var response = await ApproveAsync(admin, requestId, ThirtyMinutes);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(AlreadyDecidedMessage, (await JsonOf(response)).GetProperty("error").GetString());
        Assert.Equal(LoginRequestStatus.Cancelled, (await StoredRequestAsync(app, requestId)).Status);
        Assert.Empty(await SessionsOfAsync(app, pc.Id));
    }

    [Fact]
    public async Task Approve_WhenASessionAppearsAtEveryInsert_Returns409AndLeavesTheRequestPending()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var pcClient = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        var requestId = await RequestAsync(pcClient, pc);
        // Every login session insert also writes a conflicting session for the same PC, so the unique index refuses it each time.
        await app.WithDbAsync(db => db.Database.ExecuteSqlRawAsync("""
            CREATE TRIGGER OtherLoginWinsTheRace BEFORE INSERT ON PcSessions
            WHEN NEW.LoginRequestId IS NOT NULL
            BEGIN
                INSERT INTO PcSessions (PcId, PcName, UserAccountId, Username, StartedAtUtc, IsAdminUnlock)
                VALUES (NEW.PcId, 'PC-01', 1, 'student', NEW.StartedAtUtc, 0);
            END
            """));

        var response = await ApproveAsync(admin, requestId, ThirtyMinutes);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(OpenFailedMessage, (await JsonOf(response)).GetProperty("error").GetString());
        Assert.Equal(LoginRequestStatus.Pending, (await StoredRequestAsync(app, requestId)).Status);
        Assert.Empty(await app.WithDbAsync(db => db.PcSessions.AsNoTracking().Where(s => s.LoginRequestId != null).ToListAsync()));
        Assert.Empty(await OpenSessionsOfAsync(app, pc.Id));
    }

    // Refusals: each closes the request as denied, with its reason, and opens nothing

    [Fact]
    public async Task Approve_WithNoPcLink_ClosesTheRequestAsPcUnknown()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        var requestId = await app.WithDbAsync(async db =>
        {
            var request = new LoginRequest { PcName = "PC-11", RequestedAtUtc = app.Time.GetUtcNow(), Status = LoginRequestStatus.Pending };
            db.LoginRequests.Add(request);
            await db.SaveChangesAsync();
            return request.Id;
        });

        var response = await ApproveAsync(admin, requestId, ThirtyMinutes);

        await AssertRefusedAsync(app, response, requestId, UnknownMessage, "pc-unknown");
        Assert.Empty(await app.WithDbAsync(db => db.PcSessions.AsNoTracking().ToListAsync()));
    }

    [Fact]
    public async Task Approve_WhenThePcRowIsGone_ClosesTheRequestAsPcRemoved()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var pcClient = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        var requestId = await RequestAsync(pcClient, pc);
        await app.WithDbAsync(db => db.Pcs.Where(p => p.Id == pc.Id).ExecuteDeleteAsync());

        var response = await ApproveAsync(admin, requestId, ThirtyMinutes);

        await AssertRefusedAsync(app, response, requestId, RemovedMessage, "pc-removed");
    }

    [Fact]
    public async Task Approve_WhenThePcIsOffline_ClosesTheRequestAsPcOffline_AndThePcLearnsIt()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var pcClient = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        var requestId = await RequestAsync(pcClient, pc);
        app.Time.Advance(TimeSpan.FromSeconds(20));

        var response = await ApproveAsync(admin, requestId, ThirtyMinutes);

        await AssertRefusedAsync(app, response, requestId, OfflineMessage, "pc-offline");
        Assert.Empty(await SessionsOfAsync(app, pc.Id));
        var history = (await JsonOf(await admin.GetAsync("/api/login-requests"))).GetProperty("history");
        Assert.Equal("denied", history[0].GetProperty("status").GetString());
        Assert.Equal("pc-offline", history[0].GetProperty("reason").GetString());

        var body = await JsonOf(await HeartbeatAsync(pcClient, pc));
        Assert.Equal("denied", body.GetProperty("loginRequest").GetProperty("status").GetString());
        Assert.Equal("pc-offline", body.GetProperty("loginRequest").GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Approve_WhenThePcIsLocked_ClosesTheRequestAsPcLocked()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var pcClient = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        var requestId = await RequestAsync(pcClient, pc);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsJsonAsync($"/api/pcs/{pc.Id}/lock", new { })).StatusCode);

        var response = await ApproveAsync(admin, requestId, ThirtyMinutes);

        await AssertRefusedAsync(app, response, requestId, LockedMessage, "pc-locked");
        Assert.Empty(await SessionsOfAsync(app, pc.Id));
    }

    [Fact]
    public async Task Approve_WhenThePcIsInUse_ClosesTheRequestAsPcInUse_AndKeepsTheRunningSession()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var pcClient = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        var requestId = await RequestAsync(pcClient, pc);
        // Written directly: an admin unlock or a login through the API would already close the pending request.
        await app.WithDbAsync(async db =>
        {
            var stored = await db.Pcs.SingleAsync(p => p.Id == pc.Id);
            db.PcSessions.Add(SessionLifecycle.NewAdminUnlock(stored, app.Time.GetUtcNow()));
            return await db.SaveChangesAsync();
        });
        var runningId = Assert.Single(await OpenSessionsOfAsync(app, pc.Id)).Id;

        var response = await ApproveAsync(admin, requestId, ThirtyMinutes);

        await AssertRefusedAsync(app, response, requestId, InUseMessage, "pc-in-use");
        var open = Assert.Single(await OpenSessionsOfAsync(app, pc.Id));
        Assert.Equal(runningId, open.Id);
        Assert.Null(open.LoginRequestId);
    }

    [Fact]
    public async Task Approve_WhenTheSessionCannotBeOpened_Returns409AndLeavesTheRequestPending()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var pcClient = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        var requestId = await RequestAsync(pcClient, pc);
        // Every login session insert also writes a conflicting session for the same PC, so the unique index refuses it each time.
        await app.WithDbAsync(db => db.Database.ExecuteSqlRawAsync("""
            CREATE TRIGGER OtherLoginWinsTheRace BEFORE INSERT ON PcSessions
            WHEN NEW.LoginRequestId IS NOT NULL
            BEGIN
                INSERT INTO PcSessions (PcId, PcName, UserAccountId, Username, StartedAtUtc, IsAdminUnlock)
                VALUES (NEW.PcId, 'PC-01', 1, 'student', NEW.StartedAtUtc, 0);
            END
            """));

        var response = await ApproveAsync(admin, requestId, ThirtyMinutes);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(OpenFailedMessage, (await JsonOf(response)).GetProperty("error").GetString());
        Assert.Equal(LoginRequestStatus.Pending, (await StoredRequestAsync(app, requestId)).Status);
        Assert.Empty(await OpenSessionsOfAsync(app, pc.Id));
    }

    [Fact]
    public async Task Approve_MissingRequest_Returns404AndOpensNothing()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);

        var response = await ApproveAsync(admin, 999, ThirtyMinutes);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(await app.WithDbAsync(db => db.PcSessions.AsNoTracking().ToListAsync()));
    }
}
