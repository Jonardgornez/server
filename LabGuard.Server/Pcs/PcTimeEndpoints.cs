using LabGuard.Server.Auth;
using LabGuard.Server.Data;
using LabGuard.Server.Sessions;
using Microsoft.EntityFrameworkCore;

namespace LabGuard.Server.Pcs;

// Admin changes to the running session of a PC: add time, switch it to open time, or end it now.
// Only the session changes; the account's default time is never touched.
public static partial class PcEndpoints
{
    private const string AddTimeAction = "add-time";
    private const string OpenTimeAction = "open-time";
    private const string TimeOutAction = "time-out";

    private const int MinAddedMinutes = 1;
    private const int MaxAddedMinutes = 1440;

    private static async Task<IResult> AdjustTimeAsync(int id, HttpContext http, AppDbContext db, TimeProvider clock)
    {
        var request = await AuthEndpoints.ReadBodyAsync<PcTimeRequest>(http.Request);
        if (request is null)
        {
            return AuthEndpoints.InvalidRequest();
        }

        if (TimeRequestError(request.Action, request.Minutes) is { } error)
        {
            return error;
        }

        // The same order as the lock: closing expired sessions first, then the PC, then its session, all in one transaction.
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

        var session = await db.PcSessions.AsNoTracking().FirstOrDefaultAsync(s => s.PcId == id && s.EndedAtUtc == null, ct);
        if (session is null)
        {
            return ConflictResponse("This PC has no session.");
        }

        var rejection = await ApplyTimeActionAsync(db, session, request.Action!, WholeMinutes(request.Minutes) ?? 0, now, ct);
        if (rejection is not null)
        {
            return rejection;
        }

        await transaction.CommitAsync(ct);
        return Results.NoContent();
    }

    // Sent by the bulk time route. Action and Minutes are checked the same way as PcTimeRequest.
    private static async Task<IResult> AdjustTimeManyAsync(HttpContext http, AppDbContext db, TimeProvider clock)
    {
        var request = await AuthEndpoints.ReadBodyAsync<PcTimeManyRequest>(http.Request);
        var pcIds = ValidPcIds(request?.Ids);
        if (request is null || pcIds is null)
        {
            return AuthEndpoints.InvalidRequest();
        }

        if (TimeRequestError(request.Action, request.Minutes) is { } error)
        {
            return error;
        }

        // The same rules as the single route, for every listed PC, in one transaction. Offline PCs, PCs without a session
        // and unknown ids are skipped. A PC whose time can't change is skipped too, and nothing is written for it.
        var ct = http.RequestAborted;
        var now = clock.GetUtcNow();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await SessionLifecycle.EndExpiredAsync(db, now, ct);
        var onlinePcIds = (await db.Pcs
            .AsNoTracking()
            .Where(p => pcIds.Contains(p.Id))
            .Select(p => new { p.Id, p.LastSeenUtc })
            .ToListAsync(ct))
            .Where(p => IsOnline(p.LastSeenUtc, now))
            .Select(p => p.Id)
            .ToHashSet();
        var activeSessions = await db.PcSessions
            .AsNoTracking()
            .Where(s => s.EndedAtUtc == null && pcIds.Contains(s.PcId))
            .ToListAsync(ct);

        // minutes is only read for add-time, which was checked above.
        var minutes = WholeMinutes(request.Minutes) ?? 0;
        var affected = 0;
        foreach (var session in activeSessions.Where(s => onlinePcIds.Contains(s.PcId)))
        {
            if (await ApplyTimeActionAsync(db, session, request.Action!, minutes, now, ct) is null)
            {
                affected++;
            }
        }

        await transaction.CommitAsync(ct);
        return Results.Ok(new AffectedPcsResponse(affected));
    }

    // Null when the action and, for add-time, the minutes are valid. Otherwise the error to send.
    private static IResult? TimeRequestError(string? action, double? minutes)
    {
        if (action is not (AddTimeAction or OpenTimeAction or TimeOutAction))
        {
            return AuthEndpoints.BadRequest($"Action must be \"{AddTimeAction}\", \"{OpenTimeAction}\" or \"{TimeOutAction}\".");
        }

        if (action == AddTimeAction && WholeMinutes(minutes) is null)
        {
            return AuthEndpoints.BadRequest($"Minutes must be a whole number from {MinAddedMinutes} to {MaxAddedMinutes}.");
        }

        return null;
    }

    // Returns the error to send, or null once the action has been applied. Nothing is written for an error.
    private static async Task<IResult?> ApplyTimeActionAsync(
        AppDbContext db,
        PcSession session,
        string action,
        int minutes,
        DateTimeOffset now,
        CancellationToken ct)
    {
        if (action == AddTimeAction)
        {
            if (!SessionLifecycle.FitsMaxTimeLeft(session, minutes, now))
            {
                return AuthEndpoints.BadRequest("A session can have at most 24 hours left.");
            }

            await SessionLifecycle.AddTimeAsync(db, session, minutes, now, ct);
            return null;
        }

        if (action == OpenTimeAction)
        {
            await SessionLifecycle.SetOpenTimeAsync(db, session.Id, ct);
            return null;
        }

        // A paused session has no running time to end, so it must be unlocked first.
        if (session.PausedAtUtc is not null)
        {
            return ConflictResponse("Unlock this PC before timing it out.");
        }

        await SessionLifecycle.EndSessionAsync(db, session.Id, SessionLifecycle.TimeExpired, now, ct);
        return null;
    }

    // Null unless the value is a whole number of minutes in range. A fraction, NaN or infinity is refused here.
    private static int? WholeMinutes(double? minutes) =>
        minutes is { } value
        && double.IsFinite(value)
        && value == Math.Floor(value)
        && value >= MinAddedMinutes
        && value <= MaxAddedMinutes
            ? (int)value
            : null;
}
