using LabGuard.Server.Accounts;
using LabGuard.Server.Backgrounds;
using LabGuard.Server.Auth;
using LabGuard.Server.ClientUpdates;
using LabGuard.Server.Clients;
using LabGuard.Server.Data;
using LabGuard.Server.Logs;
using LabGuard.Server.Network;
using LabGuard.Server.Pcs;
using LabGuard.Server.Requests;
using LabGuard.Server.Screen;
using LabGuard.Server.Settings;
using LabGuard.Server.Wallpapers;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using System.Net.WebSockets;
using System.Security.Authentication;

if (PcChannelExportCommand.IsRequested(args))
{
    Environment.ExitCode = PcChannelExportCommand.Run(args);
    return;
}

var builder = WebApplication.CreateBuilder(args);

// In the container the sign-in cookie keys must outlive a rebuild, so they are kept in a mounted folder.
// Without the setting the default key location is used, as before, so existing sign-ins stay valid.
var dataProtectionKeysFolder = builder.Configuration["DataProtection:KeysFolder"];
if (!string.IsNullOrWhiteSpace(dataProtectionKeysFolder))
{
    builder.Services.AddDataProtection()
        .PersistKeysToFileSystem(new DirectoryInfo(Path.GetFullPath(dataProtectionKeysFolder, builder.Environment.ContentRootPath)))
        .SetApplicationName("Lykantra");
}

// Two separate ports: the admin web UI listens on loopback only, so no setting can expose it to the LAN,
// while lab PCs reach /api/client/* on the client port. ChannelGuard keeps the two paths apart.
builder.Services.AddSingleton(services => ServerPorts.FromConfiguration(services.GetRequiredService<IConfiguration>()));
// The PC port is HTTPS only, with a self-signed certificate that PCs pin by public key (see PcChannelStore).
builder.Services.AddSingleton(services =>
    PcChannelStore.Open(PcChannelStore.ResolvePath(
        services.GetRequiredService<IConfiguration>(),
        services.GetRequiredService<IHostEnvironment>().ContentRootPath)));

builder.WebHost.ConfigureKestrel(options =>
{
    var ports = options.ApplicationServices.GetRequiredService<ServerPorts>();
    options.ListenLocalhost(ports.AdminPort);
    options.ListenAnyIP(ports.ClientPort, listen => listen.UseHttps(https =>
    {
        https.ServerCertificate = options.ApplicationServices.GetRequiredService<PcChannelStore>().Certificate;
        https.SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13;
    }));
});

builder.Services.AddOpenApi();
builder.Services.AddAdminAuth();
builder.Services.AddClientRateLimiting();
builder.Services.AddScreenViewing();
builder.Services.AddClientUpdates();
builder.Services.AddSingleton<DummyLoginCredential>();
builder.Services.AddSingleton<PcMessageSends>();
builder.Services.AddSingleton<ILocalAddressSource, NetworkInterfaceAddressSource>();
builder.Services.AddDbContext<AppDbContext>((services, options) =>
{
    var configuration = services.GetRequiredService<IConfiguration>();
    var environment = services.GetRequiredService<IHostEnvironment>();
    var connection = new SqliteConnectionStringBuilder(configuration.GetConnectionString("AppDb"));
    connection.DataSource = Path.GetFullPath(connection.DataSource, environment.ContentRootPath);
    options.UseSqlite(connection.ToString());
});

var app = builder.Build();

// Opened before anything else so an unreadable channel file stops startup with a clear message.
app.Services.GetRequiredService<PcChannelStore>();

using (var scope = app.Services.CreateScope())
{
    var services = scope.ServiceProvider;
    var db = services.GetRequiredService<AppDbContext>();
    db.Database.Migrate();
    // Files left by a crash or a failed delete are removed; the stored installer, if any, is kept.
    services.GetRequiredService<ClientReleaseFiles>().SweepExcept(
        await db.ClientReleases.Select(r => r.FileName).FirstOrDefaultAsync(),
        services.GetRequiredService<TimeProvider>().GetUtcNow());
    await AdminSeeder.SeedAsync(
        db,
        app.Configuration,
        app.Environment,
        services.GetRequiredService<IPasswordHasher<AdminAccount>>(),
        services.GetRequiredService<ILoggerFactory>().CreateLogger(nameof(AdminSeeder)));
    // No live view survives a restart, so any view row still open from a crash ends now.
    await services.GetRequiredService<ScreenViewAudit>().CloseOrphanedAsync(
        services.GetRequiredService<TimeProvider>().GetUtcNow(),
        CancellationToken.None);
    await services.GetRequiredService<ScreenControlAudit>().CloseOrphanedAsync(
        services.GetRequiredService<TimeProvider>().GetUtcNow(),
        CancellationToken.None);
    await PcMessageRetention.DeleteOlderThanAsync(
        db,
        services.GetRequiredService<TimeProvider>().GetUtcNow(),
        CancellationToken.None);
    await LoginRequestRetention.DeleteDecidedOlderThanAsync(
        db,
        services.GetRequiredService<TimeProvider>().GetUtcNow(),
        CancellationToken.None);
}

app.UseMiddleware<ChannelGuard>();
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = ScreenProtocol.KeepAliveInterval });
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi().AllowAnonymous();
}

app.MapGet("/api/health", () => new HealthResponse("ok"))
    .WithName("GetHealth")
    .AllowAnonymous();

app.MapAuthEndpoints();
app.MapAccountEndpoints();
app.MapLoginRequestEndpoints();
app.MapRegistrationRequestEndpoints();
app.MapPcEndpoints();
app.MapSessionLogEndpoints();
app.MapScreenViewEndpoints();
app.MapSettingsEndpoints();
app.MapBackgroundEndpoints();
app.MapWallpaperEndpoints();
app.MapClientUpdateEndpoints();
app.MapServerInfoEndpoints();
app.MapClientEndpoints();

app.Run();

record HealthResponse(string Status);

public partial class Program;
