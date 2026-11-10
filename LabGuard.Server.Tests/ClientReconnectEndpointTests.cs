using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LabGuard.Server.Clients;
using LabGuard.Server.Data;
using LabGuard.Server.Pcs;
using LabGuard.Server.Requests;
using LabGuard.Server.Sessions;
using Microsoft.EntityFrameworkCore;

namespace LabGuard.Server.Tests;

public class ClientReconnectEndpointTests
{
    private const string ReconnectPath = "/api/client/reconnect";
    private const string PcAddress = "192.168.1.50";
    private const string OnlineMessage = "A PC with this address is online right now.";
    private const string NotRegisteredYetMessage = "This PC isn't registered yet.";
    private const string RateLimitMessage = "Too many registration attempts. Try again in a minute.";

    // Long enough that the PC counts as offline on the fake clock.
    private static readonly TimeSpan OfflineFor = PcEndpoints.OnlineWindow + TimeSpan.FromSeconds(1);

    private static async Task<JsonElement> JsonOf(HttpResponseMessage response)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.Clone();
    }

    private static Task<HttpResponseMessage> ReconnectAsync(HttpClient client, HttpContent? content = null) =>
        client.SendAsync(new HttpRequestMessage(HttpMethod.Post, ReconnectPath) { Content = content });

    private static Task<HttpResponseMessage> HeartbeatAsync(HttpClient client, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/client/heartbeat");
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
        return client.SendAsync(request);
    }

    private static async Task AssertErrorAsync(HttpResponseMessage response, HttpStatusCode status, string message)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal(message, (await JsonOf(response)).GetProperty("error").GetString());
    }

    private static Task<Pc> StoredAsync(TestApp app, string name) =>
        app.WithDbAsync(db => db.Pcs.AsNoTracking().SingleAsync(p => p.Name == name));

    private static Task<PcRegistrationRequest> RegistrationOfAsync(TestApp app, string name) =>
        app.WithDbAsync(db => db.PcRegistrationRequests.AsNoTracking().SingleAsync(r => r.Name == name));

    private static Task<int> ChangePcAsync(TestApp app, string name, Action<Pc> change) =>
        app.WithDbAsync(async db =>
        {
            var pc = await db.Pcs.SingleAsync(p => p.Name == name);
            change(pc);
            return await db.SaveChangesAsync();
        });

    private static async Task<int> AddPcAsync(TestApp app, string name, string ip, TimeSpan seenAgo)
    {
        var now = app.Time.GetUtcNow();
        return await app.WithDbAsync(async db =>
        {
            var pc = new Pc
            {
                Name = name,
                TokenHash = Guid.NewGuid().ToString("N"),
                IpAddress = ip,
                RegisteredAtUtc = now - TimeSpan.FromHours(2),
                LastSeenUtc = now - seenAgo,
            };
            db.Pcs.Add(pc);
            await db.SaveChangesAsync();
            return pc.Id;
        });
    }

    // Registers a PC from the client's address, then lets the fake clock pass the online window so the PC is offline.
    private static async Task<string> RegisterOfflinePcAsync(TestApp app, HttpClient pc, string name = "PC-01")
    {
        var token = await app.RegisterPcAsync(pc, name);
        app.Time.Advance(OfflineFor);
        return token;
    }

    private static async Task<string> ReconnectTokenAsync(HttpClient pc)
    {
        var response = await ReconnectAsync(pc);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await JsonOf(response)).GetProperty("token").GetString()!;
    }

    // Match and answer

    [Fact]
    public async Task Reconnect_OfflinePcAtTheSameAddress_Returns200WithANewTokenAndTheExistingName()
    {
        using var app = new TestApp();
        using var pc = app.CreatePcClient(PcAddress);
        await RegisterOfflinePcAsync(app, pc);

        var response = await ReconnectAsync(pc);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await JsonOf(response);
        Assert.Equal(new[] { "pcName", "token" }, body.EnumerateObject().Select(p => p.Name).Order().ToArray());
        Assert.Equal("PC-01", body.GetProperty("pcName").GetString());
        var token = body.GetProperty("token").GetString()!;
        Assert.Equal(43, token.Length);
        Assert.Matches("^[A-Za-z0-9_-]+$", token);
    }

    [Fact]
    public async Task Reconnect_TheNewTokenWorksForHeartbeat_AndTheOldTokenIsRefused()
    {
        using var app = new TestApp();
        using var pc = app.CreatePcClient(PcAddress);
        var oldToken = await RegisterOfflinePcAsync(app, pc);

        var newToken = await ReconnectTokenAsync(pc);

        Assert.NotEqual(oldToken, newToken);
        Assert.Equal(HttpStatusCode.OK, (await HeartbeatAsync(pc, newToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await HeartbeatAsync(pc, oldToken)).StatusCode);
    }

    [Fact]
    public async Task Reconnect_StoresOnlyTheHashOfTheNewToken_AndMarksThePcOnline()
    {
        using var app = new TestApp();
        using var pc = app.CreatePcClient(PcAddress);
        await RegisterOfflinePcAsync(app, pc);
        var before = await StoredAsync(app, "PC-01");

        var token = await ReconnectTokenAsync(pc);

        var after = await StoredAsync(app, "PC-01");
        Assert.NotEqual(before.TokenHash, after.TokenHash);
        Assert.NotEqual(token, after.TokenHash);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token))), after.TokenHash);
        Assert.Equal(app.Time.GetUtcNow().ToUnixTimeMilliseconds(), after.LastSeenUtc.ToUnixTimeMilliseconds());
    }

    [Fact]
    public async Task Reconnect_KeepsTheIdNameRegistrationAddressLockAndInternetBlock()
    {
        using var app = new TestApp();
        using var pc = app.CreatePcClient(PcAddress);
        await RegisterOfflinePcAsync(app, pc);
        var lockedAt = app.Time.GetUtcNow();
        await ChangePcAsync(app, "PC-01", stored =>
        {
            stored.LockedAtUtc = lockedAt;
            stored.InternetBlocked = true;
        });
        var before = await StoredAsync(app, "PC-01");

        await ReconnectAsync(pc);

        var after = await StoredAsync(app, "PC-01");
        Assert.Equal(before.Id, after.Id);
        Assert.Equal(before.Name, after.Name);
        Assert.Equal(before.RegisteredAtUtc, after.RegisteredAtUtc);
        Assert.Equal(before.IpAddress, after.IpAddress);
        Assert.Equal(lockedAt.ToUnixTimeMilliseconds(), after.LockedAtUtc!.Value.ToUnixTimeMilliseconds());
        Assert.True(after.InternetBlocked);
    }

    [Fact]
    public async Task Reconnect_KeepsAnActiveLoginLockout()
    {
        using var app = new TestApp();
        using var pc = app.CreatePcClient(PcAddress);
        await RegisterOfflinePcAsync(app, pc);
        var now = app.Time.GetUtcNow();
        var lockedUntil = now + ClientEndpoints.LoginLockDuration;
        await ChangePcAsync(app, "PC-01", stored =>
        {
            stored.FailedLoginCount = ClientEndpoints.MaxFailedLogins;
            stored.LastFailedLoginUtc = now;
            stored.LoginLockedUntilUtc = lockedUntil;
        });

        Assert.Equal(HttpStatusCode.OK, (await ReconnectAsync(pc)).StatusCode);

        var after = await StoredAsync(app, "PC-01");
        Assert.Equal(ClientEndpoints.MaxFailedLogins, after.FailedLoginCount);
        Assert.Equal(now.ToUnixTimeMilliseconds(), after.LastFailedLoginUtc!.Value.ToUnixTimeMilliseconds());
        Assert.Equal(lockedUntil.ToUnixTimeMilliseconds(), after.LoginLockedUntilUtc!.Value.ToUnixTimeMilliseconds());
    }

    [Fact]
    public async Task Reconnect_Logs_ThePcAndAddress_ButNeverTheToken()
    {
        using var app = new TestApp();
        using var pc = app.CreatePcClient(PcAddress);
        await RegisterOfflinePcAsync(app, pc);
        var pcId = (await StoredAsync(app, "PC-01")).Id;

        var token = await ReconnectTokenAsync(pc);

        Assert.Contains(app.Logs.Entries, entry => entry.Contains($"PC {pcId} (PC-01) reconnected from {PcAddress}"));
        Assert.DoesNotContain(app.Logs.Entries, entry => entry.Contains(token));
    }

    [Fact]
    public async Task Reconnect_Success_IsNotCached()
    {
        using var app = new TestApp();
        using var pc = app.CreatePcClient(PcAddress);
        await RegisterOfflinePcAsync(app, pc);

        var response = await ReconnectAsync(pc);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
    }

    [Fact]
    public async Task Reconnect_FromLoopbackAgainstALanPc_Returns404()
    {
        using var app = new TestApp();
        using var pc = app.CreatePcClient(PcAddress);
        await RegisterOfflinePcAsync(app, pc);
        using var loopback = app.CreatePcClient(IPAddress.Loopback.ToString());

        await AssertErrorAsync(await ReconnectAsync(loopback), HttpStatusCode.NotFound, NotRegisteredYetMessage);
    }

    // Sessions, login requests and registration requests

    [Fact]
    public async Task Reconnect_EndsTheOpenSession_AsConnectionLost()
    {
        using var app = new TestApp();
        using var pc = app.CreatePcClient(PcAddress);
        await RegisterOfflinePcAsync(app, pc);
        var sessionId = await app.WithDbAsync(async db =>
        {
            var stored = await db.Pcs.SingleAsync(p => p.Name == "PC-01");
            db.PcSessions.Add(SessionLifecycle.NewAdminUnlock(stored, app.Time.GetUtcNow()));
            await db.SaveChangesAsync();
            return await db.PcSessions.Select(s => s.Id).SingleAsync();
        });

        await ReconnectAsync(pc);

        var session = await app.WithDbAsync(db => db.PcSessions.AsNoTracking().SingleAsync(s => s.Id == sessionId));
        Assert.NotNull(session.EndedAtUtc);
        Assert.Equal(SessionLifecycle.ConnectionLost, session.EndReason);
    }

    [Fact]
    public async Task Reconnect_ASessionThatAlreadyRanOut_IsRecordedAsTimeExpiredAtItsExpiry()
    {
        using var app = new TestApp();
        using var pc = app.CreatePcClient(PcAddress);
        await app.RegisterPcAsync(pc, "PC-01");
        var expiresAt = app.Time.GetUtcNow() + TimeSpan.FromSeconds(10);
        var sessionId = await app.WithDbAsync(async db =>
        {
            var stored = await db.Pcs.SingleAsync(p => p.Name == "PC-01");
            db.PcSessions.Add(new PcSession
            {
                PcId = stored.Id,
                PcName = stored.Name,
                UserAccountId = 1,
                Username = "student",
                StartedAtUtc = app.Time.GetUtcNow(),
                ExpiresAtUtc = expiresAt,
                DefaultTimeMinutes = 1,
            });
            await db.SaveChangesAsync();
            return await db.PcSessions.Select(s => s.Id).SingleAsync();
        });
        app.Time.Advance(OfflineFor);

        await ReconnectAsync(pc);

        var session = await app.WithDbAsync(db => db.PcSessions.AsNoTracking().SingleAsync(s => s.Id == sessionId));
        Assert.Equal(SessionLifecycle.TimeExpired, session.EndReason);
        Assert.Equal(expiresAt.ToUnixTimeMilliseconds(), session.EndedAtUtc!.Value.ToUnixTimeMilliseconds());
    }

    [Fact]
    public async Task Reconnect_ClosesThePendingLoginRequest_AsPcOffline()
    {
        using var app = new TestApp();
        using var pc = app.CreatePcClient(PcAddress);
        await RegisterOfflinePcAsync(app, pc);
        var stored = await StoredAsync(app, "PC-01");
        await app.WithDbAsync(async db =>
        {
            db.LoginRequests.Add(new LoginRequest
            {
                PcName = stored.Name,
                PcId = stored.Id,
                RequestedAtUtc = app.Time.GetUtcNow(),
                Status = LoginRequestStatus.Pending,
            });
            return await db.SaveChangesAsync();
        });

        await ReconnectAsync(pc);

        var request = await app.WithDbAsync(db => db.LoginRequests.AsNoTracking().SingleAsync(r => r.PcId == stored.Id));
        Assert.Equal(LoginRequestStatus.Cancelled, request.Status);
        Assert.Equal(LoginRequestLifecycle.PcOffline, request.Reason);
    }

    [Fact]
    public async Task Reconnect_CancelsAPendingRegistrationFromThatAddress_AndLeavesOtherAddressesAlone()
    {
        using var app = new TestApp();
        using var pc = app.CreatePcClient(PcAddress);
        using var other = app.CreatePcClient("192.168.1.51");
        await RegisterOfflinePcAsync(app, pc);
        Assert.Equal(HttpStatusCode.Accepted, (await pc.PostAsync("/api/client/register-requests", TestApp.RegistrationRequestBody("PC-02"))).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await other.PostAsync("/api/client/register-requests", TestApp.RegistrationRequestBody("PC-03"))).StatusCode);

        await ReconnectAsync(pc);

        Assert.Equal(RegistrationRequestStatus.Cancelled, (await RegistrationOfAsync(app, "PC-02")).Status);
        Assert.Equal(RegistrationRequestStatus.Pending, (await RegistrationOfAsync(app, "PC-03")).Status);
    }

    // Refusals

    [Fact]
    public async Task Reconnect_WhileTheMatchingPcIsOnline_Returns409AndChangesNothing()
    {
        using var app = new TestApp();
        using var pc = app.CreatePcClient(PcAddress);
        var token = await app.RegisterPcAsync(pc, "PC-01");
        var before = await StoredAsync(app, "PC-01");

        await AssertErrorAsync(await ReconnectAsync(pc), HttpStatusCode.Conflict, OnlineMessage);

        var after = await StoredAsync(app, "PC-01");
        Assert.Equal(before.TokenHash, after.TokenHash);
        Assert.Equal(HttpStatusCode.OK, (await HeartbeatAsync(pc, token)).StatusCode);
    }

    [Fact]
    public async Task Reconnect_WithNoPcAtThisAddress_Returns404()
    {
        using var app = new TestApp();
        using var pc = app.CreatePcClient(PcAddress);
        using var elsewhere = app.CreatePcClient("192.168.1.60");
        await RegisterOfflinePcAsync(app, pc);

        await AssertErrorAsync(await ReconnectAsync(elsewhere), HttpStatusCode.NotFound, NotRegisteredYetMessage);
    }

    [Fact]
    public async Task Reconnect_WithNoAddressOnTheConnection_Returns404()
    {
        using var app = new TestApp();
        using var pc = app.CreatePcClient(SimulatedConnectionFilter.NoAddress);

        await AssertErrorAsync(await ReconnectAsync(pc), HttpStatusCode.NotFound, NotRegisteredYetMessage);
    }

    [Fact]
    public async Task Reconnect_AfterTheAdminRemovesThePc_Returns404_AndRegistrationStillWorks()
    {
        using var app = new TestApp();
        using var pc = app.CreatePcClient(PcAddress);
        await RegisterOfflinePcAsync(app, pc);
        using var admin = await app.SignedInAdminAsync();
        var id = (await StoredAsync(app, "PC-01")).Id;
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/pcs/{id}")).StatusCode);

        await AssertErrorAsync(await ReconnectAsync(pc), HttpStatusCode.NotFound, NotRegisteredYetMessage);
        Assert.Equal(HttpStatusCode.Accepted, (await pc.PostAsync("/api/client/register-requests", TestApp.RegistrationRequestBody("PC-01"))).StatusCode);
    }

    [Fact]
    public async Task Reconnect_ChoosesThePcSeenMostRecently_WhenSeveralShareTheAddress()
    {
        using var app = new TestApp();
        using var pc = app.CreatePcClient(PcAddress);
        await AddPcAsync(app, "PC-NEW", PcAddress, TimeSpan.FromMinutes(1));
        await AddPcAsync(app, "PC-OLD", PcAddress, TimeSpan.FromHours(1));
        var oldBefore = await StoredAsync(app, "PC-OLD");

        var response = await ReconnectAsync(pc);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("PC-NEW", (await JsonOf(response)).GetProperty("pcName").GetString());
        Assert.Equal(oldBefore.TokenHash, (await StoredAsync(app, "PC-OLD")).TokenHash);
    }

    [Fact]
    public async Task Reconnect_FromAnIpv4MappedIPv6Address_MatchesThePcWithThePlainIPv4Address()
    {
        using var app = new TestApp();
        using var pc = app.CreatePcClient(PcAddress);
        await RegisterOfflinePcAsync(app, pc);
        using var mapped = app.CreatePcClient($"::ffff:{PcAddress}");

        var response = await ReconnectAsync(mapped);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("PC-01", (await JsonOf(response)).GetProperty("pcName").GetString());
    }

    [Fact]
    public async Task Reconnect_AcceptsAnEmptyBodyOrAnEmptyObject()
    {
        using var app = new TestApp();
        using var pc = app.CreatePcClient(PcAddress);
        await RegisterOfflinePcAsync(app, pc);

        Assert.Equal(HttpStatusCode.OK, (await ReconnectAsync(pc, JsonContent.Create(new { }))).StatusCode);
        app.Time.Advance(OfflineFor);
        Assert.Equal(HttpStatusCode.OK, (await ReconnectAsync(pc)).StatusCode);
    }

    // Rate limit and concurrency

    [Fact]
    public async Task Reconnect_AfterTheLimitInAMinute_Returns429_ForThatAddressOnly()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient(PcAddress);
        using var otherClient = app.CreatePcClient("192.168.1.51");

        for (var i = 0; i < ClientEndpoints.MaxRegistrationsPerMinute; i++)
        {
            Assert.Equal(HttpStatusCode.NotFound, (await ReconnectAsync(client)).StatusCode);
        }

        await AssertErrorAsync(await ReconnectAsync(client), HttpStatusCode.TooManyRequests, RateLimitMessage);
        Assert.Equal(HttpStatusCode.NotFound, (await ReconnectAsync(otherClient)).StatusCode);
    }

    [Fact]
    public async Task Reconnect_ConcurrentRequestsForOneOfflinePc_OnlyOneTokenWorks()
    {
        using var app = new TestApp();
        using var pc = app.CreatePcClient(PcAddress);
        var oldToken = await RegisterOfflinePcAsync(app, pc);

        var responses = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => ReconnectAsync(pc)));

        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.Equal(4, responses.Count(r => r.StatusCode == HttpStatusCode.Conflict));
        var winner = responses.Single(r => r.StatusCode == HttpStatusCode.OK);
        var newToken = (await JsonOf(winner)).GetProperty("token").GetString()!;
        Assert.Equal(HttpStatusCode.OK, (await HeartbeatAsync(pc, newToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await HeartbeatAsync(pc, oldToken)).StatusCode);
    }
}
