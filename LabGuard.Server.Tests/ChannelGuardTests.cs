using System.Net;
using System.Net.Http.Json;
using System.Text;
using LabGuard.Server.Network;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace LabGuard.Server.Tests;

public class ChannelGuardTests
{
    private static HttpClient AdminBrowser(TestApp app) =>
        app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });

    private static async Task SignInAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/api/auth/login", new { username = TestApp.Username, password = TestApp.Password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static HttpRequestMessage WithHost(HttpMethod method, string path, string host)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Host = host;
        return request;
    }

    // PC port

    [Theory]
    [InlineData("GET", "/api/auth/me")]
    [InlineData("POST", "/api/auth/login")]
    [InlineData("POST", "/api/auth/logout")]
    [InlineData("GET", "/api/pcs")]
    [InlineData("DELETE", "/api/pcs/1")]
    [InlineData("GET", "/api/pcs/1/screen/live")]
    [InlineData("GET", "/api/accounts")]
    [InlineData("GET", "/api/login-requests")]
    [InlineData("GET", "/api/health")]
    [InlineData("GET", "/openapi/v1.json")]
    [InlineData("GET", "/api/client")]
    [InlineData("GET", "/")]
    public async Task PcPort_ReturnsEmpty404ForEverythingOutsideApiClient_EvenWithAdminCookie(string method, string path)
    {
        using var app = new TestApp();
        using var client = AdminBrowser(app);
        await SignInAsync(client);
        var port = app.Services.GetRequiredService<ServerPorts>().ClientPort.ToString();
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        request.Headers.Add(SimulatedConnectionFilter.LocalPortHeader, port);
        request.Content = new StringContent("{}", Encoding.UTF8, "application/json");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task PcPort_AcceptsApiClientPathsWithALanHostHeader()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();
        using var request = WithHost(HttpMethod.Get, "/api/client/ping", "192.168.1.5:5001");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task PcPort_RefusesAConnectionWithoutTls_EvenOnAClientPath()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/client/ping");
        request.Headers.Add(SimulatedConnectionFilter.PlainHttpHeader, "1");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task PcPort_ApiClientPathMatchIgnoresCase()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/API/Client/ping")).StatusCode);
    }

    [Fact]
    public async Task PcPort_DoesNotGoByTheHostHeader()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();
        using var request = WithHost(HttpMethod.Get, "/api/health", "localhost");

        Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task PcPort_WithOversizedContentLength_Returns413BeforeReadingTheBody()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();
        var body = new string('x', ChannelGuard.MaxClientBodyBytes + 1);

        var response = await client.PostAsync("/api/client/heartbeat", new StringContent(body, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task PcPort_BodyAtTheLimit_IsAccepted()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();
        var json = $$"""{"name":"PC-01","commitment":"{{TestApp.NewCommitment()}}"}""";
        var padding = new string(' ', ChannelGuard.MaxClientBodyBytes - json.Length);

        var response = await client.PostAsync("/api/client/register-requests", new StringContent(json + padding, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    // Admin port

    [Theory]
    [InlineData("GET", "/api/client/ping")]
    [InlineData("POST", "/api/client/register-requests")]
    [InlineData("POST", "/api/client/heartbeat")]
    [InlineData("POST", "/api/client/offline")]
    [InlineData("GET", "/api/client/screen/stream")]
    [InlineData("GET", "/API/CLIENT/ping")]
    public async Task AdminPort_ReturnsEmpty404ForApiClientPaths(string method, string path)
    {
        using var app = new TestApp();
        using var client = AdminBrowser(app);
        await SignInAsync(client);
        using var request = new HttpRequestMessage(new HttpMethod(method), path)
        {
            Content = new StringContent("""{"name":"PC-01"}""", Encoding.UTF8, "application/json"),
        };

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("", await response.Content.ReadAsStringAsync());
        Assert.Equal(0, await app.WithDbAsync(db => db.Pcs.CountAsync()));
    }

    [Theory]
    [InlineData("localhost")]
    [InlineData("LOCALHOST:5000")]
    [InlineData("127.0.0.1:5000")]
    [InlineData("[::1]:5000")]
    public async Task AdminPort_AcceptsLoopbackHosts(string host)
    {
        using var app = new TestApp();
        using var client = app.CreateBrowser();
        using var request = WithHost(HttpMethod.Get, "/api/health", host);

        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(request)).StatusCode);
    }

    [Theory]
    [InlineData("192.168.1.5:5000")]
    [InlineData("192.168.1.5")]
    [InlineData("evil.example")]
    [InlineData("localhost.evil.example")]
    [InlineData("0.0.0.0:5000")]
    public async Task AdminPort_RejectsOtherHostsWith400(string host)
    {
        using var app = new TestApp();
        using var client = app.CreateBrowser();
        using var request = WithHost(HttpMethod.Get, "/api/health", host);

        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task AdminPort_RejectsLanHostEvenForTheLoginEndpoint()
    {
        using var app = new TestApp();
        using var client = app.CreateBrowser();
        using var request = WithHost(HttpMethod.Post, "/api/auth/login", "192.168.1.5:5000");
        request.Content = JsonContent.Create(new { username = TestApp.Username, password = TestApp.Password });

        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(request)).StatusCode);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("127.5.5.5")]
    [InlineData("::1")]
    [InlineData("::ffff:127.0.0.1")]
    public async Task AdminPort_AcceptsEveryLoopbackForm(string remote)
    {
        using var app = new TestApp();
        using var client = app.CreateBrowser();
        using var request = WithHost(HttpMethod.Get, "/api/health", "localhost");
        request.Headers.Add(SimulatedConnectionFilter.RemoteIpHeader, remote);
        request.Headers.Add(SimulatedConnectionFilter.LocalIpHeader, remote);

        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(request)).StatusCode);
    }

    // Fails closed: anything that is not the PC channel or a loopback connection to the admin port is a bare 404.

    private static HttpRequestMessage Spoofed(HttpMethod method, string path, ServerPorts ports, int? localPort = null,
        string? remote = null, string? local = null)
    {
        var request = WithHost(method, path, "localhost");
        request.Headers.Add(SimulatedConnectionFilter.LocalPortHeader, (localPort ?? ports.AdminPort).ToString());
        if (remote is not null)
        {
            request.Headers.Add(SimulatedConnectionFilter.RemoteIpHeader, remote);
        }

        if (local is not null)
        {
            request.Headers.Add(SimulatedConnectionFilter.LocalIpHeader, local);
        }

        if (method == HttpMethod.Post)
        {
            request.Content = JsonContent.Create(new { username = TestApp.Username, password = TestApp.Password });
        }

        return request;
    }

    public static TheoryData<string, string> AdminRoutes => new()
    {
        { "POST", "/api/auth/login" },
        { "GET", "/api/pcs" },
        { "GET", "/api/pcs/1/screen/live" },
        { "GET", "/api/client/screen/stream" },
        { "GET", "/api/health" },
        { "GET", "/openapi/v1.json" },
    };

    private static async Task AssertBare404Async(TestApp app, string method, string path, int? localPort = null,
        string? remote = null, string? local = null)
    {
        using var client = app.CreateBrowser();
        using var request = Spoofed(new HttpMethod(method), path, app.Services.GetRequiredService<ServerPorts>(), localPort, remote, local);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [MemberData(nameof(AdminRoutes))]
    public async Task AdminPort_FromANonLoopbackRemote_Returns404_EvenWithLocalhostHost(string method, string path)
    {
        using var app = new TestApp();
        await AssertBare404Async(app, method, path, remote: "192.168.1.77");
    }

    [Theory]
    [MemberData(nameof(AdminRoutes))]
    public async Task AdminPort_OnANonLoopbackLocalAddress_Returns404(string method, string path)
    {
        using var app = new TestApp();
        await AssertBare404Async(app, method, path, remote: "127.0.0.1", local: "192.168.1.5");
        await AssertBare404Async(app, method, path, remote: "127.0.0.1", local: "0.0.0.0");
    }

    [Theory]
    [MemberData(nameof(AdminRoutes))]
    public async Task AdminPort_WithANullRemoteOrLocalAddress_Returns404(string method, string path)
    {
        using var app = new TestApp();
        await AssertBare404Async(app, method, path, remote: SimulatedConnectionFilter.NoAddress);
        await AssertBare404Async(app, method, path, local: SimulatedConnectionFilter.NoAddress);
    }

    [Theory]
    [MemberData(nameof(AdminRoutes))]
    public async Task UnknownPort_Returns404_EvenFromLoopbackWithLocalhostHost(string method, string path)
    {
        using var app = new TestApp();
        var ports = app.Services.GetRequiredService<ServerPorts>();
        await AssertBare404Async(app, method, path, localPort: 0);
        await AssertBare404Async(app, method, path, localPort: ports.AdminPort + 1000);
    }

    [Fact]
    public async Task UnknownPort_DoesNotLetAnonymousLoginThrough()
    {
        using var app = new TestApp();
        await AssertBare404Async(app, "POST", "/api/auth/login", localPort: 54321, remote: "192.168.1.77");
        Assert.Equal(0, await app.WithDbAsync(db => db.Pcs.CountAsync()));
    }

    [Fact]
    public async Task AdminPort_DoesNotServeThePcScreenStream_EvenAsAWebSocket()
    {
        using var app = new TestApp();
        var cookie = await LiveViewClient.SignInCookieAsync(app);
        var (_, token) = await LiveViewClient.AddPcAsync(app);

        var status = await LiveViewClient.RefusedStatusAsync(() =>
        {
            var socket = app.Server.CreateWebSocketClient();
            socket.ConfigureRequest = request =>
            {
                request.Headers["Authorization"] = $"Bearer {token}";
                request.Headers["Cookie"] = cookie;
            };
            return socket.ConnectAsync(new Uri("ws://localhost/api/client/screen/stream?viewId=x"), CancellationToken.None);
        });

        Assert.Equal(404, status);
    }

    [Fact]
    public async Task PcPort_FromTheSameComputerOverLoopback_StillWorks()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient("127.0.0.1");
        using var ipv6 = app.CreatePcClient("::1");

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/client/ping")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await ipv6.GetAsync("/api/client/ping")).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsync("/api/client/register-requests", TestApp.RegistrationRequestBody("PC-01"))).StatusCode);
    }

    [Fact]
    public async Task PcPort_ErrorInsideAnEndpoint_ReturnsEmpty500_AndLogsNoHeadersOrToken()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();
        const string token = "super-secret-token-value";
        app.Time.Fail = true;
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/client/heartbeat");
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("", await response.Content.ReadAsStringAsync());
        var logs = string.Join("\n", app.Logs.Entries);
        Assert.Contains("Simulated clock failure", logs);
        Assert.DoesNotContain(token, logs);
        Assert.DoesNotContain("Bearer", logs, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Authorization:", logs, StringComparison.OrdinalIgnoreCase);
    }
}
