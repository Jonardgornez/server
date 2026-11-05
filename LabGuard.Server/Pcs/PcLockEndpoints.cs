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

        // The PC is read inside the transaction, so a PC removed a moment ago can't get a session.
        var ct = http.RequestAborted;
        var now = clock.GetUtcNow();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var pc = await db.Pcs.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct);
        if (pc is null)
        {
            return PcNotFound();
        }

        if (!IsOnline(pc.LastSeenUtc, now))
        {
            return ConflictResponse("This PC is offline.");
        }

        // An expired session is closed first, so only a session that is still running blocks the unlock.
        // The transaction is committed either way, so that closing is kept even when the unlock is refused.
        await SessionLifecycle.EndExpiredForPcAsync(db, id, now, ct);
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

    private static IResult PcNotFound() =>
        Results.Json(new ErrorResponse("PC not found."), statusCode: StatusCodes.Status404NotFound);

    private static IResult InUseResponse() => ConflictResponse("This PC is already in use.");

    private static IResult ConflictResponse(string message) =>
        Results.Json(new ErrorResponse(message), statusCode: StatusCodes.Status409Conflict);
}
