using System.Buffers.Text;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.RateLimiting;
using LabGuard.Server.Auth;
using LabGuard.Server.Data;
using LabGuard.Server.Network;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace LabGuard.Server.Clients;

public record PingResponse(string Service);
public record RegisterPcRequest(string? Name, string? SetupSecret);
public record RegisterPcResponse(int Id, string Name, string Token);

public static partial class ClientEndpoints
{
    public const int MaxNameLength = 32;
    public const int MaxRegistrationsPerMinute = 10;
    public const string RegistrationRateLimitPolicy = "pc-registration";
    // A PC sends 12 heartbeats a minute, so this leaves plenty of room.
    public const int MaxTrafficPerMinute = 120;
    public const string TrafficRateLimitPolicy = "pc-traffic";

    private const int TokenBytes = 32;
    private const int MaxTokenLength = 64;
    private const string BearerPrefix = "Bearer ";
    private const string NameTakenMessage = "That PC name is already registered.";
    private const string WrongSetupCodeMessage = "That setup code isn't right.";
    private const int SqliteUniqueConstraintErrorCode = 2067;

    [GeneratedRegex(@"^[A-Za-z0-9._-]+\z")]
    private static partial Regex NamePattern();

    public static IServiceCollection AddClientRateLimiting(this IServiceCollection services) =>
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = (context, cancellationToken) =>
            {
                var policy = context.HttpContext.GetEndpoint()?.Metadata.GetMetadata<EnableRateLimitingAttribute>()?.PolicyName;
                context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                if (policy == LoginRateLimitPolicy)
                {
                    context.HttpContext.Response.Headers.RetryAfter = "60";
                    return new ValueTask(context.HttpContext.Response.WriteAsJsonAsync(
                        new RateLimitResponse("Too many attempts. Try again in a minute.", 60), cancellationToken));
                }

                var message = policy == RegistrationRateLimitPolicy
                    ? "Too many registration attempts. Try again in a minute."
                    : "Too many requests. Try again in a minute.";
                return new ValueTask(context.HttpContext.Response.WriteAsJsonAsync(new ErrorResponse(message), cancellationToken));
            };

            // Partitioned by the connection's address only; forwarded headers are never trusted.
            // Each policy keeps its own counters, so heavy ping traffic can't use up registrations or the reverse.
            options.AddPolicy(RegistrationRateLimitPolicy, context =>
                PerAddressLimit(context, RegistrationRateLimitPolicy, MaxRegistrationsPerMinute));
            options.AddPolicy(TrafficRateLimitPolicy, context =>
                PerAddressLimit(context, TrafficRateLimitPolicy, MaxTrafficPerMinute));
            // Per address, so registering fake PCs can't get around the per-PC login lockout.
            options.AddPolicy(LoginRateLimitPolicy, context =>
                PerAddressLimit(context, LoginRateLimitPolicy, MaxLoginsPerMinute));
        });

    private static RateLimitPartition<string> PerAddressLimit(HttpContext context, string policy, int permitLimit) =>
        RateLimitPartition.GetFixedWindowLimiter(
            $"{policy}:{RemoteAddress(context)?.ToString() ?? "unknown"}",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = permitLimit,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            });

    public static IEndpointRouteBuilder MapClientEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/client").AllowAnonymous();
        group.MapGet("/ping", () => Results.Ok(new PingResponse("lykantra"))).RequireRateLimiting(TrafficRateLimitPolicy);
        group.MapPost("/register", RegisterAsync).RequireRateLimiting(RegistrationRateLimitPolicy);
        group.MapPost("/heartbeat", HeartbeatAsync).RequireRateLimiting(TrafficRateLimitPolicy)
            .AddEndpointFilter<ClientStorageErrorFilter>();
        group.MapPost("/login", LoginAsync).RequireRateLimiting(LoginRateLimitPolicy)
            .AddEndpointFilter<ClientStorageErrorFilter>();
        group.MapPost("/logout", LogoutAsync).RequireRateLimiting(TrafficRateLimitPolicy)
            .AddEndpointFilter<ClientStorageErrorFilter>();
        return app;
    }

    private static async Task<IResult> RegisterAsync(HttpContext http, AppDbContext db, TimeProvider clock, PcChannelStore channel)
    {
        var request = await AuthEndpoints.ReadBodyAsync<RegisterPcRequest>(http.Request);
        if (request is null)
        {
            return AuthEndpoints.InvalidRequest();
        }

        // First, so nothing about the name (valid, taken) can be probed without the setup code.
        if (!channel.SecretMatches(request.SetupSecret))
        {
            return Results.Json(new ErrorResponse(WrongSetupCodeMessage), statusCode: StatusCodes.Status403Forbidden);
        }

        var name = request.Name?.Trim() ?? "";
        if (name.Length == 0 || name.Length > MaxNameLength || !NamePattern().IsMatch(name))
        {
            return AuthEndpoints.BadRequest(
                $"PC name must be 1 to {MaxNameLength} characters: letters, numbers, dots, underscores and hyphens only.");
        }

        var ct = http.RequestAborted;
        if (await db.Pcs.AnyAsync(p => p.Name == name, ct))
        {
            return NameTaken();
        }

        var token = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(TokenBytes));
        var now = clock.GetUtcNow();
        var pc = new Pc
        {
            Name = name,
            TokenHash = HashToken(token),
            IpAddress = RemoteAddress(http)?.ToString(),
            RegisteredAtUtc = now,
            LastSeenUtc = now,
        };
        db.Pcs.Add(pc);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            return NameTaken();
        }

        return Results.Json(new RegisterPcResponse(pc.Id, pc.Name, token), statusCode: StatusCodes.Status201Created);
    }

    private static string? ReadBearerToken(HttpRequest request)
    {
        var header = request.Headers.Authorization.ToString();
        if (!header.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var token = header[BearerPrefix.Length..].Trim();
        return token.Length is > 0 and <= MaxTokenLength ? token : null;
    }

    // High-entropy random token, so a plain SHA-256 is enough; no slow password hash needed.
    private static string HashToken(string token) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    // IPv4 clients on a dual-stack socket show up as ::ffff:a.b.c.d; store the plain IPv4 form.
    private static IPAddress? RemoteAddress(HttpContext http)
    {
        var address = http.Connection.RemoteIpAddress;
        return address is { IsIPv4MappedToIPv6: true } ? address.MapToIPv4() : address;
    }

    private static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is SqliteException { SqliteExtendedErrorCode: SqliteUniqueConstraintErrorCode };

    private static IResult NameTaken() =>
        Results.Json(new ErrorResponse(NameTakenMessage), statusCode: StatusCodes.Status409Conflict);

    internal static IResult NotRegistered() =>
        Results.Json(new ErrorResponse("This PC isn't registered."), statusCode: StatusCodes.Status401Unauthorized);

    internal static IResult ServerBusy() =>
        Results.Json(new ErrorResponse("The server is busy. Try again in a moment."), statusCode: StatusCodes.Status503ServiceUnavailable);
}
