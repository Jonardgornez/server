using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LabGuard.Server.Clients;
using LabGuard.Server.Data;
using LabGuard.Server.Network;
using Microsoft.EntityFrameworkCore;

namespace LabGuard.Server.Tests;

public class ClientRegistrationEndpointTests
{
    private const string NotRegisteredMessage = "This PC isn't registered.";

    private static async Task<JsonElement> JsonOf(HttpResponseMessage response)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.Clone();
    }

    private static Task<HttpResponseMessage> CreateAsync(HttpClient pc, string? name, string? nonce = null) =>
        pc.PostAsync("/api/client/register-requests", TestApp.RegistrationRequestBody(name, nonce));

    private static async Task<string> CreateTokenAsync(HttpClient pc, string name)
    {
        var response = await CreateAsync(pc, name);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return (await JsonOf(response)).GetProperty("token").GetString()!;
    }

    private static Task<HttpResponseMessage> PostWithAuthorizationAsync(HttpClient client, string path, string? authorization)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path);
        if (authorization is not null)
        {
            request.Headers.TryAddWithoutValidation("Authorization", authorization);
        }

        return client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> StatusAsync(HttpClient client, string? authorization) =>
        PostWithAuthorizationAsync(client, "/api/client/register-requests/status", authorization);

    private static Task<HttpResponseMessage> CancelAsync(HttpClient client, string? authorization) =>
        PostWithAuthorizationAsync(client, "/api/client/register-requests/cancel", authorization);

    private static Task<HttpResponseMessage> HeartbeatAsync(HttpClient client, string authorization) =>
        PostWithAuthorizationAsync(client, "/api/client/heartbeat", authorization);

    private static async Task<string> StatusOfAsync(HttpClient client, string token)
    {
        var response = await StatusAsync(client, $"Bearer {token}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await JsonOf(response)).GetProperty("status").GetString()!;
    }

    private static Task<PcRegistrationRequest> StoredByNameAsync(TestApp app, string name) =>
        app.WithDbAsync(db => db.PcRegistrationRequests.AsNoTracking().SingleAsync(r => r.Name == name));

    private static string HashOf(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    // Create

    [Fact]
    public async Task Create_Succeeds_Returns202WithOnlyTheToken_AndStoresOnlyItsHash()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient("192.168.1.77");

        var response = await CreateAsync(client, "  PC-05  ");

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var body = await JsonOf(response);
        Assert.Single(body.EnumerateObject());
        var token = body.GetProperty("token").GetString()!;
        Assert.Equal(43, token.Length);
        Assert.Matches("^[A-Za-z0-9_-]+$", token);

        var stored = await StoredByNameAsync(app, "PC-05");
        Assert.Equal(HashOf(token), stored.TokenHash);
        Assert.NotEqual(token, stored.TokenHash);
        Assert.Equal("192.168.1.77", stored.IpAddress);
        Assert.Equal(RegistrationRequestStatus.Pending, stored.Status);
        Assert.Equal(app.Time.GetUtcNow().ToUnixTimeMilliseconds(), stored.RequestedAtUtc.ToUnixTimeMilliseconds());
        Assert.Equal(0, await app.WithDbAsync(db => db.Pcs.CountAsync()));
    }

    [Fact]
    public async Task Create_StoresTheNonceAndThePairingNumberForIt()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();
        var nonce = TestApp.NewNonce();

        Assert.Equal(HttpStatusCode.Accepted, (await CreateAsync(client, "PC-01", nonce)).StatusCode);

        var stored = await StoredByNameAsync(app, "PC-01");
        Assert.Equal(nonce, stored.Nonce);
        Assert.Equal(PairingNumber.Compute(app.Channel.Certificate, Convert.FromHexString(nonce)), stored.PairingNumber);
        Assert.Matches("^[0-9]{6}$", stored.PairingNumber);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("PC 05")]
    [InlineData("PC/05")]
    [InlineData("PC;05")]
    [InlineData("pcé")]
    [InlineData("PC-05\u0000")]
    public async Task Create_WithInvalidName_Returns400AndStoresNothing(string name)
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();

        var response = await CreateAsync(client, name);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("letters, numbers", (await JsonOf(response)).GetProperty("error").GetString());
        Assert.Equal(0, await app.WithDbAsync(db => db.PcRegistrationRequests.CountAsync()));
    }

    [Fact]
    public async Task Create_WithMissingOrTooLongName_Returns400_AndMaxLengthIsAccepted()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();

        Assert.Equal(HttpStatusCode.BadRequest, (await CreateAsync(client, null)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await CreateAsync(client, new string('a', 33))).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await CreateAsync(client, new string('a', 32))).StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("ABCDEF")]
    [InlineData("0123456789abcdef")]
    [InlineData("ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef0")]
    [InlineData("g123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
    [InlineData(" 0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcde")]
    public async Task Create_WithInvalidNonce_Returns400AndStoresNothing(string nonce)
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();

        var response = await CreateAsync(client, "PC-01", nonce);

        await AssertErrorAsync(response, HttpStatusCode.BadRequest, "Nonce must be 64 lowercase hex characters.");
        Assert.Equal(0, await app.WithDbAsync(db => db.PcRegistrationRequests.CountAsync()));
    }

    [Fact]
    public async Task Create_WithoutANonce_Returns400()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();

        var response = await client.PostAsJsonAsync("/api/client/register-requests", new { name = "PC-01" });

        await AssertErrorAsync(response, HttpStatusCode.BadRequest, "Nonce must be 64 lowercase hex characters.");
    }

    [Theory]
    [InlineData("{not json", "application/json")]
    [InlineData("null", "application/json")]
    [InlineData("""{"name":"PC-01"}""", "text/plain")]
    public async Task Create_WithMalformedBodyOrContentType_Returns400(string body, string contentType)
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();

        var response = await client.PostAsync("/api/client/register-requests", new StringContent(body, Encoding.UTF8, contentType));

        await AssertErrorAsync(response, HttpStatusCode.BadRequest, "Invalid request.");
        Assert.Equal(0, await app.WithDbAsync(db => db.PcRegistrationRequests.CountAsync()));
    }

    [Fact]
    public async Task Create_WithAPcNameThatIsAlreadyRegistered_Returns409AndStoresNothing()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();
        await app.RegisterPcAsync(client, "PC-05");
        var requestsBefore = await app.WithDbAsync(db => db.PcRegistrationRequests.CountAsync());

        var response = await CreateAsync(client, "pc-05");

        await AssertErrorAsync(response, HttpStatusCode.Conflict, "That PC name is already registered.");
        Assert.DoesNotContain("token", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(requestsBefore, await app.WithDbAsync(db => db.PcRegistrationRequests.CountAsync()));
    }

    [Fact]
    public async Task Create_EleventhAttemptInAMinute_Returns429_ForThatAddressOnly()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient("192.168.1.50");
        using var otherClient = app.CreatePcClient("192.168.1.51");

        for (var i = 0; i < ClientEndpoints.MaxRegistrationsPerMinute; i++)
        {
            Assert.Equal(HttpStatusCode.Accepted, (await CreateAsync(client, $"PC-{i:00}")).StatusCode);
        }

        var limited = await CreateAsync(client, "PC-99");

        await AssertErrorAsync(limited, HttpStatusCode.TooManyRequests, "Too many registration attempts. Try again in a minute.");
        Assert.Equal(HttpStatusCode.Accepted, (await CreateAsync(otherClient, "PC-98")).StatusCode);
    }

    [Fact]
    public async Task Create_WhenOneHundredRequestsArePending_Returns429_UntilOneExpires()
    {
        using var app = new TestApp();
        var pcs = Enumerable.Range(0, ClientEndpoints.MaxPendingRegistrationRequests)
            .Select(i => app.CreatePcClient($"10.20.{i / 200}.{i % 200 + 1}"))
            .ToList();
        using var extra = app.CreatePcClient("10.30.0.1");
        try
        {
            for (var i = 0; i < pcs.Count; i++)
            {
                Assert.Equal(HttpStatusCode.Accepted, (await CreateAsync(pcs[i], $"PC-{i:000}")).StatusCode);
            }

            var full = await CreateAsync(extra, "PC-EXTRA");

            await AssertErrorAsync(full, HttpStatusCode.TooManyRequests, "Too many PCs are waiting for approval. Try again later.");
            Assert.Equal(0, await app.WithDbAsync(db => db.PcRegistrationRequests.CountAsync(r => r.Name == "PC-EXTRA")));

            app.Time.Advance(TimeSpan.FromMinutes(11));
            Assert.Equal(HttpStatusCode.Accepted, (await CreateAsync(extra, "PC-EXTRA")).StatusCode);
        }
        finally
        {
            foreach (var pc in pcs)
            {
                pc.Dispose();
            }
        }
    }

    [Fact]
    public async Task Create_FromTheSameAddress_CancelsItsEarlierPendingRequest()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient("192.168.1.80");
        var first = await CreateTokenAsync(client, "PC-01");

        var second = await CreateTokenAsync(client, "PC-01");

        Assert.Equal("expired", await StatusOfAsync(client, first));
        Assert.Equal("pending", await StatusOfAsync(client, second));
    }

    [Fact]
    public async Task Create_FromAnotherAddress_DoesNotCancelTheFirstRequest()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient("192.168.1.80");
        using var other = app.CreatePcClient("192.168.1.81");
        var first = await CreateTokenAsync(client, "PC-01");

        await CreateTokenAsync(other, "PC-02");

        Assert.Equal("pending", await StatusOfAsync(client, first));
    }

    [Fact]
    public async Task Create_DeletesFinishedAndExpiredRowsOlderThanAnHour_AndKeepsRecentOnes()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();
        await app.WithDbAsync(async db =>
        {
            db.PcRegistrationRequests.AddRange(
                Stored("OLD-DENIED", RegistrationRequestStatus.Denied, app.Time.GetUtcNow() - TimeSpan.FromMinutes(61)),
                Stored("OLD-PENDING", RegistrationRequestStatus.Pending, app.Time.GetUtcNow() - TimeSpan.FromMinutes(90)),
                Stored("RECENT-DENIED", RegistrationRequestStatus.Denied, app.Time.GetUtcNow() - TimeSpan.FromMinutes(30)),
                Stored("RECENT-EXPIRED", RegistrationRequestStatus.Pending, app.Time.GetUtcNow() - TimeSpan.FromMinutes(20)));
            await db.SaveChangesAsync();
            return 0;
        });

        Assert.Equal(HttpStatusCode.Accepted, (await CreateAsync(client, "PC-NEW")).StatusCode);

        var names = await app.WithDbAsync(db => db.PcRegistrationRequests.Select(r => r.Name).ToListAsync());
        Assert.Contains("RECENT-DENIED", names);
        Assert.Contains("RECENT-EXPIRED", names);
        Assert.DoesNotContain("OLD-DENIED", names);
        Assert.DoesNotContain("OLD-PENDING", names);
    }

    [Fact]
    public async Task Create_WithBodyOver4Kb_IsRejectedCleanly()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();
        var body = $$"""{"name":"PC-01","nonce":"{{TestApp.NewNonce()}}","padding":"{{new string('x', ChannelGuard.MaxClientBodyBytes)}}"}""";

        var response = await client.PostAsync("/api/client/register-requests", new StringContent(body, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal(0, await app.WithDbAsync(db => db.PcRegistrationRequests.CountAsync()));
    }

    [Fact]
    public async Task OldRegisterRoute_IsGone()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();

        var response = await client.PostAsJsonAsync("/api/client/register", new { name = "PC-01", setupSecret = "AAAAAAAAAA" });

        // Served like any unknown path: nothing is created and nothing is answered as a success.
        Assert.False(response.IsSuccessStatusCode);
        Assert.Equal(0, await app.WithDbAsync(db => db.Pcs.CountAsync()));
        Assert.Equal(0, await app.WithDbAsync(db => db.PcRegistrationRequests.CountAsync()));
    }

    // Status

    [Fact]
    public async Task Status_ReadsPending_ThenApproved_AfterAccept()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();
        var token = await CreateTokenAsync(client, "PC-01");
        Assert.Equal("pending", await StatusOfAsync(client, token));

        using var admin = await app.SignedInAdminAsync();
        var id = (await StoredByNameAsync(app, "PC-01")).Id;
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsJsonAsync($"/api/registration-requests/{id}/accept", new { })).StatusCode);

        Assert.Equal("approved", await StatusOfAsync(client, token));
    }

    [Fact]
    public async Task Status_ReadsDenied_AfterDeny()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();
        var token = await CreateTokenAsync(client, "PC-01");
        using var admin = await app.SignedInAdminAsync();
        var id = (await StoredByNameAsync(app, "PC-01")).Id;
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsJsonAsync($"/api/registration-requests/{id}/deny", new { })).StatusCode);

        Assert.Equal("denied", await StatusOfAsync(client, token));
    }

    [Fact]
    public async Task Status_ReadsExpired_OnlyAfterTenMinutes()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();
        var token = await CreateTokenAsync(client, "PC-01");

        // Times are stored to the millisecond, so the boundary itself is checked a few seconds either side.
        app.Time.Advance(TimeSpan.FromMinutes(10) - TimeSpan.FromSeconds(1));
        Assert.Equal("pending", await StatusOfAsync(client, token));

        app.Time.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal("expired", await StatusOfAsync(client, token));
    }

    [Fact]
    public async Task Status_ReadsExpired_ForACancelledRequest()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();
        var token = await CreateTokenAsync(client, "PC-01");
        Assert.Equal(HttpStatusCode.NoContent, (await CancelAsync(client, $"Bearer {token}")).StatusCode);

        Assert.Equal("expired", await StatusOfAsync(client, token));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Bearer")]
    [InlineData("Bearer ")]
    [InlineData("Basic dXNlcjpwYXNz")]
    [InlineData("Bearer not-a-known-token")]
    public async Task Status_WithMissingOrUnknownToken_Returns401(string? authorization)
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();
        await CreateTokenAsync(client, "PC-01");

        var response = await StatusAsync(client, authorization);

        await AssertErrorAsync(response, HttpStatusCode.Unauthorized, NotRegisteredMessage);
    }

    [Fact]
    public async Task Status_WithTheRawTokenOrItsHash_Returns401()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();
        var token = await CreateTokenAsync(client, "PC-01");

        Assert.Equal(HttpStatusCode.Unauthorized, (await StatusAsync(client, token)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await StatusAsync(client, $"Bearer {HashOf(token)}")).StatusCode);
    }

    [Fact]
    public async Task Status_UsesTheTrafficBudget()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();

        for (var i = 0; i < ClientEndpoints.MaxTrafficPerMinute; i++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await StatusAsync(client, "Bearer x")).StatusCode);
        }

        await AssertErrorAsync(await StatusAsync(client, "Bearer x"), HttpStatusCode.TooManyRequests);
    }

    // Cancel

    [Fact]
    public async Task Cancel_PendingRequest_Returns204_AndTheRequestReadsExpired()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();
        var token = await CreateTokenAsync(client, "PC-01");

        var response = await CancelAsync(client, $"Bearer {token}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(RegistrationRequestStatus.Cancelled, (await StoredByNameAsync(app, "PC-01")).Status);
        Assert.Equal("expired", await StatusOfAsync(client, token));
    }

    [Fact]
    public async Task Cancel_AfterApproval_Returns204_AndLeavesThePcRegistered()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();
        var token = await app.RegisterPcAsync(client, "PC-01");

        Assert.Equal(HttpStatusCode.NoContent, (await CancelAsync(client, $"Bearer {token}")).StatusCode);

        Assert.Equal(RegistrationRequestStatus.Approved, (await StoredByNameAsync(app, "PC-01")).Status);
        Assert.Equal(HttpStatusCode.OK, (await HeartbeatAsync(client, $"Bearer {token}")).StatusCode);
    }

    [Fact]
    public async Task Cancel_AfterDenial_Returns204_AndStaysDenied()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();
        var token = await CreateTokenAsync(client, "PC-01");
        using var admin = await app.SignedInAdminAsync();
        var id = (await StoredByNameAsync(app, "PC-01")).Id;
        await admin.PostAsJsonAsync($"/api/registration-requests/{id}/deny", new { });

        Assert.Equal(HttpStatusCode.NoContent, (await CancelAsync(client, $"Bearer {token}")).StatusCode);

        Assert.Equal("denied", await StatusOfAsync(client, token));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Bearer not-a-known-token")]
    public async Task Cancel_WithMissingOrUnknownToken_Returns401(string? authorization)
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();
        await CreateTokenAsync(client, "PC-01");

        await AssertErrorAsync(await CancelAsync(client, authorization), HttpStatusCode.Unauthorized, NotRegisteredMessage);
        Assert.Equal(RegistrationRequestStatus.Pending, (await StoredByNameAsync(app, "PC-01")).Status);
    }

    // The token before approval

    [Fact]
    public async Task Heartbeat_WithAPendingToken_Returns401()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();
        var token = await CreateTokenAsync(client, "PC-01");

        var response = await HeartbeatAsync(client, $"Bearer {token}");

        await AssertErrorAsync(response, HttpStatusCode.Unauthorized, NotRegisteredMessage);
        Assert.Equal(0, await app.WithDbAsync(db => db.Pcs.CountAsync()));
    }

    [Fact]
    public async Task Heartbeat_WithADeniedOrCancelledToken_Returns401()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();
        var denied = await CreateTokenAsync(client, "PC-01");
        using var admin = await app.SignedInAdminAsync();
        await admin.PostAsJsonAsync($"/api/registration-requests/{(await StoredByNameAsync(app, "PC-01")).Id}/deny", new { });
        var cancelled = await CreateTokenAsync(client, "PC-02");
        await CancelAsync(client, $"Bearer {cancelled}");

        Assert.Equal(HttpStatusCode.Unauthorized, (await HeartbeatAsync(client, $"Bearer {denied}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await HeartbeatAsync(client, $"Bearer {cancelled}")).StatusCode);
    }

    [Fact]
    public async Task AlreadyRegisteredPc_KeepsWorkingWithItsToken_WhileOthersRequest()
    {
        using var app = new TestApp();
        using var registered = app.CreatePcClient("192.168.1.10");
        using var newcomer = app.CreatePcClient("192.168.1.11");
        var token = await app.RegisterPcAsync(registered, "PC-01");
        Assert.Equal(HttpStatusCode.OK, (await HeartbeatAsync(registered, $"Bearer {token}")).StatusCode);

        await CreateTokenAsync(newcomer, "PC-02");
        app.Time.Advance(TimeSpan.FromMinutes(15));

        Assert.Equal(HttpStatusCode.OK, (await HeartbeatAsync(registered, $"Bearer {token}")).StatusCode);
    }

    [Fact]
    public async Task PcPort_ServesNoRegistrationRequestsOnTheAdminPath()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/registration-requests")).StatusCode);
    }

    private static PcRegistrationRequest Stored(string name, RegistrationRequestStatus status, DateTimeOffset requestedAt) =>
        new()
        {
            Name = name,
            Nonce = TestApp.NewNonce(),
            PairingNumber = "000000",
            TokenHash = Guid.NewGuid().ToString("N"),
            RequestedAtUtc = requestedAt,
            Status = status,
        };

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
}
