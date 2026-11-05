using LabGuard.Server.Accounts;
using LabGuard.Server.Auth;
using LabGuard.Server.Clients;
using LabGuard.Server.Data;
using LabGuard.Server.Network;
using LabGuard.Server.Pcs;
using LabGuard.Server.Requests;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Two separate ports: the admin web UI listens on loopback only, so no setting can expose it to the LAN,
// while lab PCs reach /api/client/* on the client port. ChannelGuard keeps the two paths apart.
var ports = ServerPorts.FromConfiguration(builder.Configuration);
builder.Services.AddSingleton(ports);
builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenLocalhost(ports.AdminPort);
    options.ListenAnyIP(ports.ClientPort);
});

builder.Services.AddOpenApi();
builder.Services.AddAdminAuth();
builder.Services.AddClientRateLimiting();
builder.Services.AddSingleton<DummyLoginCredential>();
builder.Services.AddDbContext<AppDbContext>((services, options) =>
{
    var configuration = services.GetRequiredService<IConfiguration>();
    var environment = services.GetRequiredService<IHostEnvironment>();
    var connection = new SqliteConnectionStringBuilder(configuration.GetConnectionString("AppDb"));
    connection.DataSource = Path.GetFullPath(connection.DataSource, environment.ContentRootPath);
    options.UseSqlite(connection.ToString());
});

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var services = scope.ServiceProvider;
    var db = services.GetRequiredService<AppDbContext>();
    db.Database.Migrate();
    await AdminSeeder.SeedAsync(
        db,
        app.Configuration,
        app.Environment,
        services.GetRequiredService<IPasswordHasher<AdminAccount>>(),
        services.GetRequiredService<ILoggerFactory>().CreateLogger(nameof(AdminSeeder)));
    await LoginRequestSeeder.SeedAsync(
        db,
        app.Configuration,
        app.Environment,
        services.GetRequiredService<TimeProvider>(),
        services.GetRequiredService<ILoggerFactory>().CreateLogger(nameof(LoginRequestSeeder)));
}

app.UseMiddleware<ChannelGuard>();
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
app.MapPcEndpoints();
app.MapClientEndpoints();

app.Run();

record HealthResponse(string Status);

public partial class Program;
