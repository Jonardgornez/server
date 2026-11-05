using System.Net;
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

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(_environment);
        builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:AppDb"] = $"Data Source={_databasePath}",
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
