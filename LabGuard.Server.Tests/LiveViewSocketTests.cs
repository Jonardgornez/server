using System.Net;
using System.Net.Http.Json;
using System.Net.Security;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LabGuard.Server.Data;
using LabGuard.Server.Network;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace LabGuard.Server.Tests;

// The live view over real sockets: Kestrel, TLS on the PC port and actual WebSocket framing.
// TestServer has no sockets, so these tests are the ones that prove the body limit, aborts and origin refusals on the wire.
public sealed class LiveViewSocketTests : IAsyncLifetime
{
    private readonly string _folder = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"lykantra-live-{Guid.NewGuid():N}")).FullName;
    private LiveViewSocketFactory? _factory;
    private int _adminPort;
    private int _clientPort;

    public Task InitializeAsync()
    {
        _adminPort = FreePort();
        _clientPort = FreePort();
        _factory = new LiveViewSocketFactory(_folder, _adminPort, _clientPort);
        _factory.UseKestrel();
        _factory.StartServer();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_folder, recursive: true);
    }

    private static int FreePort()
    {
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static ClientWebSocket NewPcSocket(string token)
    {
        var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", $"Bearer {token}");
        socket.Options.RemoteCertificateValidationCallback = static (_, _, _, _) => true;
        return socket;
    }

    private static Uri PcStreamUri(int port, string viewId) => new($"wss://127.0.0.1:{port}/api/client/screen/stream?viewId={viewId}");

    private async Task<(int Id, string Token)> AddPcAsync()
    {
        var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = DateTimeOffset.UtcNow;
        var pc = new Pc
        {
            Name = "PC-01",
            TokenHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token))),
            RegisteredAtUtc = now,
            LastSeenUtc = now,
        };
        db.Pcs.Add(pc);
        await db.SaveChangesAsync();
        return (pc.Id, token);
    }

    private async Task<string> SignInCookieAsync()
    {
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_adminPort}") };
        var response = await client.PostAsJsonAsync("/api/auth/login", new { username = TestApp.Username, password = TestApp.Password });
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        return response.Headers.GetValues("Set-Cookie").Single().Split(';')[0];
    }

    private static ClientWebSocket NewAdminSocket(string cookie, string origin)
    {
        var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Origin", origin);
        socket.Options.SetRequestHeader("Cookie", cookie);
        return socket;
    }

    private async Task<ClientWebSocket> OpenAdminViewAsync(int pcId, string cookie)
    {
        var socket = NewAdminSocket(cookie, LiveViewClient.LocalOrigin);
        await socket.ConnectAsync(new Uri($"ws://localhost:{_adminPort}/api/pcs/{pcId}/screen/live"), CancellationToken.None);
        Assert.Equal("{\"state\":\"waiting\"}", await LiveViewClient.ReceiveStateAsync(socket));
        return socket;
    }

    // The PC learns the view id from its heartbeat, over its own pinned TLS connection.
    private async Task<string> ViewIdAsync(string token)
    {
        using var handler = new SocketsHttpHandler
        {
            SslOptions = new SslClientAuthenticationOptions { RemoteCertificateValidationCallback = static (_, _, _, _) => true },
        };
        using var pc = new HttpClient(handler) { BaseAddress = new Uri($"https://127.0.0.1:{_clientPort}") };
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/client/heartbeat");
        request.Headers.Add("Authorization", $"Bearer {token}");
        using var response = await pc.SendAsync(request);
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("screenView").GetProperty("viewId").GetString()!;
    }

    [Fact]
    public async Task AFrameLargerThanTheClientBodyLimit_CrossesTheRealSocketIntact()
    {
        var (id, token) = await AddPcAsync();
        var cookie = await SignInCookieAsync();
        using var browser = await OpenAdminViewAsync(id, cookie);
        var viewId = await ViewIdAsync(token);
        using var pc = NewPcSocket(token);
        await pc.ConnectAsync(PcStreamUri(_clientPort, viewId), CancellationToken.None);
        Assert.Equal("{\"state\":\"live\"}", await LiveViewClient.ReceiveStateAsync(browser));
        var frame = LiveViewClient.Frame("large", size: 1_000_000);

        await LiveViewClient.SendAsync(pc, frame);

        var received = await LiveViewClient.ReceiveAsync(browser);
        Assert.Equal(WebSocketMessageType.Binary, received.Type);
        Assert.Equal(frame, received.Payload);
    }

    [Fact]
    public async Task APcThatAborts_ReturnsTheViewToWaiting_AndAReconnectResumesTheStream()
    {
        var (id, token) = await AddPcAsync();
        var cookie = await SignInCookieAsync();
        using var browser = await OpenAdminViewAsync(id, cookie);
        var viewId = await ViewIdAsync(token);
        var first = NewPcSocket(token);
        await first.ConnectAsync(PcStreamUri(_clientPort, viewId), CancellationToken.None);
        Assert.Equal("{\"state\":\"live\"}", await LiveViewClient.ReceiveStateAsync(browser));

        first.Abort();

        Assert.Equal("{\"state\":\"waiting\"}", await LiveViewClient.ReceiveStateAsync(browser));
        // Kestrel runs on the system clock, so the reconnect waits out the one-second attach cooldown.
        await Task.Delay(TimeSpan.FromSeconds(1.1));
        using var second = NewPcSocket(token);
        await second.ConnectAsync(PcStreamUri(_clientPort, viewId), CancellationToken.None);
        Assert.Equal("{\"state\":\"live\"}", await LiveViewClient.ReceiveStateAsync(browser));
        await LiveViewClient.SendAsync(second, LiveViewClient.Frame("after-reconnect"));
        var received = await LiveViewClient.ReceiveAsync(browser);
        Assert.Equal("after-reconnect", LiveViewClient.LabelOf(received.Payload, 15));
    }

    [Fact]
    public async Task AForeignOriginOnTheRealAdminPort_IsRefusedWith403()
    {
        var (id, _) = await AddPcAsync();
        var cookie = await SignInCookieAsync();
        using var socket = NewAdminSocket(cookie, "https://evil.example");

        var exception = await Assert.ThrowsAnyAsync<WebSocketException>(() =>
            socket.ConnectAsync(new Uri($"ws://localhost:{_adminPort}/api/pcs/{id}/screen/live"), CancellationToken.None));

        Assert.Contains("403", exception.Message);
    }

    private sealed class LiveViewSocketFactory(string folder, int adminPort, int clientPort) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseContentRoot(folder);
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Server:Port"] = adminPort.ToString(),
                    ["Server:ClientPort"] = clientPort.ToString(),
                    [PcChannelStore.FileConfigurationKey] = Path.Combine(folder, "pc-channel.dat"),
                    ["ConnectionStrings:AppDb"] = $"Data Source={Path.Combine(folder, "live.db")}",
                    ["Seed:AdminUsername"] = TestApp.Username,
                    ["Seed:AdminPassword"] = TestApp.Password,
                    ["Seed:SampleLoginRequests"] = "false",
                    ["Screen:AllowedOrigins:0"] = "http://localhost:5173",
                }));
        }
    }
}
