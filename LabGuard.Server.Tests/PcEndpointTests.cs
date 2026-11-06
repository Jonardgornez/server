using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LabGuard.Server.Auth;
using LabGuard.Server.Data;
using LabGuard.Server.Sessions;
using Microsoft.EntityFrameworkCore;

namespace LabGuard.Server.Tests;

public class PcEndpointTests
{
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

    private static async Task<int> AddAsync(TestApp app, string name = "PC-01", TimeSpan? seenAgo = null, string? ip = "192.168.1.20")
    {
        var now = app.Time.GetUtcNow();
        var pc = new Pc
        {
            Name = name,
            TokenHash = Guid.NewGuid().ToString("N"),
            IpAddress = ip,
            RegisteredAtUtc = now - TimeSpan.FromHours(1),
            LastSeenUtc = now - (seenAgo ?? TimeSpan.Zero),
        };
        return await app.WithDbAsync(async db =>
        {
            db.Pcs.Add(pc);
            await db.SaveChangesAsync();
            return pc.Id;
        });
    }

    private static HttpRequestMessage Polling(string path)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add(AuthServiceExtensions.BackgroundRefreshHeader, "1");
        return request;
    }

    private static Task<HttpResponseMessage> HeartbeatAsync(HttpClient client, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/client/heartbeat");
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
        return client.SendAsync(request);
    }

    // Authentication

    [Theory]
    [InlineData("GET", "/api/pcs")]
    [InlineData("DELETE", "/api/pcs/1")]
    public async Task Endpoints_WithoutCookie_Return401(string method, string path)
    {
        using var app = new TestApp();
        using var client = app.CreateBrowser();

        var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), path));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task PcPort_CannotListOrDeletePcs()
    {
        using var app = new TestApp();
        var id = await AddAsync(app, seenAgo: TimeSpan.FromMinutes(5));
        using var pcClient = app.CreatePcClient();

        Assert.Equal(HttpStatusCode.NotFound, (await pcClient.GetAsync("/api/pcs")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await pcClient.DeleteAsync($"/api/pcs/{id}")).StatusCode);
        Assert.Equal(1, await app.WithDbAsync(db => db.Pcs.CountAsync()));
    }

    // List

    [Fact]
    public async Task List_StartsEmpty()
    {
        using var app = new TestApp();
        using var client = await SignedInAsync(app);

        var response = await client.GetAsync("/api/pcs");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, (await JsonOf(response)).GetArrayLength());
    }

    [Fact]
    public async Task List_HasExactShapeWithoutTokenOrHash()
    {
        using var app = new TestApp();
        using var client = await SignedInAsync(app);
        var id = await AddAsync(app, "PC-07", TimeSpan.FromSeconds(3));

        var response = await client.GetAsync("/api/pcs");

        var entry = (await JsonOf(response))[0];
        Assert.Equal(
            new[] { "id", "ipAddress", "isLocked", "isOnline", "lastSeenAt", "name", "registeredAt", "session" },
            entry.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(id, entry.GetProperty("id").GetInt32());
        Assert.Equal("PC-07", entry.GetProperty("name").GetString());
        Assert.True(entry.GetProperty("isOnline").GetBoolean());
        Assert.Equal("192.168.1.20", entry.GetProperty("ipAddress").GetString());
        Assert.Equal(app.Time.GetUtcNow().AddSeconds(-3).ToUnixTimeMilliseconds(), entry.GetProperty("lastSeenAt").GetDateTimeOffset().ToUnixTimeMilliseconds());
        Assert.Equal(app.Time.GetUtcNow().AddHours(-1).ToUnixTimeMilliseconds(), entry.GetProperty("registeredAt").GetDateTimeOffset().ToUnixTimeMilliseconds());
        var text = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("token", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("hash", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task List_ShowsIsLocked_ForAnOfflineLockedPcToo()
    {
        using var app = new TestApp();
        using var client = await SignedInAsync(app);
        await AddAsync(app, "PC-01", seenAgo: TimeSpan.FromMinutes(5));
        await AddAsync(app, "PC-02");
        var now = app.Time.GetUtcNow();
        await app.WithDbAsync(db => db.Pcs
            .Where(p => p.Name == "PC-01")
            .ExecuteUpdateAsync(setters => setters.SetProperty(p => p.LockedAtUtc, now)));

        var entries = await JsonOf(await client.GetAsync("/api/pcs"));

        Assert.True(entries[0].GetProperty("isLocked").GetBoolean());
        Assert.False(entries[0].GetProperty("isOnline").GetBoolean());
        Assert.False(entries[1].GetProperty("isLocked").GetBoolean());
    }

    [Fact]
    public async Task List_AdminUnlockSession_HasNullUsername()
    {
        using var app = new TestApp();
        using var client = await SignedInAsync(app);
        await AddAsync(app, "PC-01");
        await app.WithDbAsync(async db =>
        {
            var pc = await db.Pcs.SingleAsync();
            db.PcSessions.Add(SessionLifecycle.NewAdminUnlock(pc, app.Time.GetUtcNow()));
            await db.SaveChangesAsync();
            return 0;
        });

        var session = (await JsonOf(await client.GetAsync("/api/pcs")))[0].GetProperty("session");

        Assert.Equal(JsonValueKind.Null, session.GetProperty("username").ValueKind);
        Assert.Equal("open-time", session.GetProperty("time").GetProperty("mode").GetString());
        Assert.Equal(JsonValueKind.Null, session.GetProperty("secondsRemaining").ValueKind);
    }

    [Fact]
    public async Task List_IsOrderedByNameIgnoringCase()
    {
        using var app = new TestApp();
        using var client = await SignedInAsync(app);
        await AddAsync(app, "pc-10");
        await AddAsync(app, "PC-02");
        await AddAsync(app, "Lab-1");

        var names = (await JsonOf(await client.GetAsync("/api/pcs"))).EnumerateArray().Select(e => e.GetProperty("name").GetString()!).ToArray();

        Assert.Equal(["Lab-1", "PC-02", "pc-10"], names);
    }

    [Fact]
    public async Task List_NullIpAddress_IsReturnedAsNull()
    {
        using var app = new TestApp();
        using var client = await SignedInAsync(app);
        await AddAsync(app, ip: null);

        var entry = (await JsonOf(await client.GetAsync("/api/pcs")))[0];

        Assert.Equal(JsonValueKind.Null, entry.GetProperty("ipAddress").ValueKind);
    }

    [Fact]
    public async Task List_IsOnlineWhenSeenLessThan15SecondsAgo()
    {
        using var app = new TestApp();
        using var client = await SignedInAsync(app);
        await AddAsync(app);

        app.Time.Advance(TimeSpan.FromSeconds(14));
        var at14 = (await JsonOf(await client.GetAsync("/api/pcs")))[0].GetProperty("isOnline").GetBoolean();
        app.Time.Advance(TimeSpan.FromSeconds(1));
        var at15 = (await JsonOf(await client.GetAsync("/api/pcs")))[0].GetProperty("isOnline").GetBoolean();

        Assert.True(at14);
        Assert.False(at15);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3600)]
    public async Task List_LastSeenInTheFuture_IsOffline(int secondsAhead)
    {
        using var app = new TestApp();
        using var client = await SignedInAsync(app);
        await AddAsync(app, seenAgo: TimeSpan.FromSeconds(-secondsAhead));

        var entry = (await JsonOf(await client.GetAsync("/api/pcs")))[0];

        Assert.False(entry.GetProperty("isOnline").GetBoolean());
    }

    // Delete

    [Theory]
    [InlineData(1)]
    [InlineData(3600)]
    public async Task Delete_LastSeenInTheFuture_IsTreatedAsOfflineAndRemoved(int secondsAhead)
    {
        using var app = new TestApp();
        using var client = await SignedInAsync(app);
        var id = await AddAsync(app, seenAgo: TimeSpan.FromSeconds(-secondsAhead));

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/pcs/{id}")).StatusCode);
        Assert.Equal(0, await app.WithDbAsync(db => db.Pcs.CountAsync()));
    }

    [Fact]
    public async Task ListedOnlineState_AndDelete_AreExactComplements_AcrossTheBoundaries()
    {
        using var app = new TestApp();
        using var client = await SignedInAsync(app);
        var agos = new[] { -5, 0, 1, 14, 15, 16 };
        var ids = new Dictionary<int, int>();
        foreach (var ago in agos)
        {
            ids[ago] = await AddAsync(app, $"PC-{ago + 10:00}", TimeSpan.FromSeconds(ago));
        }

        var online = (await JsonOf(await client.GetAsync("/api/pcs"))).EnumerateArray()
            .ToDictionary(e => e.GetProperty("id").GetInt32(), e => e.GetProperty("isOnline").GetBoolean());

        foreach (var ago in agos)
        {
            var deleted = (await client.DeleteAsync($"/api/pcs/{ids[ago]}")).StatusCode == HttpStatusCode.NoContent;
            Assert.NotEqual(online[ids[ago]], deleted);
        }

        Assert.Equal([0, 1, 14], agos.Where(a => online[ids[a]]));
    }

    [Fact]
    public async Task Delete_MissingPc_Returns404()
    {
        using var app = new TestApp();
        using var client = await SignedInAsync(app);

        var response = await client.DeleteAsync("/api/pcs/999");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("PC not found.", (await JsonOf(response)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task Delete_OnlinePc_Returns409AndKeepsIt()
    {
        using var app = new TestApp();
        using var client = await SignedInAsync(app);
        var id = await AddAsync(app, seenAgo: TimeSpan.FromSeconds(14));

        var response = await client.DeleteAsync($"/api/pcs/{id}");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("This PC is online. It can only be removed while offline.", (await JsonOf(response)).GetProperty("error").GetString());
        Assert.Equal(1, await app.WithDbAsync(db => db.Pcs.CountAsync()));
    }

    [Fact]
    public async Task Delete_OfflinePc_Returns204AndRemovesIt()
    {
        using var app = new TestApp();
        using var client = await SignedInAsync(app);
        var id = await AddAsync(app, seenAgo: TimeSpan.FromSeconds(15));
        var other = await AddAsync(app, "PC-02", TimeSpan.FromMinutes(5));

        var response = await client.DeleteAsync($"/api/pcs/{id}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal([other], await app.WithDbAsync(db => db.Pcs.Select(p => p.Id).ToListAsync()));
    }

    [Fact]
    public async Task Delete_NonNumericId_Returns404()
    {
        using var app = new TestApp();
        using var client = await SignedInAsync(app);

        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync("/api/pcs/abc")).StatusCode);
    }

    [Fact]
    public async Task Delete_RemovedPcToken_StopsWorkingAtOnce()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var pc = app.CreatePcClient();
        var token = await app.RegisterPcAsync(pc, "PC-01");
        var id = await app.WithDbAsync(db => db.Pcs.Where(p => p.Name == "PC-01").Select(p => p.Id).SingleAsync());
        app.Time.Advance(TimeSpan.FromSeconds(20));

        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/pcs/{id}")).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await HeartbeatAsync(pc, token)).StatusCode);
    }

    [Fact]
    public async Task Delete_AfterPcComesBackOnline_Returns409()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var pc = app.CreatePcClient();
        var token = await app.RegisterPcAsync(pc, "PC-01");
        var id = await app.WithDbAsync(db => db.Pcs.Where(p => p.Name == "PC-01").Select(p => p.Id).SingleAsync());
        app.Time.Advance(TimeSpan.FromSeconds(60));
        await HeartbeatAsync(pc, token);

        var response = await admin.DeleteAsync($"/api/pcs/{id}");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    // The setup code is gone: its routes are served like any unknown path, even to a signed-in administrator.

    [Theory]
    [InlineData("GET", "/api/pcs/setup-code")]
    [InlineData("POST", "/api/pcs/setup-code/regenerate")]
    public async Task SetupCodeRoutes_NoLongerExist(string method, string path)
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var request = new HttpRequestMessage(new HttpMethod(method), path) { Content = JsonContent.Create(new { }) };

        var response = await admin.SendAsync(request);

        Assert.False(response.IsSuccessStatusCode);
        Assert.DoesNotContain("\"code\"", await response.Content.ReadAsStringAsync());
    }

    // Background refresh and the idle timeout

    [Fact]
    public async Task BackgroundPolling_DoesNotKeepIdleSessionAlive()
    {
        using var app = new TestApp();
        using var client = await SignedInAsync(app);

        for (var minute = 1; minute <= 29; minute++)
        {
            app.Time.Advance(TimeSpan.FromMinutes(1));
            using var poll = Polling("/api/pcs");
            Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(poll)).StatusCode);
        }

        app.Time.Advance(TimeSpan.FromMinutes(2));
        using var late = Polling("/api/pcs");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(late)).StatusCode);
    }

    [Fact]
    public async Task OrdinaryRequests_KeepSessionAliveBeyond30Minutes()
    {
        using var app = new TestApp();
        using var client = await SignedInAsync(app);

        for (var minute = 1; minute <= 45; minute++)
        {
            app.Time.Advance(TimeSpan.FromMinutes(1));
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/pcs")).StatusCode);
        }
    }

    [Fact]
    public async Task BackgroundPolling_AfterARealRequest_ExpiresFrom30MinutesAfterThatRequest()
    {
        using var app = new TestApp();
        using var client = await SignedInAsync(app);

        app.Time.Advance(TimeSpan.FromMinutes(25));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/auth/me")).StatusCode);
        for (var minute = 1; minute <= 29; minute++)
        {
            app.Time.Advance(TimeSpan.FromMinutes(1));
            using var poll = Polling("/api/pcs");
            Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(poll)).StatusCode);
        }

        app.Time.Advance(TimeSpan.FromMinutes(2));
        using var late = Polling("/api/pcs");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(late)).StatusCode);
    }
}
