using LabGuard.Server.Auth;
using LabGuard.Server.Clients;
using LabGuard.Server.Data;
using LabGuard.Server.Sessions;
using Microsoft.EntityFrameworkCore;

namespace LabGuard.Server.Pcs;

// Admin lock and unlock of a PC's session. Every route requires a JSON body, so a cross-origin HTML form can't trigger it.
public static partial class PcEndpoints
{
    private static async Task<IResult> LockAsync(int id, HttpContext http, AppDbContext db, TimeProvider clock)
    {
        if (await AuthEndpoints.ReadBodyAsync<PcAdminActionRequest>(http.Request) is null)
        {
            return AuthEndpoints.InvalidRequest();
        }

        var ct = http.RequestAborted;
        if (!await db.Pcs.AnyAsync(p => p.Id == id, ct))
        {
            return PcNotFound();
        }

        // A session that already ran out is recorded as time-expired; only the rest is ended as an admin lock.
        var now = clock.GetUtcNow();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await SessionLifecycle.EndExpiredForPcAsync(db, id, now, ct);
        await SessionLifecycle.EndForPcAsync(db, id, SessionLifecycle.AdminLock, now, ct);
        await transaction.CommitAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> UnlockAsync(int id, HttpContext http, AppDbContext db, TimeProvider clock)
    {
        if (await AuthEndpoints.ReadBodyAsync<PcAdminActionRequest>(http.Request) is null)
        {
            return AuthEndpoints.InvalidRequest();
        }

        // Closing an expired session comes first: that write takes the database lock, so heartbeats can't commit
        // between the reads below and the insert. Only a session that is still running then blocks the unlock.
        // The PC is read inside the transaction, so a PC removed a moment ago can't get a session.
        var ct = http.RequestAborted;
        var now = clock.GetUtcNow();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await SessionLifecycle.EndExpiredForPcAsync(db, id, now, ct);
        var pc = await db.Pcs.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct);
        if (pc is null)
        {
            return PcNotFound();
        }

        if (!IsOnline(pc.LastSeenUtc, now))
        {
            return ConflictResponse("This PC is offline.");
        }

        // Committed either way, so closing an expired session is kept even when the unlock is refused.
        var inUse = await db.PcSessions.AnyAsync(s => s.PcId == id && s.EndedAtUtc == null, ct);
        var opened = !inUse && await TryOpenAdminUnlockAsync(db, pc, now, ct);
        await transaction.CommitAsync(ct);

        return opened ? Results.NoContent() : InUseResponse();
    }

    // Returns false when an active session already holds the PC. The unique index reports that, for example when a
    // login wins the race, and only this PC is skipped, so a bulk unlock carries on with the others.
    private static async Task<bool> TryOpenAdminUnlockAsync(AppDbContext db, Pc pc, DateTimeOffset now, CancellationToken ct)
    {
        var session = SessionLifecycle.NewAdminUnlock(pc, now);
        db.PcSessions.Add(session);
        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException exception) when (ClientEndpoints.IsUniqueViolation(exception))
        {
            db.Entry(session).State = EntityState.Detached;
            return false;
        }
    }

    private static async Task<IResult> LockAllAsync(HttpContext http, AppDbContext db, TimeProvider clock)
    {
        if (await AuthEndpoints.ReadBodyAsync<PcAdminActionRequest>(http.Request) is null)
        {
            return AuthEndpoints.InvalidRequest();
        }

        // Expired sessions are recorded as time-expired first, so only the sessions still running are counted as admin locks.
        var ct = http.RequestAborted;
        var now = clock.GetUtcNow();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await SessionLifecycle.EndExpiredAsync(db, now, ct);
        var locked = await SessionLifecycle.EndAllAsync(db, SessionLifecycle.AdminLock, now, ct);
        await transaction.CommitAsync(ct);
        return Results.Ok(new AffectedPcsResponse(locked));
    }

    private static async Task<IResult> UnlockAllAsync(HttpContext http, AppDbContext db, TimeProvider clock)
    {
        if (await AuthEndpoints.ReadBodyAsync<PcAdminActionRequest>(http.Request) is null)
        {
            return AuthEndpoints.InvalidRequest();
        }

        var ct = http.RequestAborted;
        var now = clock.GetUtcNow();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await SessionLifecycle.EndExpiredAsync(db, now, ct);
        var pcs = await db.Pcs.AsNoTracking().ToListAsync(ct);
        var busyPcIds = (await db.PcSessions
            .AsNoTracking()
            .Where(s => s.EndedAtUtc == null)
            .Select(s => s.PcId)
            .ToListAsync(ct))
            .ToHashSet();

        // A PC that gets a session in the meantime is skipped by the unique index, so the others still open.
        var unlocked = 0;
        foreach (var pc in pcs)
        {
            if (IsOnline(pc.LastSeenUtc, now) && !busyPcIds.Contains(pc.Id) && await TryOpenAdminUnlockAsync(db, pc, now, ct))
            {
                unlocked++;
            }
        }

        await transaction.CommitAsync(ct);
        return Results.Ok(new AffectedPcsResponse(unlocked));
    }

    private static IResult PcNotFound() =>
        Results.Json(new ErrorResponse("PC not found."), statusCode: StatusCodes.Status404NotFound);

    private static IResult InUseResponse() => ConflictResponse("This PC is already in use.");

    private static IResult ConflictResponse(string message) =>
        Results.Json(new ErrorResponse(message), statusCode: StatusCodes.Status409Conflict);
}
