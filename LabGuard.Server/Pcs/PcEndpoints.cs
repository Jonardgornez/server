using LabGuard.Server.Accounts;
using LabGuard.Server.Auth;
using LabGuard.Server.Data;
using LabGuard.Server.Sessions;
using Microsoft.EntityFrameworkCore;

namespace LabGuard.Server.Pcs;

public record PcResponse(
    int Id,
    string Name,
    bool IsOnline,
    string? IpAddress,
    DateTimeOffset RegisteredAt,
    DateTimeOffset LastSeenAt,
    PcSessionSummary? Session);

public record PcSessionSummary(string Username, DateTimeOffset StartedAt, DefaultTimeResponse Time, int? SecondsRemaining);

public static class PcEndpoints
{
    // A PC counts as online while its last heartbeat is newer than this.
    public static readonly TimeSpan OnlineWindow = TimeSpan.FromSeconds(15);

    public static IEndpointRouteBuilder MapPcEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/pcs");
        group.MapGet("/", ListAsync);
        group.MapDelete("/{id:int}", DeleteAsync);
        return app;
    }

    // A last-seen time in the future (the server clock moved back) counts as offline.
    private static bool IsOnline(DateTimeOffset lastSeen, DateTimeOffset now) =>
        lastSeen <= now && now - lastSeen < OnlineWindow;

    private static async Task<IResult> ListAsync(AppDbContext db, TimeProvider clock, CancellationToken cancellationToken)
    {
        var pcs = await db.Pcs
            .AsNoTracking()
            .OrderBy(p => p.Name)
            .ThenBy(p => p.Id)
            .ToListAsync(cancellationToken);

        // At most one active session per PC (unique index).
        var activeSessions = (await db.PcSessions
            .AsNoTracking()
            .Where(s => s.EndedAtUtc == null)
            .ToListAsync(cancellationToken))
            .ToDictionary(s => s.PcId);

        var now = clock.GetUtcNow();
        return Results.Ok(pcs.Select(p =>
        {
            var isOnline = IsOnline(p.LastSeenUtc, now);
            return new PcResponse(
                p.Id,
                p.Name,
                isOnline,
                p.IpAddress,
                p.RegisteredAtUtc,
                p.LastSeenUtc,
                isOnline && activeSessions.TryGetValue(p.Id, out var session) && !SessionLifecycle.HasExpired(session, now)
                    ? ToSummary(session, now)
                    : null);
        }));
    }

    private static PcSessionSummary ToSummary(PcSession session, DateTimeOffset now) =>
        new(session.Username, session.StartedAtUtc, SessionLifecycle.ToTime(session.DefaultTimeMinutes), SessionLifecycle.SecondsRemaining(session, now));

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
            await transaction.CommitAsync(cancellationToken);
            return Results.NoContent();
        }

        return await db.Pcs.AnyAsync(p => p.Id == id, cancellationToken)
            ? Results.Json(new ErrorResponse("This PC is online. It can only be removed while offline."), statusCode: StatusCodes.Status409Conflict)
            : Results.Json(new ErrorResponse("PC not found."), statusCode: StatusCodes.Status404NotFound);
    }
}
