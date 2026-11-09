using LabGuard.Server.Accounts;
using LabGuard.Server.Auth;
using LabGuard.Server.Data;
using LabGuard.Server.Network;
using LabGuard.Server.Requests;
using LabGuard.Server.Sessions;
using Microsoft.EntityFrameworkCore;

namespace LabGuard.Server.Pcs;

// ShutdownState is null, "shutting-down", "shutdown", "restarting", "rebooting" or "cancelling". IsServerPc is true for the PC this server runs on.
// InternetBlocked is what the administrator wants. InternetBlockApplied is what the PC last reported, null until it has reported.
public record PcResponse(
    int Id,
    string Name,
    bool IsOnline,
    bool IsLocked,
    string? IpAddress,
    DateTimeOffset RegisteredAt,
    DateTimeOffset LastSeenAt,
    PcSessionSummary? Session,
    string? ShutdownState,
    bool IsServerPc,
    bool InternetBlocked,
    bool? InternetBlockApplied);

// Username is null for an admin unlock, which has no account.
public record PcSessionSummary(string? Username, DateTimeOffset StartedAt, DefaultTimeResponse Time, int? SecondsRemaining);

// Sent as {} by the single lock and unlock routes; the body is required so a cross-origin form can't send it.
public record PcAdminActionRequest;
// Action is add-time, open-time or time-out. Minutes is a double so a fraction reaches validation instead of failing JSON parsing.
public record PcTimeRequest(string? Action, double? Minutes);
// Sent by the bulk lock and unlock routes. A missing, empty or over-long list is refused.
public record PcIdsRequest(int[]? Ids);
public record AffectedPcsResponse(int Affected);

public static partial class PcEndpoints
{
    // A PC counts as online while its last heartbeat is newer than this.
    public static readonly TimeSpan OnlineWindow = TimeSpan.FromSeconds(15);

    public static IEndpointRouteBuilder MapPcEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/pcs");
        group.MapGet("/", ListAsync);
        group.MapDelete("/{id:int}", DeleteAsync);
        group.MapPost("/{id:int}/lock", LockAsync).AddEndpointFilter<PcStorageErrorFilter>();
        group.MapPost("/{id:int}/unlock", UnlockAsync).AddEndpointFilter<PcStorageErrorFilter>();
        group.MapPost("/{id:int}/time", AdjustTimeAsync).AddEndpointFilter<PcStorageErrorFilter>();
        group.MapPost("/lock", LockManyAsync).AddEndpointFilter<PcStorageErrorFilter>();
        group.MapPost("/unlock", UnlockManyAsync).AddEndpointFilter<PcStorageErrorFilter>();
        group.MapPost("/{id:int}/shutdown", ShutdownAsync).AddEndpointFilter<PcStorageErrorFilter>();
        group.MapPost("/shutdown", ShutdownManyAsync).AddEndpointFilter<PcStorageErrorFilter>();
        group.MapPost("/{id:int}/reboot", RebootAsync).AddEndpointFilter<PcStorageErrorFilter>();
        group.MapPost("/reboot", RebootManyAsync).AddEndpointFilter<PcStorageErrorFilter>();
        group.MapPost("/{id:int}/cancel-power", CancelPowerAsync).AddEndpointFilter<PcStorageErrorFilter>();
        group.MapPost("/cancel-power", CancelPowerManyAsync).AddEndpointFilter<PcStorageErrorFilter>();
        group.MapPost("/messages", SendMessageAsync).AddEndpointFilter<PcStorageErrorFilter>();
        group.MapPost("/internet", SetInternetBlockAsync).AddEndpointFilter<PcStorageErrorFilter>();
        return app;
    }

    // A last-seen time in the future (the server clock moved back) counts as offline.
    internal static bool IsOnline(DateTimeOffset lastSeen, DateTimeOffset now) =>
        lastSeen <= now && now - lastSeen < OnlineWindow;

    private static async Task<IResult> ListAsync(
        AppDbContext db,
        TimeProvider clock,
        ILocalAddressSource localAddresses,
        CancellationToken cancellationToken)
    {
        var pcs = await db.Pcs
            .AsNoTracking()
            // Registration order: the Id grows with each new PC, so a newly added PC appears last.
            .OrderBy(p => p.Id)
            .ToListAsync(cancellationToken);

        // At most one active session per PC (unique index).
        var activeSessions = (await db.PcSessions
            .AsNoTracking()
            .Where(s => s.EndedAtUtc == null)
            .ToListAsync(cancellationToken))
            .ToDictionary(s => s.PcId);

        var now = clock.GetUtcNow();
        // When the addresses can't be read, no PC is shown as the server. The list still answers.
        var machineAddresses = localAddresses.ReadOrNull();
        return Results.Ok(pcs.Select(p =>
        {
            var isOnline = IsOnline(p.LastSeenUtc, now);
            return new PcResponse(
                p.Id,
                p.Name,
                isOnline,
                p.LockedAtUtc is not null,
                p.IpAddress,
                p.RegisteredAtUtc,
                p.LastSeenUtc,
                isOnline && activeSessions.TryGetValue(p.Id, out var session) && !SessionLifecycle.HasExpired(session, now)
                    ? ToSummary(session, now)
                    : null,
                PcShutdownRules.StateOf(p, isOnline, now),
                machineAddresses is not null && ServerPcRule.IsServerPc(p.IpAddress, machineAddresses),
                p.InternetBlocked,
                p.InternetBlockApplied);
        }));
    }

    private static PcSessionSummary ToSummary(PcSession session, DateTimeOffset now) =>
        new(session.IsAdminUnlock ? null : session.Username, session.StartedAtUtc, SessionLifecycle.ToTime(session.DefaultTimeMinutes), SessionLifecycle.SecondsRemaining(session, now));

    private static async Task<IResult> DeleteAsync(int id, AppDbContext db, TimeProvider clock, CancellationToken cancellationToken)
    {
        // Deletes only while the PC is offline, in one statement, so a heartbeat arriving
        // between a check and the delete can't get an online PC removed.
        // Exactly the complement of IsOnline: offline is "old enough" or "in the future" (clock moved back).
        var now = clock.GetUtcNow();
        var offlineSince = now - OnlineWindow;
        // The session ends in the same transaction, so a removed PC never keeps an active session.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var deleted = await db.Pcs
            .Where(p => p.Id == id && (p.LastSeenUtc <= offlineSince || p.LastSeenUtc > now))
            .ExecuteDeleteAsync(cancellationToken);
        if (deleted > 0)
        {
            await SessionLifecycle.EndForPcAsync(db, id, SessionLifecycle.PcRemoved, now, cancellationToken);
            await LoginRequestLifecycle.CancelPendingForPcAsync(db, id, LoginRequestLifecycle.PcRemoved, now, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return Results.NoContent();
        }

        return await db.Pcs.AnyAsync(p => p.Id == id, cancellationToken)
            ? Results.Json(new ErrorResponse("This PC is online. It can only be removed while offline."), statusCode: StatusCodes.Status409Conflict)
            : Results.Json(new ErrorResponse("PC not found."), statusCode: StatusCodes.Status404NotFound);
    }
}
