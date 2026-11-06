using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using LabGuard.Server.Clients;
using LabGuard.Server.Data;
using LabGuard.Server.Network;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace LabGuard.Server.Tests;

public class ClientEndpointTests
{
    private const string NotRegisteredMessage = "This PC isn't registered.";

    private static async Task<JsonElement> JsonOf(HttpResponseMessage response)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.Clone();
    }

    private static Task<HttpResponseMessage> RequestRegistrationAsync(TestApp app, HttpClient client, string? name) =>
        client.PostAsync("/api/client/register-requests", TestApp.RegistrationRequestBody(name));

    private static Task<HttpResponseMessage> HeartbeatAsync(HttpClient client, string? authorization)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/client/heartbeat");
        if (authorization is not null)
        {
            request.Headers.TryAddWithoutValidation("Authorization", authorization);
        }

        return client.SendAsync(request);
    }

    private static Task<Pc> StoredAsync(TestApp app, string name) =>
        app.WithDbAsync(db => db.Pcs.AsNoTracking().SingleAsync(p => p.Name == name));

    private static async Task AssertErrorAsync(HttpResponseMessage response, HttpStatusCode status, string? message = null)
    {
        Assert.Equal(status, response.StatusCode);
        var error = (await JsonOf(response)).GetProperty("error").GetString();
        Assert.False(string.IsNullOrEmpty(error));
        if (message is not null)
        {
            Assert.Equal(message, error);
        }
    }

    // Ping

    [Fact]
    public async Task Ping_ReturnsServiceName()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();

        var response = await client.GetAsync("/api/client/ping");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await JsonOf(response);
        Assert.Equal("lykantra", body.GetProperty("service").GetString());
        Assert.Single(body.EnumerateObject());
    }

    // Accepted PCs

    [Fact]
    public async Task Accepted_TokensDiffer_AndEachWorksForHeartbeat()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();

        var first = await app.RegisterPcAsync(client, "PC-01");
        var second = await app.RegisterPcAsync(client, "PC-02");

        Assert.NotEqual(first, second);
        Assert.Equal(HttpStatusCode.OK, (await HeartbeatAsync(client, $"Bearer {first}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await HeartbeatAsync(client, $"Bearer {second}")).StatusCode);
    }

    [Fact]
    public async Task Accepted_PcStoresTheIpv4MappedAddressAsIpv4()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient("::ffff:10.0.0.7");

        await app.RegisterPcAsync(client, "PC-01");

        Assert.Equal("10.0.0.7", (await StoredAsync(app, "PC-01")).IpAddress);
    }

    [Fact]
    public async Task Request_IgnoresForwardedHeaders_ForTheAddressItRecords()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient("192.168.1.9");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/client/register-requests")
        {
            Content = TestApp.RegistrationRequestBody("PC-01"),
        };
        request.Headers.Add("X-Forwarded-For", "6.6.6.6");

        Assert.Equal(HttpStatusCode.Accepted, (await client.SendAsync(request)).StatusCode);

        var stored = await app.WithDbAsync(db => db.PcRegistrationRequests.AsNoTracking().SingleAsync());
        Assert.Equal("192.168.1.9", stored.IpAddress);
    }

    [Theory]
    [InlineData("PC-05")]
    [InlineData("pc-05")]
    [InlineData(" Pc-05 ")]
    public async Task Request_ForANameThatIsRegistered_Returns409AndKeepsTheOriginalPc(string secondName)
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();
        var originalToken = await app.RegisterPcAsync(client, "PC-05");
        var originalHash = (await StoredAsync(app, "PC-05")).TokenHash;

        var response = await RequestRegistrationAsync(app, client, secondName);

        await AssertErrorAsync(response, HttpStatusCode.Conflict, "That PC name is already registered.");
        Assert.DoesNotContain("token", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, await app.WithDbAsync(db => db.Pcs.CountAsync()));
        Assert.Equal(originalHash, (await StoredAsync(app, "PC-05")).TokenHash);
        Assert.Equal(HttpStatusCode.OK, (await HeartbeatAsync(client, $"Bearer {originalToken}")).StatusCode);
    }

    // Heartbeat

    [Fact]
    public async Task Heartbeat_WithValidToken_Returns200AndUpdatesLastSeenAndIp()
    {
        using var app = new TestApp();
        using var registering = app.CreatePcClient("192.168.1.50");
        var token = await app.RegisterPcAsync(registering, "PC-01");
        var registeredAt = app.Time.GetUtcNow();
        app.Time.Advance(TimeSpan.FromSeconds(10));
        using var moved = app.CreatePcClient("192.168.1.60");

        var response = await HeartbeatAsync(moved, $"Bearer {token}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var stored = await StoredAsync(app, "PC-01");
        Assert.Equal(app.Time.GetUtcNow().ToUnixTimeMilliseconds(), stored.LastSeenUtc.ToUnixTimeMilliseconds());
        Assert.Equal(registeredAt.ToUnixTimeMilliseconds(), stored.RegisteredAtUtc.ToUnixTimeMilliseconds());
        Assert.Equal("192.168.1.60", stored.IpAddress);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Bearer")]
    [InlineData("Bearer ")]
    [InlineData("Basic dXNlcjpwYXNz")]
    [InlineData("Bearer not-a-registered-token")]
    [InlineData("Bearer aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public async Task Heartbeat_WithMissingMalformedOrUnknownToken_Returns401(string? authorization)
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();
        await app.RegisterPcAsync(client, "PC-01");

        var response = await HeartbeatAsync(client, authorization);

        await AssertErrorAsync(response, HttpStatusCode.Unauthorized, NotRegisteredMessage);
    }

    [Fact]
    public async Task Heartbeat_WithTokenSentRawOrHash_Returns401()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();
        var token = await app.RegisterPcAsync(client, "PC-01");
        var hash = (await StoredAsync(app, "PC-01")).TokenHash;

        Assert.Equal(HttpStatusCode.Unauthorized, (await HeartbeatAsync(client, token)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await HeartbeatAsync(client, $"Bearer {hash}")).StatusCode);
    }

    [Fact]
    public async Task Heartbeat_WithRemovedPcToken_Returns401AndDoesNotRecreateIt()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();
        var token = await app.RegisterPcAsync(client, "PC-01");
        await app.WithDbAsync(async db =>
        {
            db.Pcs.RemoveRange(db.Pcs);
            await db.SaveChangesAsync();
            return 0;
        });

        var response = await HeartbeatAsync(client, $"Bearer {token}");

        await AssertErrorAsync(response, HttpStatusCode.Unauthorized, NotRegisteredMessage);
        Assert.Equal(0, await app.WithDbAsync(db => db.Pcs.CountAsync()));
    }

    [Fact]
    public async Task Heartbeat_WithoutBody_Works()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();
        var token = await app.RegisterPcAsync(client, "PC-01");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/client/heartbeat");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(request)).StatusCode);
    }

    // Ping and heartbeat rate limit

    [Fact]
    public async Task Heartbeat_AfterTheLimitInAMinute_Returns429WithErrorBody_ForThatAddressOnly()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient("192.168.1.50");
        using var otherClient = app.CreatePcClient("192.168.1.51");

        for (var i = 0; i < ClientEndpoints.MaxTrafficPerMinute; i++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await HeartbeatAsync(client, "Bearer x")).StatusCode);
        }

        await AssertErrorAsync(await HeartbeatAsync(client, "Bearer x"), HttpStatusCode.TooManyRequests);
        Assert.Equal(HttpStatusCode.Unauthorized, (await HeartbeatAsync(otherClient, "Bearer x")).StatusCode);
    }

    [Fact]
    public async Task Ping_AndHeartbeat_ShareOneBudget()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();

        for (var i = 0; i < ClientEndpoints.MaxTrafficPerMinute; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/client/ping")).StatusCode);
        }

        await AssertErrorAsync(await client.GetAsync("/api/client/ping"), HttpStatusCode.TooManyRequests);
        await AssertErrorAsync(await HeartbeatAsync(client, "Bearer x"), HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task RateLimitPolicies_AreIndependent()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();

        for (var i = 0; i < ClientEndpoints.MaxTrafficPerMinute; i++)
        {
            await client.GetAsync("/api/client/ping");
        }

        // Traffic budget is spent, registration still has its own.
        Assert.Equal(HttpStatusCode.Accepted, (await RequestRegistrationAsync(app, client, "PC-01")).StatusCode);

        using var second = app.CreatePcClient("192.168.1.99");
        for (var i = 0; i < ClientEndpoints.MaxRegistrationsPerMinute; i++)
        {
            await RequestRegistrationAsync(app, second, $"PC-{i + 10}");
        }

        await AssertErrorAsync(
            await RequestRegistrationAsync(app, second, "PC-99"),
            HttpStatusCode.TooManyRequests,
            "Too many registration attempts. Try again in a minute.");
        Assert.Equal(HttpStatusCode.OK, (await second.GetAsync("/api/client/ping")).StatusCode);
    }

    // Ports

    [Fact]
    public void ServerPorts_UseDefaultsWhenNotConfigured()
    {
        var ports = ServerPorts.FromConfiguration(new ConfigurationBuilder().Build());

        Assert.Equal(new ServerPorts(5000, 5001), ports);
    }

    [Fact]
    public void ServerPorts_WithEqualPorts_ThrowsAtStartup()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Server:Port"] = "5000", ["Server:ClientPort"] = "5000" })
            .Build();

        Assert.Throws<InvalidOperationException>(() => ServerPorts.FromConfiguration(configuration));
    }

    [Theory]
    [InlineData("Server:ClientPort", "0")]
    [InlineData("Server:ClientPort", "-1")]
    [InlineData("Server:ClientPort", "70000")]
    [InlineData("Server:ClientPort", "65536")]
    [InlineData("Server:ClientPort", "abc")]
    [InlineData("Server:ClientPort", "")]
    [InlineData("Server:ClientPort", "50.5")]
    [InlineData("Server:Port", "0")]
    [InlineData("Server:Port", "-1")]
    [InlineData("Server:Port", "70000")]
    [InlineData("Server:Port", "abc")]
    public void ServerPorts_WithAnInvalidPort_ThrowsAClearError(string key, string value)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [key] = value })
            .Build();

        var exception = Assert.Throws<InvalidOperationException>(() => ServerPorts.FromConfiguration(configuration));

        Assert.Contains(key, exception.Message);
        Assert.Contains("1 to 65535", exception.Message);
    }

    [Theory]
    [InlineData("1", "65535")]
    [InlineData("8080", "8081")]
    public void ServerPorts_AcceptsPortsAtTheEdgesOfTheRange(string admin, string client)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Server:Port"] = admin, ["Server:ClientPort"] = client })
            .Build();

        Assert.Equal(new ServerPorts(int.Parse(admin), int.Parse(client)), ServerPorts.FromConfiguration(configuration));
    }

    [Fact]
    public void ServerPorts_WhenOnlyAdminPortIsMovedOntoTheDefaultClientPort_Throws()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Server:Port"] = "5001" })
            .Build();

        Assert.Throws<InvalidOperationException>(() => ServerPorts.FromConfiguration(configuration));
    }
}
