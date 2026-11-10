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
    private const string BadNonceMessage = "Nonce must be 64 lowercase hex characters.";
    private const string RevealPath = "/api/client/register-requests/reveal";

    private static async Task<JsonElement> JsonOf(HttpResponseMessage response)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.Clone();
    }

    private static Task<HttpResponseMessage> CreateAsync(HttpClient pc, string? commitment = null) =>
        pc.PostAsync("/api/client/register-requests", TestApp.RegistrationRequestBody(commitment));

    private static async Task<string> CreateTokenAsync(HttpClient pc)
    {
        var response = await CreateAsync(pc);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return (await JsonOf(response)).GetProperty("token").GetString()!;
    }

    // Creates a request for a nonce the test keeps, so the test can reveal it later. Returns the token and the server's nonce.
    private static async Task<(string Token, string ServerNonce)> CommitAsync(HttpClient pc, string nonce)
    {
        var response = await CreateAsync(pc, TestApp.CommitmentOf(nonce));
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var body = await JsonOf(response);
        return (body.GetProperty("token").GetString()!, body.GetProperty("serverNonce").GetString()!);
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

    private static Task<HttpResponseMessage> RevealWithAuthorizationAsync(HttpClient client, string? authorization, string? nonce)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, RevealPath) { Content = TestApp.RevealBody(nonce) };
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
    public async Task Create_Succeeds_Returns202WithTheTokenAndServerNonce_AndStoresOnlyTheTokenHash()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient("192.168.1.77");

        var response = await CreateAsync(client);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var body = await JsonOf(response);
        Assert.Equal(new[] { "serverNonce", "token" }, body.EnumerateObject().Select(p => p.Name).Order().ToArray());
        var token = body.GetProperty("token").GetString()!;
        Assert.Equal(43, token.Length);
        Assert.Matches("^[A-Za-z0-9_-]+$", token);
        var serverNonce = body.GetProperty("serverNonce").GetString()!;
        Assert.Matches("^[0-9a-f]{64}$", serverNonce);

        var stored = await StoredByNameAsync(app, "PC-1");
        Assert.Equal(HashOf(token), stored.TokenHash);
        Assert.NotEqual(token, stored.TokenHash);
        Assert.Equal(serverNonce, stored.ServerNonce);
        Assert.Equal("192.168.1.77", stored.IpAddress);
        Assert.Equal(RegistrationRequestStatus.Pending, stored.Status);
        Assert.Equal(app.Time.GetUtcNow().ToUnixTimeMilliseconds(), stored.RequestedAtUtc.ToUnixTimeMilliseconds());
        Assert.Equal(0, await app.WithDbAsync(db => db.Pcs.CountAsync()));
    }

    [Fact]
    public async Task Create_StoresTheCommitment_AndLeavesTheNonceAndPairingNumberUntilReveal()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();
        var nonce = TestApp.NewNonce();

        var (_, serverNonce) = await CommitAsync(client, nonce);

        var stored = await StoredByNameAsync(app, "PC-1");
        Assert.Equal(TestApp.CommitmentOf(nonce), stored.Commitment);
        Assert.Equal(serverNonce, stored.ServerNonce);
        Assert.Null(stored.Nonce);
        Assert.Null(stored.PairingNumber);
    }

    [Fact]
    public async Task Create_GivesEachRequestItsOwnServerNonce()
    {
        using var app = new TestApp();
        using var first = app.CreatePcClient("192.168.1.31");
        using var second = app.CreatePcClient("192.168.1.32");
        var commitment = TestApp.NewCommitment();

        var firstNonce = (await CommitAsync(first, TestApp.NewNonce())).ServerNonce;
        var secondResponse = await CreateAsync(second, commitment);

        Assert.NotEqual(firstNonce, (await JsonOf(secondResponse)).GetProperty("serverNonce").GetString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("ABCDEF")]
    [InlineData("0123456789abcdef")]
    [InlineData("ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef0")]
    [InlineData("g123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
    [InlineData(" 0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcde")]
    public async Task Create_WithInvalidCommitment_Returns400AndStoresNothing(string commitment)
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();

        var response = await CreateAsync(client, commitment);

        await AssertErrorAsync(response, HttpStatusCode.BadRequest, BadNonceMessage);
        Assert.Equal(0, await app.WithDbAsync(db => db.PcRegistrationRequests.CountAsync()));
    }

    [Fact]
    public async Task Create_WithoutACommitment_Returns400()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();

        var response = await client.PostAsJsonAsync("/api/client/register-requests", new { name = "PC-1" });

        await AssertErrorAsync(response, HttpStatusCode.BadRequest, BadNonceMessage);
    }

    [Theory]
    [InlineData("{not json", "application/json")]
    [InlineData("null", "application/json")]
    [InlineData("""{"name":"PC-1"}""", "text/plain")]
    public async Task Create_WithMalformedBodyOrContentType_Returns400(string body, string contentType)
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();

        var response = await client.PostAsync("/api/client/register-requests", new StringContent(body, Encoding.UTF8, contentType));

        await AssertErrorAsync(response, HttpStatusCode.BadRequest, "Invalid request.");
        Assert.Equal(0, await app.WithDbAsync(db => db.PcRegistrationRequests.CountAsync()));
    }

    // Automatic names

    private static async Task<string> NameOfRequestAsync(TestApp app, string token) =>
        (await app.WithDbAsync(db => db.PcRegistrationRequests.AsNoTracking().SingleAsync(r => r.TokenHash == HashOf(token)))).Name;

    [Fact]
    public async Task Create_FromDifferentAddresses_AssignsPc1ThenPc2()
    {
        using var app = new TestApp();
        using var first = app.CreatePcClient("192.168.1.31");
        using var second = app.CreatePcClient("192.168.1.32");

        var firstToken = await CreateTokenAsync(first);
        var secondToken = await CreateTokenAsync(second);

        Assert.Equal("PC-1", await NameOfRequestAsync(app, firstToken));
        Assert.Equal("PC-2", await NameOfRequestAsync(app, secondToken));
    }

    [Fact]
    public async Task Create_AgainFromTheSameAddress_GetsTheSameNumberBack()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient("192.168.1.31");
        using var other = app.CreatePcClient("192.168.1.32");
        await CreateTokenAsync(client);
        await CreateTokenAsync(other);

        var again = await CreateTokenAsync(client);

        Assert.Equal("PC-1", await NameOfRequestAsync(app, again));
    }

    [Fact]
    public async Task Create_AfterAPendingRequestWasCancelled_ReusesItsNumber()
    {
        using var app = new TestApp();
        using var first = app.CreatePcClient("192.168.1.31");
        using var second = app.CreatePcClient("192.168.1.32");
        var firstToken = await CreateTokenAsync(first);
        Assert.Equal(HttpStatusCode.NoContent, (await CancelAsync(first, $"Bearer {firstToken}")).StatusCode);

        var secondToken = await CreateTokenAsync(second);

        Assert.Equal("PC-1", await NameOfRequestAsync(app, secondToken));
    }

    [Fact]
    public async Task Create_APendingRequestReservesItsName_ForOtherAddresses()
    {
        using var app = new TestApp();
        var pcs = Enumerable.Range(1, 3).Select(i => app.CreatePcClient($"192.168.1.{30 + i}")).ToList();
        try
        {
            var names = new List<string>();
            foreach (var pc in pcs)
            {
                names.Add(await NameOfRequestAsync(app, await CreateTokenAsync(pc)));
            }

            Assert.Equal(new[] { "PC-1", "PC-2", "PC-3" }, names);
        }
        finally
        {
            pcs.ForEach(pc => pc.Dispose());
        }
    }

    [Fact]
    public async Task Create_AfterAPcIsRemoved_ReusesItsNumber()
    {
        using var app = new TestApp();
        using var first = app.CreatePcClient("192.168.1.31");
        using var second = app.CreatePcClient("192.168.1.32");
        using var third = app.CreatePcClient("192.168.1.33");
        await app.RegisterPcAsync(first);
        await app.RegisterPcAsync(second);
        await app.WithDbAsync(async db =>
        {
            db.Pcs.RemoveRange(db.Pcs.Where(p => p.Name == "PC-1"));
            await db.SaveChangesAsync();
            return 0;
        });

        var token = await CreateTokenAsync(third);

        Assert.Equal("PC-1", await NameOfRequestAsync(app, token));
    }

    [Fact]
    public async Task Create_SkipsNumbersTakenByPcsInAnyCase_AndIgnoresOtherNames()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();
        await app.WithDbAsync(async db =>
        {
            foreach (var name in new[] { "pc-1", "PC-3", "Lab-2", "PC-02" })
            {
                db.Pcs.Add(new Pc
                {
                    Name = name,
                    TokenHash = $"hash-{name}",
                    RegisteredAtUtc = app.Time.GetUtcNow(),
                    LastSeenUtc = app.Time.GetUtcNow(),
                });
            }

            await db.SaveChangesAsync();
            return 0;
        });

        var token = await CreateTokenAsync(client);

        Assert.Equal("PC-2", await NameOfRequestAsync(app, token));
    }

    [Fact]
    public async Task Create_DoesNotReserveTheNameOfAnExpiredRequest()
    {
        using var app = new TestApp();
        using var first = app.CreatePcClient("192.168.1.31");
        using var second = app.CreatePcClient("192.168.1.32");
        await CreateTokenAsync(first);
        app.Time.Advance(TimeSpan.FromMinutes(11));

        var token = await CreateTokenAsync(second);

        Assert.Equal("PC-1", await NameOfRequestAsync(app, token));
    }

    [Fact]
    public async Task Status_ReturnsNoName_WhilePending_AndTheAssignedNameOnceApproved()
    {
        using var app = new TestApp();
        using var other = app.CreatePcClient("192.168.1.32");
        using var client = app.CreatePcClient("192.168.1.31");
        await app.RegisterPcAsync(other);
        var token = await app.RequestAndRevealAsync(client);

        var pending = await JsonOf(await StatusAsync(client, $"Bearer {token}"));
        Assert.Equal("pending", pending.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, pending.GetProperty("name").ValueKind);

        var id = await app.WithDbAsync(db => db.PcRegistrationRequests.Where(r => r.TokenHash == HashOf(token)).Select(r => r.Id).SingleAsync());
        using var admin = await app.SignedInAdminAsync();
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsJsonAsync($"/api/registration-requests/{id}/accept", new { })).StatusCode);

        var approved = await JsonOf(await StatusAsync(client, $"Bearer {token}"));
        Assert.Equal("approved", approved.GetProperty("status").GetString());
        Assert.Equal("PC-2", approved.GetProperty("name").GetString());
    }

    [Fact]
    public async Task Status_ReturnsNoName_WhenDenied()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();
        var token = await app.RequestAndRevealAsync(client);
        var id = await app.WithDbAsync(db => db.PcRegistrationRequests.Select(r => r.Id).SingleAsync());
        using var admin = await app.SignedInAdminAsync();
        await admin.PostAsJsonAsync($"/api/registration-requests/{id}/deny", new { });

        var denied = await JsonOf(await StatusAsync(client, $"Bearer {token}"));

        Assert.Equal("denied", denied.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, denied.GetProperty("name").ValueKind);
    }

    [Fact]
    public async Task Create_EleventhAttemptInAMinute_Returns429_ForThatAddressOnly()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient("192.168.1.50");
        using var otherClient = app.CreatePcClient("192.168.1.51");

        for (var i = 0; i < ClientEndpoints.MaxRegistrationsPerMinute; i++)
        {
            Assert.Equal(HttpStatusCode.Accepted, (await CreateAsync(client)).StatusCode);
        }

        var limited = await CreateAsync(client);

        await AssertErrorAsync(limited, HttpStatusCode.TooManyRequests, "Too many registration attempts. Try again in a minute.");
        Assert.Equal(HttpStatusCode.Accepted, (await CreateAsync(otherClient)).StatusCode);
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
                Assert.Equal(HttpStatusCode.Accepted, (await CreateAsync(pcs[i])).StatusCode);
            }

            var full = await CreateAsync(extra);

            await AssertErrorAsync(full, HttpStatusCode.TooManyRequests, "Too many PCs are waiting for approval. Try again later.");
            Assert.Equal(ClientEndpoints.MaxPendingRegistrationRequests, await app.WithDbAsync(db => db.PcRegistrationRequests.CountAsync()));

            app.Time.Advance(TimeSpan.FromMinutes(11));
            Assert.Equal(HttpStatusCode.Accepted, (await CreateAsync(extra)).StatusCode);
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
        var first = await CreateTokenAsync(client);

        var second = await CreateTokenAsync(client);

        Assert.Equal("expired", await StatusOfAsync(client, first));
        Assert.Equal("pending", await StatusOfAsync(client, second));
    }

    [Fact]
    public async Task Create_FromAnotherAddress_DoesNotCancelTheFirstRequest()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient("192.168.1.80");
        using var other = app.CreatePcClient("192.168.1.81");
        var first = await CreateTokenAsync(client);

        await CreateTokenAsync(other);

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

        Assert.Equal(HttpStatusCode.Accepted, (await CreateAsync(client)).StatusCode);

        var names = await app.WithDbAsync(db => db.PcRegistrationRequests.Select(r => r.Name).ToListAsync());
        Assert.Contains("PC-1", names);
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
        var body = $$"""{"commitment":"{{TestApp.NewCommitment()}}","padding":"{{new string('x', ChannelGuard.MaxClientBodyBytes)}}"}""";

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

    // Reveal

    [Fact]
    public async Task Reveal_WithTheNonceThatMatchesTheCommitment_Returns204_AndStoresTheNonceAndPairingNumber()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();
        var nonce = TestApp.NewNonce();
        var (token, serverNonce) = await CommitAsync(client, nonce);

        var response = await TestApp.RevealAsync(client, token, nonce);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var stored = await StoredByNameAsync(app, "PC-1");
        Assert.Equal(nonce, stored.Nonce);
        Assert.Equal(
            PairingNumber.Compute(app.Channel.Certificate, Convert.FromHexString(nonce), Convert.FromHexString(serverNonce)),
            stored.PairingNumber);
        Assert.Matches("^[0-9]{6}$", stored.PairingNumber!);
        Assert.Equal(RegistrationRequestStatus.Pending, stored.Status);
    }

    [Fact]
    public async Task Reveal_WithAWrongNonce_Returns400_CancelsTheRequest_AndItCannotBeAccepted()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();
        var (token, _) = await CommitAsync(client, TestApp.NewNonce());

        var response = await TestApp.RevealAsync(client, token, TestApp.NewNonce());

        await AssertErrorAsync(response, HttpStatusCode.BadRequest, "The nonce doesn't match the commitment. The request has been cancelled.");
        var stored = await StoredByNameAsync(app, "PC-1");
        Assert.Equal(RegistrationRequestStatus.Cancelled, stored.Status);
        Assert.Null(stored.Nonce);
        Assert.Null(stored.PairingNumber);
        Assert.Equal("expired", await StatusOfAsync(client, token));

        using var admin = await app.SignedInAdminAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PostAsJsonAsync($"/api/registration-requests/{stored.Id}/accept", new { })).StatusCode);
        Assert.Equal(0, await app.WithDbAsync(db => db.Pcs.CountAsync()));
    }

    [Fact]
    public async Task Reveal_WithTheRightNonceAfterAWrongOne_Returns409()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();
        var nonce = TestApp.NewNonce();
        var (token, _) = await CommitAsync(client, nonce);
        await TestApp.RevealAsync(client, token, TestApp.NewNonce());

        var response = await TestApp.RevealAsync(client, token, nonce);

        await AssertErrorAsync(response, HttpStatusCode.Conflict);
        Assert.Null((await StoredByNameAsync(app, "PC-1")).Nonce);
    }

    [Fact]
    public async Task Reveal_SameNonceAgainSucceeds_ADifferentOneReturns409_AndNeverReplacesTheFirstNonce()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();
        var nonce = TestApp.NewNonce();
        var (token, _) = await CommitAsync(client, nonce);
        Assert.Equal(HttpStatusCode.NoContent, (await TestApp.RevealAsync(client, token, nonce)).StatusCode);
        var pairingNumber = (await StoredByNameAsync(app, "PC-1")).PairingNumber;

        Assert.Equal(HttpStatusCode.NoContent, (await TestApp.RevealAsync(client, token, nonce)).StatusCode);
        await AssertErrorAsync(await TestApp.RevealAsync(client, token, TestApp.NewNonce()), HttpStatusCode.Conflict);

        var stored = await StoredByNameAsync(app, "PC-1");
        Assert.Equal(nonce, stored.Nonce);
        Assert.Equal(pairingNumber, stored.PairingNumber);
        Assert.Equal(RegistrationRequestStatus.Pending, stored.Status);
        Assert.Equal("pending", await StatusOfAsync(client, token));
    }

    [Fact]
    public async Task Reveal_BeforeTheRequestExpires_Returns204()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();
        var nonce = TestApp.NewNonce();
        var (token, _) = await CommitAsync(client, nonce);

        // Times are stored to the millisecond, so the boundary itself is checked a few seconds either side.
        app.Time.Advance(TimeSpan.FromMinutes(10) - TimeSpan.FromSeconds(1));

        Assert.Equal(HttpStatusCode.NoContent, (await TestApp.RevealAsync(client, token, nonce)).StatusCode);
        Assert.Equal(nonce, (await StoredByNameAsync(app, "PC-1")).Nonce);
    }

    [Fact]
    public async Task Reveal_AfterTheRequestExpires_Returns409_AndStoresNoNonce()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();
        var nonce = TestApp.NewNonce();
        var (token, _) = await CommitAsync(client, nonce);

        app.Time.Advance(TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(1));

        await AssertErrorAsync(await TestApp.RevealAsync(client, token, nonce), HttpStatusCode.Conflict);
        var stored = await StoredByNameAsync(app, "PC-1");
        Assert.Null(stored.Nonce);
        Assert.Null(stored.PairingNumber);
        Assert.Equal("expired", await StatusOfAsync(client, token));
    }

    [Fact]
    public async Task Reveal_AfterTheRequestIsCancelled_Returns409()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();
        var nonce = TestApp.NewNonce();
        var (token, _) = await CommitAsync(client, nonce);
        Assert.Equal(HttpStatusCode.NoContent, (await CancelAsync(client, $"Bearer {token}")).StatusCode);

        await AssertErrorAsync(await TestApp.RevealAsync(client, token, nonce), HttpStatusCode.Conflict);
        Assert.Null((await StoredByNameAsync(app, "PC-1")).Nonce);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcde")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef0")]
    [InlineData("g123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
    [InlineData(" 0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcde")]
    public async Task Reveal_WithAMalformedNonce_Returns400_AndLeavesTheRequestPending(string? nonce)
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();
        var (token, _) = await CommitAsync(client, TestApp.NewNonce());

        await AssertErrorAsync(await TestApp.RevealAsync(client, token, nonce), HttpStatusCode.BadRequest, BadNonceMessage);

        var stored = await StoredByNameAsync(app, "PC-1");
        Assert.Equal(RegistrationRequestStatus.Pending, stored.Status);
        Assert.Null(stored.Nonce);
    }

    [Theory]
    [InlineData("{not json", "application/json")]
    [InlineData("null", "application/json")]
    [InlineData("{}", "text/plain")]
    public async Task Reveal_WithAMalformedBody_Returns400_AndLeavesTheRequestPending(string body, string contentType)
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();
        var (token, _) = await CommitAsync(client, TestApp.NewNonce());
        using var request = new HttpRequestMessage(HttpMethod.Post, RevealPath)
        {
            Content = new StringContent(body, Encoding.UTF8, contentType),
        };
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");

        await AssertErrorAsync(await client.SendAsync(request), HttpStatusCode.BadRequest, "Invalid request.");

        Assert.Equal(RegistrationRequestStatus.Pending, (await StoredByNameAsync(app, "PC-1")).Status);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Bearer")]
    [InlineData("Bearer not-a-known-token")]
    [InlineData("Basic dXNlcjpwYXNz")]
    public async Task Reveal_WithMissingOrUnknownToken_Returns401(string? authorization)
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();
        var nonce = TestApp.NewNonce();
        await CommitAsync(client, nonce);

        await AssertErrorAsync(await RevealWithAuthorizationAsync(client, authorization, nonce), HttpStatusCode.Unauthorized, NotRegisteredMessage);
        Assert.Null((await StoredByNameAsync(app, "PC-1")).Nonce);
    }

    [Fact]
    public async Task Reveal_WithTheTokenHash_Returns401()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();
        var nonce = TestApp.NewNonce();
        var (token, _) = await CommitAsync(client, nonce);

        var response = await RevealWithAuthorizationAsync(client, $"Bearer {HashOf(token)}", nonce);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null((await StoredByNameAsync(app, "PC-1")).Nonce);
    }

    [Fact]
    public async Task Reveal_UsesTheTrafficBudget()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();
        var nonce = TestApp.NewNonce();

        for (var i = 0; i < ClientEndpoints.MaxTrafficPerMinute; i++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await RevealWithAuthorizationAsync(client, "Bearer x", nonce)).StatusCode);
        }

        await AssertErrorAsync(await RevealWithAuthorizationAsync(client, "Bearer x", nonce), HttpStatusCode.TooManyRequests);
    }

    // Status

    [Fact]
    public async Task Status_ReadsPending_ThenApproved_AfterAccept()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();
        var token = await app.RequestAndRevealAsync(client);
        Assert.Equal("pending", await StatusOfAsync(client, token));

        using var admin = await app.SignedInAdminAsync();
        var id = (await StoredByNameAsync(app, "PC-1")).Id;
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsJsonAsync($"/api/registration-requests/{id}/accept", new { })).StatusCode);

        Assert.Equal("approved", await StatusOfAsync(client, token));
    }

    [Fact]
    public async Task Status_ReadsDenied_AfterDeny()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();
        var token = await app.RequestAndRevealAsync(client);
        using var admin = await app.SignedInAdminAsync();
        var id = (await StoredByNameAsync(app, "PC-1")).Id;
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsJsonAsync($"/api/registration-requests/{id}/deny", new { })).StatusCode);

        Assert.Equal("denied", await StatusOfAsync(client, token));
    }

    [Fact]
    public async Task Status_ReadsExpired_OnlyAfterTenMinutes()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();
        var token = await CreateTokenAsync(client);

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
        var token = await CreateTokenAsync(client);
        Assert.Equal(HttpStatusCode.NoContent, (await CancelAsync(client, $"Bearer {token}")).StatusCode);

        Assert.Equal("expired", await StatusOfAsync(client, token));
    }

    [Fact]
    public async Task Status_ReadsApproved_WhenTheRequestRowIsGone_AndThePcIsRegistered()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();
        var token = await app.RegisterPcAsync(client);
        var tokenHash = HashOf(token);
        await app.WithDbAsync(db => db.PcRegistrationRequests.Where(r => r.TokenHash == tokenHash).ExecuteDeleteAsync());

        Assert.Equal("approved", await StatusOfAsync(client, token));
        Assert.Equal(HttpStatusCode.OK, (await HeartbeatAsync(client, $"Bearer {token}")).StatusCode);
    }

    [Fact]
    public async Task Status_WithARemovedRequestAndNoRegisteredPc_Returns401()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();
        var token = await CreateTokenAsync(client);
        var tokenHash = HashOf(token);
        await app.WithDbAsync(db => db.PcRegistrationRequests.Where(r => r.TokenHash == tokenHash).ExecuteDeleteAsync());

        await AssertErrorAsync(await StatusAsync(client, $"Bearer {token}"), HttpStatusCode.Unauthorized, NotRegisteredMessage);
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
        await CreateTokenAsync(client);

        var response = await StatusAsync(client, authorization);

        await AssertErrorAsync(response, HttpStatusCode.Unauthorized, NotRegisteredMessage);
    }

    [Fact]
    public async Task Status_WithTheRawTokenOrItsHash_Returns401()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();
        var token = await CreateTokenAsync(client);

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
        var token = await CreateTokenAsync(client);

        var response = await CancelAsync(client, $"Bearer {token}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(RegistrationRequestStatus.Cancelled, (await StoredByNameAsync(app, "PC-1")).Status);
        Assert.Equal("expired", await StatusOfAsync(client, token));
    }

    [Fact]
    public async Task Cancel_AfterApproval_Returns409WithApprovedStatus_AndLeavesThePcRegistered()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();
        var token = await app.RegisterPcAsync(client);

        var response = await CancelAsync(client, $"Bearer {token}");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await JsonOf(response);
        Assert.Equal(new[] { "name", "status" }, body.EnumerateObject().Select(p => p.Name).Order().ToArray());
        Assert.Equal("approved", body.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("name").ValueKind);
        Assert.Equal(RegistrationRequestStatus.Approved, (await StoredByNameAsync(app, "PC-1")).Status);
        Assert.Equal(HttpStatusCode.OK, (await HeartbeatAsync(client, $"Bearer {token}")).StatusCode);
    }

    [Fact]
    public async Task Cancel_AfterDenial_Returns204_AndStaysDenied()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();
        var token = await app.RequestAndRevealAsync(client);
        using var admin = await app.SignedInAdminAsync();
        var id = (await StoredByNameAsync(app, "PC-1")).Id;
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
        await CreateTokenAsync(client);

        await AssertErrorAsync(await CancelAsync(client, authorization), HttpStatusCode.Unauthorized, NotRegisteredMessage);
        Assert.Equal(RegistrationRequestStatus.Pending, (await StoredByNameAsync(app, "PC-1")).Status);
    }

    // The token before approval

    [Fact]
    public async Task Heartbeat_WithAPendingToken_Returns401()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();
        var token = await CreateTokenAsync(client);

        var response = await HeartbeatAsync(client, $"Bearer {token}");

        await AssertErrorAsync(response, HttpStatusCode.Unauthorized, NotRegisteredMessage);
        Assert.Equal(0, await app.WithDbAsync(db => db.Pcs.CountAsync()));
    }

    [Fact]
    public async Task Heartbeat_WhenRevealedButNotAccepted_IsRefused_UntilAccepted()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();
        var token = await app.RequestAndRevealAsync(client);

        await AssertErrorAsync(await HeartbeatAsync(client, $"Bearer {token}"), HttpStatusCode.Unauthorized, NotRegisteredMessage);
        Assert.Equal(0, await app.WithDbAsync(db => db.Pcs.CountAsync()));

        using var admin = await app.SignedInAdminAsync();
        var id = (await StoredByNameAsync(app, "PC-1")).Id;
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsJsonAsync($"/api/registration-requests/{id}/accept", new { })).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await HeartbeatAsync(client, $"Bearer {token}")).StatusCode);
    }

    [Fact]
    public async Task Heartbeat_WithADeniedOrCancelledToken_Returns401()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();
        var denied = await app.RequestAndRevealAsync(client);
        using var admin = await app.SignedInAdminAsync();
        await admin.PostAsJsonAsync($"/api/registration-requests/{(await StoredByNameAsync(app, "PC-1")).Id}/deny", new { });
        var cancelled = await CreateTokenAsync(client);
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
        var token = await app.RegisterPcAsync(registered);
        Assert.Equal(HttpStatusCode.OK, (await HeartbeatAsync(registered, $"Bearer {token}")).StatusCode);

        await CreateTokenAsync(newcomer);
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
            Commitment = TestApp.NewCommitment(),
            ServerNonce = TestApp.NewNonce(),
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
