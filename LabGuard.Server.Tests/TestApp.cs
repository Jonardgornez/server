using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using LabGuard.Server.Data;
using LabGuard.Server.Network;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Time.Testing;

namespace LabGuard.Server.Tests;

/// <summary>
/// Test host with its own temporary SQLite file, a fake clock and known seed credentials.
/// </summary>
public sealed class TestApp : WebApplicationFactory<Program>
{
    public const string Username = "tester";
    public const string Password = "correct horse";

    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"lykantra-test-{Guid.NewGuid():N}.db");
    private readonly string _channelPath = Path.Combine(Path.GetTempPath(), $"lykantra-test-{Guid.NewGuid():N}.pc-channel.dat");
    private readonly string _environment;

    private readonly bool _seedSampleLoginRequests;

    private readonly Action<IServiceCollection>? _configureServices;

    public TestApp(string environment = "Development", bool seedSampleLoginRequests = false, Action<IServiceCollection>? configureServices = null)
    {
        _environment = environment;
        _seedSampleLoginRequests = seedSampleLoginRequests;
        _configureServices = configureServices;
    }

    public FailableTimeProvider Time { get; } = new(DateTimeOffset.UtcNow);

    public CapturingLoggerProvider Logs { get; } = new();

    public PcChannelStore Channel => Services.GetRequiredService<PcChannelStore>();

    /// <summary>A nonce as a PC sends it: 32 random bytes as 64 lowercase hex characters.</summary>
    public static string NewNonce() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));

    /// <summary>The commitment a PC sends for a nonce: the SHA-256 of the nonce's 32 bytes, as 64 lowercase hex characters.</summary>
    public static string CommitmentOf(string nonce) => Convert.ToHexStringLower(SHA256.HashData(Convert.FromHexString(nonce)));

    public static string NewCommitment() => CommitmentOf(NewNonce());

    public static JsonContent RegistrationRequestBody(string? name, string? commitment = null) =>
        JsonContent.Create(new { name, commitment = commitment ?? NewCommitment() });

    public static JsonContent RevealBody(string? nonce) => JsonContent.Create(new { nonce });

    /// <summary>Sends the nonce a PC kept for its request, with the token it was given.</summary>
    public static Task<HttpResponseMessage> RevealAsync(HttpClient pc, string token, string? nonce)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/client/register-requests/reveal") { Content = RevealBody(nonce) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return pc.SendAsync(request);
    }

    /// <summary>Signs in as the administrator on a loopback browser client.</summary>
    public async Task<HttpClient> SignedInAdminAsync()
    {
        var client = CreateBrowser();
        var response = await client.PostAsJsonAsync("/api/auth/login", new { username = Username, password = Password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }

    /// <summary>
    /// Creates a registration request as a PC does, then reveals its nonce, so the administrator can see it. Returns the token the PC holds.
    /// </summary>
    public async Task<string> RequestAndRevealAsync(HttpClient pc, string name)
    {
        var nonce = NewNonce();
        var created = await pc.PostAsync("/api/client/register-requests", RegistrationRequestBody(name, CommitmentOf(nonce)));
        Assert.Equal(HttpStatusCode.Accepted, created.StatusCode);
        var token = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement.GetProperty("token").GetString()!;

        var revealed = await RevealAsync(pc, token, nonce);
        Assert.Equal(HttpStatusCode.NoContent, revealed.StatusCode);
        return token;
    }

    /// <summary>
    /// Sends a registration request from the PC, reveals it, has the administrator accept it, and returns the token the PC holds.
    /// </summary>
    public async Task<string> RegisterPcAsync(HttpClient pc, string name)
    {
        var token = await RequestAndRevealAsync(pc, name);

        using var admin = await SignedInAdminAsync();
        using var listed = JsonDocument.Parse(await (await admin.GetAsync("/api/registration-requests")).Content.ReadAsStringAsync());
        var id = listed.RootElement.GetProperty("requests").EnumerateArray()
            .Single(request => request.GetProperty("name").GetString() == name)
            .GetProperty("id").GetInt32();

        var accepted = await admin.PostAsJsonAsync($"/api/registration-requests/{id}/accept", new { });
        Assert.Equal(HttpStatusCode.NoContent, accepted.StatusCode);
        return token;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(_environment);
        builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:AppDb"] = $"Data Source={_databasePath}",
                [PcChannelStore.FileConfigurationKey] = _channelPath,
                ["Seed:AdminUsername"] = Username,
                ["Seed:AdminPassword"] = Password,
                ["Seed:SampleLoginRequests"] = _seedSampleLoginRequests ? "true" : "false",
            }));
        builder.ConfigureLogging(logging => logging.AddProvider(Logs));
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Time);
            services.AddSingleton<IStartupFilter, SimulatedConnectionFilter>();
            _configureServices?.Invoke(services);
        });
    }

    public HttpClient CreateBrowser() =>
        CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });

    /// <summary>A client that appears to connect on the PC port, from the given remote address.</summary>
    public HttpClient CreatePcClient(string remoteIp = "192.168.1.50")
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(SimulatedConnectionFilter.LocalPortHeader, Services.GetRequiredService<ServerPorts>().ClientPort.ToString());
        client.DefaultRequestHeaders.Add(SimulatedConnectionFilter.RemoteIpHeader, remoteIp);
        return client;
    }

    public async Task<T> WithDbAsync<T>(Func<AppDbContext, Task<T>> action)
    {
        using var scope = Services.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing)
        {
            return;
        }

        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-shm", "-wal" })
        {
            File.Delete(_databasePath + suffix);
        }

        File.Delete(_channelPath);
        File.Delete(_channelPath + ".tmp");
    }
}

/// <summary>
/// Test-only: TestServer has no sockets, so this sets the connection's local port and remote address
/// from request headers. It is registered by the test project only; the product has no such switch.
/// </summary>
internal sealed class SimulatedConnectionFilter : IStartupFilter
{
    public const string LocalPortHeader = "X-Test-Local-Port";
    public const string RemoteIpHeader = "X-Test-Remote-Ip";
    public const string LocalIpHeader = "X-Test-Local-Ip";
    public const string PlainHttpHeader = "X-Test-Plain-Http";
    /// <summary>Header value that makes an address null, as on a connection with no address information.</summary>
    public const string NoAddress = "none";

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use((context, nextMiddleware) =>
        {
            // By default an ordinary test request is a loopback connection to the admin port.
            var ports = context.RequestServices.GetRequiredService<ServerPorts>();
            var connection = context.Connection;
            connection.LocalPort = int.TryParse(context.Request.Headers[LocalPortHeader], out var port) ? port : ports.AdminPort;
            connection.RemoteIpAddress = ReadAddress(context.Request.Headers[RemoteIpHeader]);
            connection.LocalIpAddress = ReadAddress(context.Request.Headers[LocalIpHeader]);
            // The real PC port is HTTPS only, so simulate that unless a test asks for a plain connection.
            if (connection.LocalPort == ports.ClientPort && !context.Request.Headers.ContainsKey(PlainHttpHeader))
            {
                context.Request.Scheme = "https";
            }

            return nextMiddleware(context);
        });
        next(app);
    };

    private static IPAddress? ReadAddress(string? header)
    {
        if (string.IsNullOrEmpty(header))
        {
            return IPAddress.Loopback;
        }

        return IPAddress.TryParse(header, out var address) ? address : null;
    }
}

/// <summary>A fake clock that can be told to fail, to force an unhandled error inside an endpoint.</summary>
public sealed class FailableTimeProvider(DateTimeOffset start) : FakeTimeProvider(start)
{
    public bool Fail { get; set; }

    public override DateTimeOffset GetUtcNow() =>
        Fail ? throw new InvalidOperationException("Simulated clock failure.") : base.GetUtcNow();
}

/// <summary>Collects everything the server logs, so tests can check what was (and wasn't) written.</summary>
public sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _entries = new();

    public IReadOnlyCollection<string> Entries => _entries;

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(_entries);

    public void Dispose()
    {
    }

    private sealed class CapturingLogger(System.Collections.Concurrent.ConcurrentQueue<string> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            entries.Enqueue($"{logLevel}: {formatter(state, exception)} {exception}");
    }
}
