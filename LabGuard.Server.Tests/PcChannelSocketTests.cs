using System.Net;
using System.Net.Http.Json;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using LabGuard.Server.Network;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace LabGuard.Server.Tests;

// Starts the real host on real sockets (Kestrel) on free ports, with a temporary database and channel file.
public sealed class PcChannelSocketTests : IAsyncLifetime
{
    private readonly string _folder = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"lykantra-socket-{Guid.NewGuid():N}")).FullName;
    private SocketFactory? _factory;
    private int _adminPort;
    private int _clientPort;

    private PcChannelStore Channel => _factory!.Services.GetRequiredService<PcChannelStore>();

    public Task InitializeAsync()
    {
        _adminPort = FreePort();
        _clientPort = FreePort();
        _factory = new SocketFactory(_folder, _adminPort, _clientPort);
        _factory.UseKestrel();
        _factory.StartServer();
        Assert.Equal(new ServerPorts(_adminPort, _clientPort), _factory.Services.GetRequiredService<ServerPorts>());
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
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static string KeyHash(X509Certificate2 certificate) =>
        Convert.ToHexString(SHA256.HashData(certificate.PublicKey.ExportSubjectPublicKeyInfo()));

    private static HttpClient PinnedClient(Action<X509Certificate2> capture)
    {
        var handler = new SocketsHttpHandler
        {
            SslOptions = new SslClientAuthenticationOptions
            {
                // Self-signed: the client trusts nothing but captures what the server presents.
                RemoteCertificateValidationCallback = (_, certificate, _, _) =>
                {
                    capture(new X509Certificate2(certificate!));
                    return true;
                },
            },
        };
        return new HttpClient(handler);
    }

    [Fact]
    public async Task PcPort_ServesHttpsWithTheStoredCertificate()
    {
        X509Certificate2? presented = null;
        using var client = PinnedClient(certificate => presented = certificate);

        var response = await client.GetAsync($"https://127.0.0.1:{_clientPort}/api/client/ping");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(presented);
        Assert.Equal(KeyHash(Channel.Certificate), KeyHash(presented));
        Assert.Equal(Channel.Certificate.Thumbprint, presented.Thumbprint);
    }

    [Fact]
    public async Task PcPort_TakesRegistrationRequestsOverHttps()
    {
        using var client = PinnedClient(_ => { });
        var uri = $"https://127.0.0.1:{_clientPort}/api/client/register-requests";

        var invalid = await client.PostAsJsonAsync(uri, new { name = "bad name!", nonce = TestApp.NewNonce() });
        var created = await client.PostAsJsonAsync(uri, new { name = "PC-01", nonce = TestApp.NewNonce() });

        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, created.StatusCode);
        Assert.Matches("^[A-Za-z0-9_-]{43}$", (await created.Content.ReadFromJsonAsync<TokenBody>())!.Token);
    }

    private sealed record TokenBody(string Token);

    [Theory]
    [InlineData(SslProtocols.Tls12)]
    [InlineData(SslProtocols.Tls13)]
    public async Task PcPort_NegotiatesTls12AndTls13(SslProtocols protocol)
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, _clientPort);
        await using var ssl = new SslStream(tcp.GetStream(), false, (_, _, _, _) => true);

        await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = "localhost", EnabledSslProtocols = protocol });

        Assert.Equal(protocol, ssl.SslProtocol);
    }

#pragma warning disable SYSLIB0039 // Deliberately offering the obsolete protocols to prove they are refused.
    [Theory]
    [InlineData(SslProtocols.Tls11)]
    [InlineData(SslProtocols.Tls)]
#pragma warning restore SYSLIB0039
    public async Task PcPort_RefusesOldTlsVersions(SslProtocols protocol)
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, _clientPort);
        await using var ssl = new SslStream(tcp.GetStream(), false, (_, _, _, _) => true);

        // Fails whether the client OS refuses to offer the protocol or the server refuses to accept it.
        await Assert.ThrowsAnyAsync<Exception>(() =>
            ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = "localhost", EnabledSslProtocols = protocol }));
    }

    [Fact]
    public async Task PcPort_PlainHttpFails()
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

        await Assert.ThrowsAnyAsync<HttpRequestException>(() =>
            client.GetAsync($"http://127.0.0.1:{_clientPort}/api/client/ping"));
    }

    [Fact]
    public async Task AdminPort_StillServesPlainHttpOnLoopback_AndKeepsTheCookieRule()
    {
        using var client = new HttpClient();

        var health = await client.GetAsync($"http://127.0.0.1:{_adminPort}/api/health");
        var pcs = await client.GetAsync($"http://127.0.0.1:{_adminPort}/api/pcs");

        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, pcs.StatusCode);
    }

    [Fact]
    public async Task PcPort_DoesNotServeAdminEndpoints()
    {
        using var client = PinnedClient(_ => { });

        var response = await client.GetAsync($"https://127.0.0.1:{_clientPort}/api/pcs");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private sealed class SocketFactory(string folder, int adminPort, int clientPort) : WebApplicationFactory<Program>
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
                    ["ConnectionStrings:AppDb"] = $"Data Source={Path.Combine(folder, "socket.db")}",
                    ["Seed:AdminUsername"] = TestApp.Username,
                    ["Seed:AdminPassword"] = TestApp.Password,
                    ["Seed:SampleLoginRequests"] = "false",
                }));
        }
    }
}
