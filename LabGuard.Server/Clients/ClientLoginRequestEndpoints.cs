using System.Globalization;
using LabGuard.Server.Auth;
using LabGuard.Server.Data;
using LabGuard.Server.Pcs;
using LabGuard.Server.Requests;
using LabGuard.Server.Sessions;
using Microsoft.EntityFrameworkCore;
using LoginRequest = LabGuard.Server.Data.LoginRequest;

namespace LabGuard.Server.Clients;

// Sent as {} by the PC. The body is required, so a cross-origin form can't send it.
public record ClientLoginRequestBody;
public record ClientLoginRequestResponse(int Id, string Status, DateTimeOffset RequestedAt);

// Reported on the heartbeat while the PC's latest request is pending, or was decided a moment ago.
public record HeartbeatLoginRequest(int Id, string Status, string? Reason);

// A PC at the lock screen asks the administrator to let a student log in. A PC has at most one pending request.
public static partial class ClientEndpoints
{
    private const int MaxLoginRequestAttempts = 2;
    private const string InUseMessage = "This PC is already in use.";

    // What decides a create: the PC's pending request, whether the PC exists and is locked, whether a session holds it,
    // and when it last asked. PcName is null when the PC no longer exists.
    private sealed record LoginRequestState(
        ClientLoginRequestResponse? Pending,
        string? PcName,
        bool IsLocked,
        bool IsInUse,
        DateTimeOffset? LatestRequestedAt);

    private static async Task<IResult> CreateLoginRequestAsync(HttpContext http, AppDbContext db, TimeProvider clock)
    {
        if (ReadBearerToken(http.Request) is not { } token)
        {
            return NotRegistered();
        }

        var ct = http.RequestAborted;
        if (await FindPcIdAsync(db, token, ct) is not { } pcId)
        {
            return NotRegistered();
        }

        if (await AuthEndpoints.ReadBodyAsync<ClientLoginRequestBody>(http.Request) is null)
        {
            return AuthEndpoints.InvalidRequest();
        }

        // Read-only first. A request that is already answered (pending, refused) never takes the database write lock.
        var now = clock.GetUtcNow();
        if (DecideWithoutInsert(http, await ReadStateAsync(db, pcId, now, ct), now) is { } answer)
        {
            return answer;
        }

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await CreateInTransactionAsync(http, db, pcId, now, ct);
            }
            catch (DbUpdateException exception) when (IsUniqueViolation(exception))
            {
                // Another request for this PC was stored at the same moment, and the unique index refused this one.
                // Starting over returns that request, unless it has been decided meanwhile.
                if (attempt == MaxLoginRequestAttempts)
                {
                    return ServerBusy();
                }

                db.ChangeTracker.Clear();
            }
        }
    }

    // The authoritative decision. It starts with the expiry write, which takes the database lock, so two requests from the
    // same PC can't both see no pending request and both insert one. The unique index is the backstop for that.
    private static async Task<IResult> CreateInTransactionAsync(HttpContext http, AppDbContext db, int pcId, DateTimeOffset now, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await SessionLifecycle.EndExpiredForPcAsync(db, pcId, now, ct);

        var state = await ReadStateAsync(db, pcId, now, ct);
        if (DecideWithoutInsert(http, state, now) is { } answer)
        {
            await transaction.CommitAsync(ct);
            return answer;
        }

        var request = new LoginRequest
        {
            PcId = pcId,
            PcName = state.PcName!,
            RequestedAtUtc = now,
            Status = LoginRequestStatus.Pending,
        };
        db.LoginRequests.Add(request);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Results.Ok(new ClientLoginRequestResponse(request.Id, LoginRequestLifecycle.WireStatus(LoginRequestStatus.Pending), now));
    }

    // Reads only. A session counts as holding the PC only while it is unexpired (or paused, which only a lock does).
    private static async Task<LoginRequestState> ReadStateAsync(AppDbContext db, int pcId, DateTimeOffset now, CancellationToken ct)
    {
        var pending = await db.LoginRequests
            .AsNoTracking()
            .Where(r => r.PcId == pcId && r.Status == LoginRequestStatus.Pending)
            .Select(r => new { r.Id, r.RequestedAtUtc })
            .FirstOrDefaultAsync(ct);

        var pc = await db.Pcs
            .AsNoTracking()
            .Where(p => p.Id == pcId)
            .Select(p => new { p.Name, p.LockedAtUtc })
            .FirstOrDefaultAsync(ct);

        var isInUse = await db.PcSessions.AnyAsync(
            s => s.PcId == pcId && s.EndedAtUtc == null && (s.PausedAtUtc != null || s.ExpiresAtUtc == null || s.ExpiresAtUtc > now),
            ct);

        var latestRequestedAt = await db.LoginRequests
            .AsNoTracking()
            .Where(r => r.PcId == pcId)
            .OrderByDescending(r => r.RequestedAtUtc)
            .Select(r => (DateTimeOffset?)r.RequestedAtUtc)
            .FirstOrDefaultAsync(ct);

        return new LoginRequestState(
            pending is null ? null : new ClientLoginRequestResponse(pending.Id, LoginRequestLifecycle.WireStatus(LoginRequestStatus.Pending), pending.RequestedAtUtc),
            pc?.Name,
            pc?.LockedAtUtc is not null,
            isInUse,
            latestRequestedAt);
    }

    // The answer when no request should be stored: the pending one, or the refusal. Null when a new request may be stored.
    private static IResult? DecideWithoutInsert(HttpContext http, LoginRequestState state, DateTimeOffset now)
    {
        if (state.Pending is { } pending)
        {
            // Asking again while one is pending returns that request, without a new row and without the cooldown.
            return Results.Ok(pending);
        }

        if (state.PcName is null)
        {
            return NotRegistered();
        }

        if (state.IsLocked)
        {
            return LockedByAdministrator();
        }

        if (state.IsInUse)
        {
            return PcEndpoints.ConflictResponse(InUseMessage);
        }

        if (state.LatestRequestedAt is { } last && LoginRequestLifecycle.CooldownSecondsLeft(last, now) is { } seconds)
        {
            return CooldownResponse(http, seconds);
        }

        return null;
    }

    // Only this PC's pending request changes. A PC with nothing pending gets the same 204.
    private static async Task<IResult> CancelLoginRequestAsync(HttpContext http, AppDbContext db, TimeProvider clock)
    {
        if (ReadBearerToken(http.Request) is not { } token)
        {
            return NotRegistered();
        }

        var ct = http.RequestAborted;
        if (await FindPcIdAsync(db, token, ct) is not { } pcId)
        {
            return NotRegistered();
        }

        if (await AuthEndpoints.ReadBodyAsync<ClientLoginRequestBody>(http.Request) is null)
        {
            return AuthEndpoints.InvalidRequest();
        }

        await LoginRequestLifecycle.CancelPendingForPcAsync(db, pcId, LoginRequestLifecycle.CancelledByPc, clock.GetUtcNow(), ct);
        return Results.NoContent();
    }

    // This PC's most recent request, while it is pending, or was decided within the report window and, for an approved one,
    // its session is still open or never ended. Null otherwise. Only this PC's own requests are read, through the PcId index.
    private static async Task<HeartbeatLoginRequest?> HeartbeatLoginRequestAsync(AppDbContext db, int pcId, DateTimeOffset now, CancellationToken ct)
    {
        var latest = await db.LoginRequests
            .AsNoTracking()
            .Where(r => r.PcId == pcId)
            .OrderByDescending(r => r.RequestedAtUtc)
            .ThenByDescending(r => r.Id)
            .Select(r => new
            {
                r.Id,
                r.Status,
                r.Reason,
                r.DecidedAtUtc,
                // An approved request is done once the session it opened has ended, however it ended.
                SessionEnded = db.PcSessions.Any(s => s.LoginRequestId == r.Id && s.EndedAtUtc != null),
            })
            .FirstOrDefaultAsync(ct);
        if (latest is null)
        {
            return null;
        }

        var isReported = latest.Status == LoginRequestStatus.Pending
            || (latest.DecidedAtUtc is { } decidedAt
                && LoginRequestLifecycle.IsDecisionReported(decidedAt, now)
                && !(latest.Status == LoginRequestStatus.Approved && latest.SessionEnded));
        return isReported
            ? new HeartbeatLoginRequest(latest.Id, LoginRequestLifecycle.WireStatus(latest.Status), latest.Reason)
            : null;
    }

    private static IResult CooldownResponse(HttpContext http, int seconds)
    {
        http.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
        return Results.Json(
            new RateLimitResponse($"You can send another request in {seconds} seconds.", seconds),
            statusCode: StatusCodes.Status429TooManyRequests);
    }
}
