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

public class PcLockEndpointTests
{
    private const string StudentPassword = "student pass 1";
    private const string InvalidRequestMessage = "Invalid request.";

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

    private static async Task AddAccountAsync(TestApp app, int? minutes = 60)
    {
        var hasher = app.Services.GetRequiredService<IPasswordHasher<UserAccount>>();
        await app.WithDbAsync(async db =>
        {
            var account = new UserAccount { Username = "student", IsActive = true, DefaultTimeMinutes = minutes };
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

    private static Task<HttpResponseMessage> LoginAsync(HttpClient client, TestPc pc) =>
        PostAsync(client, "/api/client/login", pc.Token, JsonContent.Create(new { username = "student", password = StudentPassword }));

    private static Task<HttpResponseMessage> HeartbeatAsync(HttpClient client, TestPc pc, int? sessionId = null) =>
        PostAsync(client, "/api/client/heartbeat", pc.Token, sessionId is null ? null : JsonContent.Create(new { sessionId }));

    private static async Task<int> LoggedInSessionIdAsync(HttpClient client, TestPc pc)
    {
        var response = await LoginAsync(client, pc);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await JsonOf(response)).GetProperty("sessionId").GetInt32();
    }

    private static Task<PcSession> StoredSessionAsync(TestApp app, int id) =>
        app.WithDbAsync(db => db.PcSessions.AsNoTracking().SingleAsync(s => s.Id == id));

    private static Task<HttpResponseMessage> LockAsync(HttpClient admin, int pcId) =>
        admin.PostAsJsonAsync($"/api/pcs/{pcId}/lock", new { });

    // Every admin route refuses anonymous callers, the PC port, and any request without a JSON body.

    [Theory]
    [InlineData("/api/pcs/1/lock")]
    public async Task AdminRoutes_WithoutCookie_Return401(string path)
    {
        using var app = new TestApp();
        using var browser = app.CreateBrowser();

        var response = await browser.PostAsJsonAsync(path, new { });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("/api/pcs/1/lock")]
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
    public async Task Lock_EndsTheActiveSession_AsAdminLock()
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
        Assert.Equal(SessionLifecycle.AdminLock, session.EndReason);
        Assert.Equal(app.Time.GetUtcNow().ToUnixTimeMilliseconds(), session.EndedAtUtc!.Value.ToUnixTimeMilliseconds());
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
    public async Task Lock_MissingPc_Returns404()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);

        var response = await LockAsync(admin, 999);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("PC not found.", (await JsonOf(response)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task Lock_OfflinePc_StillEndsItsSession()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        await AddAccountAsync(app);
        var sessionId = await LoggedInSessionIdAsync(client, pc);
        app.Time.Advance(TimeSpan.FromMinutes(1));

        Assert.Equal(HttpStatusCode.NoContent, (await LockAsync(admin, pc.Id)).StatusCode);

        Assert.Equal(SessionLifecycle.AdminLock, (await StoredSessionAsync(app, sessionId)).EndReason);
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
    public async Task Lock_OnlyEndsThatPcsSession()
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

        Assert.Null((await StoredSessionAsync(app, sessionB)).EndedAtUtc);
    }

    [Fact]
    public async Task Heartbeat_AfterAdminLock_ReportsAdminLockForTheSentId()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        await AddAccountAsync(app);
        var sessionId = await LoggedInSessionIdAsync(client, pc);
        await LockAsync(admin, pc.Id);

        var body = await JsonOf(await HeartbeatAsync(client, pc, sessionId));

        Assert.Equal(JsonValueKind.Null, body.GetProperty("session").ValueKind);
        Assert.Equal("admin-lock", body.GetProperty("endedReason").GetString());
    }
}
