using LabGuard.Server.Auth;
using LabGuard.Server.Clients;
using LabGuard.Server.Data;
using LabGuard.Server.Requests;
using LabGuard.Server.Sessions;
using Microsoft.EntityFrameworkCore;

namespace LabGuard.Server.Pcs;

// Admin lock and unlock of a PC. A lock freezes the PC until the admin unlocks it: a running session is paused, not ended.
// Every route requires a JSON body, so a cross-origin HTML form can't trigger it.
public static partial class PcEndpoints
{
    private static async Task<IResult> LockAsync(int id, HttpContext http, AppDbContext db, TimeProvider clock)
    {
        if (await AuthEndpoints.ReadBodyAsync<PcAdminActionRequest>(http.Request) is null)
        {
            return AuthEndpoints.InvalidRequest();
        }

        // Closing expired sessions comes first: that write takes the database lock, as in UnlockAsync.
        var ct = http.RequestAborted;
        var now = clock.GetUtcNow();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await SessionLifecycle.EndExpiredAsync(db, now, ct);
        var pc = await db.Pcs.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct);
        if (pc is null)
        {
            return PcNotFound();
        }

        if (!IsOnline(pc.LastSeenUtc, now))
        {
            return ConflictResponse("This PC is offline.");
        }

        await LockPcsAsync(db, [id], now, ct);
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
        await SessionLifecycle.EndExpiredAsync(db, now, ct);
        var pc = await db.Pcs.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct);
        if (pc is null)
        {
            return PcNotFound();
        }

        if (!IsOnline(pc.LastSeenUtc, now))
        {
            return ConflictResponse("This PC is offline.");
        }

        var active = await db.PcSessions.AsNoTracking().FirstOrDefaultAsync(s => s.PcId == id && s.EndedAtUtc == null, ct);
        var unlocked = await UnlockPcAsync(db, pc, active, now, ct);
        await transaction.CommitAsync(ct);

        return unlocked ? Results.NoContent() : InUseResponse();
    }

    // Locks each listed PC and pauses its running session. Returns how many PCs were newly locked.
    private static async Task<int> LockPcsAsync(AppDbContext db, int[] pcIds, DateTimeOffset now, CancellationToken ct)
    {
        var newlyLocked = await db.Pcs
            .Where(p => pcIds.Contains(p.Id) && p.LockedAtUtc == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(p => p.LockedAtUtc, now), ct);
        await SessionLifecycle.PauseForPcsAsync(db, pcIds, now, ct);
        return newlyLocked;
    }

    // Returns false when the PC can't be opened: an active session holds it, or a login wins the race for it.
    // A locked PC is released only once its session is resumed or an admin unlock is opened, so a failed open keeps the lock.
    private static async Task<bool> UnlockPcAsync(AppDbContext db, Pc pc, PcSession? active, DateTimeOffset now, CancellationToken ct)
    {
        if (pc.LockedAtUtc is null)
        {
            return active is null && await TryOpenAdminUnlockAsync(db, pc, now, ct);
        }

        if (active is not null)
        {
            await SessionLifecycle.ResumeAsync(db, active, now, ct);
        }
        else if (!await TryOpenAdminUnlockAsync(db, pc, now, ct))
        {
            return false;
        }

        await db.Pcs
            .Where(p => p.Id == pc.Id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(p => p.LockedAtUtc, (DateTimeOffset?)null), ct);
        return true;
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
        }
        catch (DbUpdateException exception) when (ClientEndpoints.IsUniqueViolation(exception))
        {
            db.Entry(session).State = EntityState.Detached;
            return false;
        }

        // The PC is now open, so an older login request from it must not open a second session later.
        await LoginRequestLifecycle.CancelPendingForPcAsync(db, pc.Id, LoginRequestLifecycle.LoggedIn, now, ct);
        return true;
    }

    // Most PCs one bulk request may name. The lab has 60, so this only keeps a single request bounded.
    private const int MaxIdsPerRequest = 1000;

    private static async Task<IResult> LockManyAsync(HttpContext http, AppDbContext db, TimeProvider clock)
    {
        var pcIds = await ReadPcIdsAsync(http.Request);
        if (pcIds is null)
        {
            return AuthEndpoints.InvalidRequest();
        }

        // The same rules as the single lock, for every listed PC, in one transaction. Offline and unknown PCs are skipped.
        var ct = http.RequestAborted;
        var now = clock.GetUtcNow();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await SessionLifecycle.EndExpiredAsync(db, now, ct);
        var onlineIds = (await db.Pcs
            .AsNoTracking()
            .Where(p => pcIds.Contains(p.Id))
            .Select(p => new { p.Id, p.LastSeenUtc })
            .ToListAsync(ct))
            .Where(p => IsOnline(p.LastSeenUtc, now))
            .Select(p => p.Id)
            .ToArray();
        var locked = await LockPcsAsync(db, onlineIds, now, ct);
        await transaction.CommitAsync(ct);
        return Results.Ok(new AffectedPcsResponse(locked));
    }

    private static async Task<IResult> UnlockManyAsync(HttpContext http, AppDbContext db, TimeProvider clock)
    {
        var pcIds = await ReadPcIdsAsync(http.Request);
        if (pcIds is null)
        {
            return AuthEndpoints.InvalidRequest();
        }

        var ct = http.RequestAborted;
        var now = clock.GetUtcNow();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await SessionLifecycle.EndExpiredAsync(db, now, ct);
        var pcs = await db.Pcs.AsNoTracking().Where(p => pcIds.Contains(p.Id)).ToListAsync(ct);
        var activeSessions = (await db.PcSessions
            .AsNoTracking()
            .Where(s => s.EndedAtUtc == null && pcIds.Contains(s.PcId))
            .ToListAsync(ct))
            .ToDictionary(s => s.PcId);

        // Offline PCs are skipped. A PC that loses a race for a session is skipped too, and the others still open.
        var unlocked = 0;
        foreach (var pc in pcs)
        {
            if (IsOnline(pc.LastSeenUtc, now)
                && await UnlockPcAsync(db, pc, activeSessions.GetValueOrDefault(pc.Id), now, ct))
            {
                unlocked++;
            }
        }

        await transaction.CommitAsync(ct);
        return Results.Ok(new AffectedPcsResponse(unlocked));
    }

    // Null for a missing or malformed body, an empty list or one over the limit. Duplicate ids count once.
    private static async Task<int[]?> ReadPcIdsAsync(HttpRequest request)
    {
        var ids = (await AuthEndpoints.ReadBodyAsync<PcIdsRequest>(request))?.Ids;
        return ids is { Length: > 0 and <= MaxIdsPerRequest } ? ids.Distinct().ToArray() : null;
    }

    internal static IResult PcNotFound() =>
        Results.Json(new ErrorResponse("PC not found."), statusCode: StatusCodes.Status404NotFound);

    private static IResult InUseResponse() => ConflictResponse("This PC is already in use.");

    internal static IResult ConflictResponse(string message) =>
        Results.Json(new ErrorResponse(message), statusCode: StatusCodes.Status409Conflict);
}
