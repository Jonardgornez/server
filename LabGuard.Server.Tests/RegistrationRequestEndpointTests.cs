using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LabGuard.Server.Auth;
using LabGuard.Server.Data;
using LabGuard.Server.Network;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LabGuard.Server.Tests;

public class RegistrationRequestEndpointTests
{
    private const string RequestsPath = "/api/registration-requests";

    private static async Task<JsonElement> JsonOf(HttpResponseMessage response)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.Clone();
    }

    // Each request comes from its own address, so the per-address registration limit never applies.
    private static string AddressFor(int n) => $"10.0.0.{n}";

    // Creates a request and reveals its nonce, so the administrator can see it. Returns the token the PC holds.
    private static async Task<string> RequestAsync(TestApp app, string address)
    {
        using var pc = app.CreatePcClient(address);
        return await app.RequestAndRevealAsync(pc);
    }

    // Creates a request and keeps the nonce to itself, so the administrator can't see it yet.
    private static async Task<string> UnrevealedRequestAsync(TestApp app, string address)
    {
        using var pc = app.CreatePcClient(address);
        var response = await pc.PostAsync("/api/client/register-requests", TestApp.RegistrationRequestBody());
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return (await JsonOf(response)).GetProperty("token").GetString()!;
    }

    private static async Task<string> StatusOfAsync(TestApp app, string address, string token)
    {
        using var pc = app.CreatePcClient(address);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/client/register-requests/status");
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
        var response = await pc.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await JsonOf(response)).GetProperty("status").GetString()!;
    }

    // Names are assigned by the server, so a request is found by the address it came from.
    private static Task<PcRegistrationRequest> StoredAsync(TestApp app, string address) =>
        app.WithDbAsync(db => db.PcRegistrationRequests.AsNoTracking().SingleAsync(r => r.IpAddress == address));

    private static async Task<int> IdOfAsync(TestApp app, string address) => (await StoredAsync(app, address)).Id;

    private static Task<HttpResponseMessage> AcceptAsync(HttpClient admin, int id) =>
        admin.PostAsJsonAsync($"{RequestsPath}/{id}/accept", new { });

    private static Task<HttpResponseMessage> DenyAsync(HttpClient admin, int id) =>
        admin.PostAsJsonAsync($"{RequestsPath}/{id}/deny", new { });

    private static async Task<JsonElement> ListAsync(HttpClient admin)
    {
        var response = await admin.GetAsync(RequestsPath);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await JsonOf(response)).GetProperty("requests");
    }

    private static string HashOf(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

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

    // List

    [Fact]
    public async Task List_ShowsOnlyPendingUnexpiredRequests_OldestFirst()
    {
        using var app = new TestApp();
        await RequestAsync(app, AddressFor(1));
        app.Time.Advance(TimeSpan.FromMinutes(11));
        await RequestAsync(app, AddressFor(2));
        app.Time.Advance(TimeSpan.FromMinutes(1));
        await RequestAsync(app, AddressFor(3));
        await RequestAsync(app, AddressFor(4));
        await RequestAsync(app, AddressFor(5));
        var cancelledToken = await RequestAsync(app, AddressFor(6));
        using var admin = await app.SignedInAdminAsync();
        await DenyAsync(admin, await IdOfAsync(app, AddressFor(4)));
        await AcceptAsync(admin, await IdOfAsync(app, AddressFor(5)));
        using var cancelling = app.CreatePcClient(AddressFor(6));
        using var cancel = new HttpRequestMessage(HttpMethod.Post, "/api/client/register-requests/cancel");
        cancel.Headers.TryAddWithoutValidation("Authorization", $"Bearer {cancelledToken}");
        Assert.Equal(HttpStatusCode.NoContent, (await cancelling.SendAsync(cancel)).StatusCode);

        var requests = await ListAsync(admin);

        Assert.Equal(new[] { "PC-1", "PC-2" }, requests.EnumerateArray().Select(r => r.GetProperty("name").GetString()).ToArray());
    }

    [Fact]
    public async Task List_HidesUnrevealedRequests_UntilTheyAreRevealed()
    {
        using var app = new TestApp();
        using var pc = app.CreatePcClient(AddressFor(21));
        var nonce = TestApp.NewNonce();
        var created = await pc.PostAsync("/api/client/register-requests", TestApp.RegistrationRequestBody(TestApp.CommitmentOf(nonce)));
        var token = (await JsonOf(created)).GetProperty("token").GetString()!;
        using var admin = await app.SignedInAdminAsync();

        Assert.Empty((await ListAsync(admin)).EnumerateArray());

        Assert.Equal(HttpStatusCode.NoContent, (await TestApp.RevealAsync(pc, token, nonce)).StatusCode);
        var listed = (await ListAsync(admin)).EnumerateArray().ToList();
        Assert.Equal("PC-1", Assert.Single(listed).GetProperty("name").GetString());
    }

    [Fact]
    public async Task List_ReturnsTheFieldsTheConsoleNeeds_AndNothingAboutTheTokenOrNonces()
    {
        using var app = new TestApp();
        var nonce = TestApp.NewNonce();
        using var pc = app.CreatePcClient(AddressFor(7));
        var created = await pc.PostAsync("/api/client/register-requests", TestApp.RegistrationRequestBody(TestApp.CommitmentOf(nonce)));
        var token = (await JsonOf(created)).GetProperty("token").GetString()!;
        Assert.Equal(HttpStatusCode.NoContent, (await TestApp.RevealAsync(pc, token, nonce)).StatusCode);
        using var admin = await app.SignedInAdminAsync();
        var stored = await StoredAsync(app, AddressFor(7));

        using var response = await admin.GetAsync(RequestsPath);
        var text = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString());
        using var json = JsonDocument.Parse(text);
        var root = json.RootElement;
        Assert.Equal(new[] { "requests" }, root.EnumerateObject().Select(p => p.Name).ToArray());
        var request = Assert.Single(root.GetProperty("requests").EnumerateArray());
        Assert.Equal(
            new[] { "id", "ipAddress", "name", "pairingNumber", "requestedAt" },
            request.EnumerateObject().Select(p => p.Name).Order().ToArray());
        Assert.Equal(stored.Id, request.GetProperty("id").GetInt32());
        Assert.Equal("PC-1", request.GetProperty("name").GetString());
        Assert.Equal(AddressFor(7), request.GetProperty("ipAddress").GetString());
        Assert.Equal(
            PairingNumber.Compute(app.Channel.Certificate, Convert.FromHexString(nonce), Convert.FromHexString(stored.ServerNonce)),
            request.GetProperty("pairingNumber").GetString());
        Assert.Equal(stored.RequestedAtUtc, request.GetProperty("requestedAt").GetDateTimeOffset());
        Assert.DoesNotContain(stored.TokenHash, text);
        Assert.DoesNotContain(nonce, text);
        Assert.DoesNotContain(stored.ServerNonce, text);
        Assert.DoesNotContain(stored.Commitment, text);
    }

    [Fact]
    public async Task List_WithoutASignIn_Returns401()
    {
        using var app = new TestApp();
        using var browser = app.CreateBrowser();

        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.GetAsync(RequestsPath)).StatusCode);
    }

    [Fact]
    public async Task BackgroundPolling_DoesNotKeepIdleSessionAlive()
    {
        using var app = new TestApp();
        using var admin = await app.SignedInAdminAsync();

        app.Time.Advance(TimeSpan.FromMinutes(25));
        using var poll = new HttpRequestMessage(HttpMethod.Get, RequestsPath);
        poll.Headers.Add(AuthServiceExtensions.BackgroundRefreshHeader, "1");
        Assert.Equal(HttpStatusCode.OK, (await admin.SendAsync(poll)).StatusCode);
        app.Time.Advance(TimeSpan.FromMinutes(6));

        Assert.Equal(HttpStatusCode.Unauthorized, (await admin.GetAsync(RequestsPath)).StatusCode);
    }

    [Fact]
    public async Task OrdinaryRequests_KeepTheIdleSessionAlive()
    {
        using var app = new TestApp();
        using var admin = await app.SignedInAdminAsync();

        app.Time.Advance(TimeSpan.FromMinutes(25));
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync(RequestsPath)).StatusCode);
        app.Time.Advance(TimeSpan.FromMinutes(25));

        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync(RequestsPath)).StatusCode);
    }

    // Accept

    [Fact]
    public async Task Accept_CreatesThePcFromTheRequest_AndThePcCanHeartbeatWithItsToken()
    {
        using var app = new TestApp();
        var token = await RequestAsync(app, "192.168.1.66");
        using var admin = await app.SignedInAdminAsync();
        var id = await IdOfAsync(app, "192.168.1.66");

        var response = await AcceptAsync(admin, id);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var pc = await app.WithDbAsync(db => db.Pcs.AsNoTracking().SingleAsync());
        Assert.Equal("PC-1", pc.Name);
        Assert.Equal(HashOf(token), pc.TokenHash);
        Assert.Equal("192.168.1.66", pc.IpAddress);
        Assert.Equal(app.Time.GetUtcNow().ToUnixTimeMilliseconds(), pc.RegisteredAtUtc.ToUnixTimeMilliseconds());
        Assert.Equal(pc.RegisteredAtUtc, pc.LastSeenUtc);
        Assert.Equal(RegistrationRequestStatus.Approved, (await StoredAsync(app, "192.168.1.66")).Status);

        using var pcClient = app.CreatePcClient("192.168.1.66");
        using var heartbeat = new HttpRequestMessage(HttpMethod.Post, "/api/client/heartbeat");
        heartbeat.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
        Assert.Equal(HttpStatusCode.OK, (await pcClient.SendAsync(heartbeat)).StatusCode);
    }

    [Fact]
    public async Task Accept_WhenTheNameWasTakenMeanwhile_CreatesThePcWithTheNextFreeName_AndUpdatesTheRequest()
    {
        using var app = new TestApp();
        var token = await RequestAsync(app, AddressFor(8));
        Assert.Equal("PC-1", (await StoredAsync(app, AddressFor(8))).Name);
        await app.WithDbAsync(async db =>
        {
            db.Pcs.Add(new Pc
            {
                Name = "pc-1",
                TokenHash = "already-taken",
                RegisteredAtUtc = app.Time.GetUtcNow(),
                LastSeenUtc = app.Time.GetUtcNow(),
            });
            await db.SaveChangesAsync();
            return 0;
        });
        using var admin = await app.SignedInAdminAsync();

        var response = await AcceptAsync(admin, await IdOfAsync(app, AddressFor(8)));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var created = await app.WithDbAsync(db => db.Pcs.AsNoTracking().SingleAsync(p => p.TokenHash == HashOf(token)));
        Assert.Equal("PC-2", created.Name);
        var stored = await StoredAsync(app, AddressFor(8));
        Assert.Equal("PC-2", stored.Name);
        Assert.Equal(RegistrationRequestStatus.Approved, stored.Status);
        Assert.Equal(2, await app.WithDbAsync(db => db.Pcs.CountAsync()));
        Assert.Equal("approved", await StatusOfAsync(app, AddressFor(8), token));
    }

    [Fact]
    public async Task Accept_OfTwoPendingRequests_CreatesTwoPcsWithDistinctNames()
    {
        using var app = new TestApp();
        await RequestAsync(app, AddressFor(15));
        await RequestAsync(app, AddressFor(16));
        using var admin = await app.SignedInAdminAsync();
        var requests = (await ListAsync(admin)).EnumerateArray().ToList();
        Assert.Equal(new[] { "PC-1", "PC-2" }, requests.Select(r => r.GetProperty("name").GetString()).ToArray());

        foreach (var request in requests)
        {
            Assert.Equal(HttpStatusCode.NoContent, (await AcceptAsync(admin, request.GetProperty("id").GetInt32())).StatusCode);
        }

        var names = await app.WithDbAsync(db => db.Pcs.OrderBy(p => p.Id).Select(p => p.Name).ToListAsync());
        Assert.Equal(new[] { "PC-1", "PC-2" }, names);
        Assert.Empty((await ListAsync(admin)).EnumerateArray());
    }

    [Fact]
    public async Task Accept_OfAnUnrevealedRequest_Returns404_AndCreatesNoPc()
    {
        using var app = new TestApp();
        await UnrevealedRequestAsync(app, AddressFor(22));
        using var admin = await app.SignedInAdminAsync();
        var id = await IdOfAsync(app, AddressFor(22));

        await AssertErrorAsync(await AcceptAsync(admin, id), HttpStatusCode.NotFound);
        Assert.Equal(RegistrationRequestStatus.Pending, (await StoredAsync(app, AddressFor(22))).Status);
        Assert.Equal(0, await app.WithDbAsync(db => db.Pcs.CountAsync()));
    }

    [Fact]
    public async Task Deny_OfAnUnrevealedRequest_Returns404_AndLeavesItPending()
    {
        using var app = new TestApp();
        await UnrevealedRequestAsync(app, AddressFor(23));
        using var admin = await app.SignedInAdminAsync();
        var id = await IdOfAsync(app, AddressFor(23));

        await AssertErrorAsync(await DenyAsync(admin, id), HttpStatusCode.NotFound);
        Assert.Equal(RegistrationRequestStatus.Pending, (await StoredAsync(app, AddressFor(23))).Status);
    }

    [Fact]
    public async Task Accept_UnknownId_Returns404WithError()
    {
        using var app = new TestApp();
        using var admin = await app.SignedInAdminAsync();

        var response = await AcceptAsync(admin, 4242);

        await AssertErrorAsync(response, HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Accept_AnAlreadyAnsweredRequest_Returns404_AndCreatesOnlyOnePc()
    {
        using var app = new TestApp();
        await RequestAsync(app, AddressFor(9));
        using var admin = await app.SignedInAdminAsync();
        var id = await IdOfAsync(app, AddressFor(9));
        Assert.Equal(HttpStatusCode.NoContent, (await AcceptAsync(admin, id)).StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await AcceptAsync(admin, id)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await DenyAsync(admin, id)).StatusCode);
        Assert.Equal(1, await app.WithDbAsync(db => db.Pcs.CountAsync()));
    }

    [Fact]
    public async Task Accept_AnExpiredRequest_Returns404_AndCreatesNoPc()
    {
        using var app = new TestApp();
        await RequestAsync(app, AddressFor(10));
        app.Time.Advance(TimeSpan.FromMinutes(11));
        using var admin = await app.SignedInAdminAsync();

        var response = await AcceptAsync(admin, await IdOfAsync(app, AddressFor(10)));

        await AssertErrorAsync(response, HttpStatusCode.NotFound);
        Assert.Equal(0, await app.WithDbAsync(db => db.Pcs.CountAsync()));
    }

    [Fact]
    public async Task Accept_WhenSeveralAdminsAcceptAtOnce_OnlyOneWins()
    {
        using var app = new TestApp();
        await RequestAsync(app, AddressFor(11));
        using var admin = await app.SignedInAdminAsync();
        var id = await IdOfAsync(app, AddressFor(11));

        var responses = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => AcceptAsync(admin, id)));

        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.NoContent));
        Assert.Equal(4, responses.Count(r => r.StatusCode == HttpStatusCode.NotFound));
        Assert.Equal(1, await app.WithDbAsync(db => db.Pcs.CountAsync()));
    }

    // Deny

    [Fact]
    public async Task Deny_Returns204_AndCreatesNoPc()
    {
        using var app = new TestApp();
        await RequestAsync(app, AddressFor(12));
        using var admin = await app.SignedInAdminAsync();

        var response = await DenyAsync(admin, await IdOfAsync(app, AddressFor(12)));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(RegistrationRequestStatus.Denied, (await StoredAsync(app, AddressFor(12))).Status);
        Assert.Equal(0, await app.WithDbAsync(db => db.Pcs.CountAsync()));
    }

    [Fact]
    public async Task Deny_AnExpiredRequest_Returns404()
    {
        using var app = new TestApp();
        await RequestAsync(app, AddressFor(13));
        app.Time.Advance(TimeSpan.FromMinutes(11));
        using var admin = await app.SignedInAdminAsync();

        var response = await DenyAsync(admin, await IdOfAsync(app, AddressFor(13)));

        await AssertErrorAsync(response, HttpStatusCode.NotFound);
        Assert.Equal(RegistrationRequestStatus.Pending, (await StoredAsync(app, AddressFor(13))).Status);
    }

    [Fact]
    public async Task Deny_UnknownId_Returns404()
    {
        using var app = new TestApp();
        using var admin = await app.SignedInAdminAsync();

        await AssertErrorAsync(await DenyAsync(admin, 4242), HttpStatusCode.NotFound);
    }

    // Body and access

    [Theory]
    [InlineData("{not json", "application/json")]
    [InlineData("null", "application/json")]
    [InlineData("{}", "text/plain")]
    public async Task AcceptAndDeny_WithoutAJsonBody_Returns400AndChangeNothing(string body, string contentType)
    {
        using var app = new TestApp();
        await RequestAsync(app, AddressFor(14));
        using var admin = await app.SignedInAdminAsync();
        var id = await IdOfAsync(app, AddressFor(14));

        var accept = await admin.PostAsync($"{RequestsPath}/{id}/accept", new StringContent(body, Encoding.UTF8, contentType));
        var deny = await admin.PostAsync($"{RequestsPath}/{id}/deny", new StringContent(body, Encoding.UTF8, contentType));

        await AssertErrorAsync(accept, HttpStatusCode.BadRequest, "Invalid request.");
        await AssertErrorAsync(deny, HttpStatusCode.BadRequest, "Invalid request.");
        Assert.Equal(RegistrationRequestStatus.Pending, (await StoredAsync(app, AddressFor(14))).Status);
    }

    [Theory]
    [InlineData("POST", "/api/registration-requests/1/accept")]
    [InlineData("POST", "/api/registration-requests/1/deny")]
    [InlineData("GET", "/api/registration-requests")]
    public async Task Routes_WithoutASignIn_Return401(string method, string path)
    {
        using var app = new TestApp();
        using var browser = app.CreateBrowser();
        using var request = new HttpRequestMessage(new HttpMethod(method), path) { Content = JsonContent.Create(new { }) };

        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.SendAsync(request)).StatusCode);
    }

    [Theory]
    [InlineData("GET", "/api/registration-requests")]
    [InlineData("POST", "/api/registration-requests/1/accept")]
    [InlineData("POST", "/api/registration-requests/1/deny")]
    public async Task Routes_AreNotFoundOnThePcPort_EvenWithASignIn(string method, string path)
    {
        using var app = new TestApp();
        using var admin = await app.SignedInAdminAsync();
        using var request = new HttpRequestMessage(new HttpMethod(method), path) { Content = JsonContent.Create(new { }) };
        request.Headers.Add(SimulatedConnectionFilter.LocalPortHeader, app.Services.GetRequiredService<ServerPorts>().ClientPort.ToString());

        var response = await admin.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("", await response.Content.ReadAsStringAsync());
    }
}
