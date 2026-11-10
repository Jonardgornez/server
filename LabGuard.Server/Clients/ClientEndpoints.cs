using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using LabGuard.Server.Auth;
using LabGuard.Server.Backgrounds;
using LabGuard.Server.ClientUpdates;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace LabGuard.Server.Clients;

public record PingResponse(string Service);

public static partial class ClientEndpoints
{
    public const int MaxNameLength = 32;
    public const int MaxRegistrationsPerMinute = 10;
    public const string RegistrationRateLimitPolicy = "pc-registration";
    // A PC sends 30 heartbeats a minute (one every 2 seconds), so 120 a minute still leaves room.
    public const int MaxTrafficPerMinute = 120;
    public const string TrafficRateLimitPolicy = "pc-traffic";
    // A PC downloads a background only when its hash changes, so a few a minute is plenty and bounds the bytes a PC can pull.
    public const int MaxBackgroundDownloadsPerMinute = 10;
    public const string BackgroundRateLimitPolicy = "pc-background";
    // The installer is about 75 MB, so a PC needs only a few downloads a minute, and this bounds the bytes a PC can pull.
    public const int MaxClientUpdateDownloadsPerMinute = 6;
    public const string ClientUpdateRateLimitPolicy = "pc-client-update";

    private const int MaxTokenLength = 64;
    private const string BearerPrefix = "Bearer ";
    private const string NameTakenMessage = "That PC name is already registered.";
    private const int SqliteUniqueConstraintErrorCode = 2067;

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
            options.AddPolicy(BackgroundRateLimitPolicy, context =>
                PerAddressLimit(context, BackgroundRateLimitPolicy, MaxBackgroundDownloadsPerMinute));
            options.AddPolicy(ClientUpdateRateLimitPolicy, context =>
                PerAddressLimit(context, ClientUpdateRateLimitPolicy, MaxClientUpdateDownloadsPerMinute));
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
        group.MapPost("/register-requests", CreateRegistrationRequestAsync).RequireRateLimiting(RegistrationRateLimitPolicy)
            .AddEndpointFilter<ClientStorageErrorFilter>();
        group.MapPost("/register-requests/status", RegistrationStatusAsync).RequireRateLimiting(TrafficRateLimitPolicy)
            .AddEndpointFilter<ClientStorageErrorFilter>();
        group.MapPost("/register-requests/reveal", RevealRegistrationRequestAsync).RequireRateLimiting(TrafficRateLimitPolicy)
            .AddEndpointFilter<ClientStorageErrorFilter>();
        group.MapPost("/register-requests/cancel", CancelRegistrationRequestAsync).RequireRateLimiting(TrafficRateLimitPolicy)
            .AddEndpointFilter<ClientStorageErrorFilter>();
        group.MapPost("/reconnect", ReconnectAsync).RequireRateLimiting(RegistrationRateLimitPolicy)
            .AddEndpointFilter<ClientStorageErrorFilter>();
        group.MapPost("/heartbeat", HeartbeatAsync).RequireRateLimiting(TrafficRateLimitPolicy)
            .AddEndpointFilter<ClientStorageErrorFilter>();
        group.MapPost("/login", LoginAsync).RequireRateLimiting(LoginRateLimitPolicy)
            .AddEndpointFilter<ClientStorageErrorFilter>();
        group.MapPost("/logout", LogoutAsync).RequireRateLimiting(TrafficRateLimitPolicy)
            .AddEndpointFilter<ClientStorageErrorFilter>();
        group.MapPost("/offline", OfflineAsync).RequireRateLimiting(TrafficRateLimitPolicy)
            .AddEndpointFilter<ClientStorageErrorFilter>();
        group.MapGet("/screen/stream", ScreenStreamAsync).RequireRateLimiting(TrafficRateLimitPolicy)
            .AddEndpointFilter<ClientStorageErrorFilter>();
        group.MapPost("/messages/{id:long}/ack", AckMessageAsync).RequireRateLimiting(TrafficRateLimitPolicy)
            .AddEndpointFilter<ClientStorageErrorFilter>();
        group.MapPost("/login-request", CreateLoginRequestAsync).RequireRateLimiting(TrafficRateLimitPolicy)
            .AddEndpointFilter<ClientStorageErrorFilter>();
        group.MapPost("/login-request/cancel", CancelLoginRequestAsync).RequireRateLimiting(TrafficRateLimitPolicy)
            .AddEndpointFilter<ClientStorageErrorFilter>();
        group.MapGet("/backgrounds/{kind}", BackgroundEndpoints.DownloadForPcAsync).RequireRateLimiting(BackgroundRateLimitPolicy)
            .AddEndpointFilter<ClientStorageErrorFilter>();
        group.MapGet("/client-update/{sha256}", ClientUpdateDownloadEndpoints.DownloadForPcAsync).RequireRateLimiting(ClientUpdateRateLimitPolicy)
            .AddEndpointFilter<ClientStorageErrorFilter>();
        return app;
    }

    internal static string? ReadBearerToken(HttpRequest request)
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

    internal static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is SqliteException { SqliteExtendedErrorCode: SqliteUniqueConstraintErrorCode };

    internal static IResult NameTaken() =>
        Results.Json(new ErrorResponse(NameTakenMessage), statusCode: StatusCodes.Status409Conflict);

    internal static IResult NotRegistered() =>
        Results.Json(new ErrorResponse("This PC isn't registered."), statusCode: StatusCodes.Status401Unauthorized);

    internal static IResult ServerBusy() =>
        Results.Json(new ErrorResponse("The server is busy. Try again in a moment."), statusCode: StatusCodes.Status503ServiceUnavailable);
}
