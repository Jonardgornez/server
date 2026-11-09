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

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await CreateOrReturnPendingAsync(http, db, pcId, clock.GetUtcNow(), ct);
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

    // One transaction. It starts with the expiry write, which takes the database lock, so two requests from the same PC
    // can't both see no pending request and both insert one. The unique index is the backstop for that.
    private static async Task<IResult> CreateOrReturnPendingAsync(HttpContext http, AppDbContext db, int pcId, DateTimeOffset now, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await SessionLifecycle.EndExpiredForPcAsync(db, pcId, now, ct);
        var result = await CreateIfAllowedAsync(http, db, pcId, now, ct);
        await transaction.CommitAsync(ct);
        return result;
    }

    private static async Task<IResult> CreateIfAllowedAsync(HttpContext http, AppDbContext db, int pcId, DateTimeOffset now, CancellationToken ct)
    {
        // Asking again while one is pending returns that request, without a new row and without the cooldown.
        var pending = await db.LoginRequests
            .AsNoTracking()
            .Where(r => r.PcId == pcId && r.Status == LoginRequestStatus.Pending)
            .Select(r => new { r.Id, r.RequestedAtUtc })
            .FirstOrDefaultAsync(ct);
        if (pending is not null)
        {
            return Results.Ok(new ClientLoginRequestResponse(pending.Id, LoginRequestLifecycle.WireStatus(LoginRequestStatus.Pending), pending.RequestedAtUtc));
        }

        var pc = await db.Pcs
            .AsNoTracking()
            .Where(p => p.Id == pcId)
            .Select(p => new { p.Name, p.LockedAtUtc })
            .FirstOrDefaultAsync(ct);
        if (pc is null)
        {
            return NotRegistered();
        }

        if (pc.LockedAtUtc is not null)
        {
            return LockedByAdministrator();
        }

        // Expired sessions were closed above, so any session still open holds the PC.
        if (await db.PcSessions.AnyAsync(s => s.PcId == pcId && s.EndedAtUtc == null, ct))
        {
            return PcEndpoints.ConflictResponse(InUseMessage);
        }

        var latestRequestedAt = await db.LoginRequests
            .AsNoTracking()
            .Where(r => r.PcId == pcId)
            .OrderByDescending(r => r.RequestedAtUtc)
            .Select(r => (DateTimeOffset?)r.RequestedAtUtc)
            .FirstOrDefaultAsync(ct);
        if (latestRequestedAt is { } last && LoginRequestLifecycle.CooldownSecondsLeft(last, now) is { } seconds)
        {
            return CooldownResponse(http, seconds);
        }

        var request = new LoginRequest
        {
            PcId = pcId,
            PcName = pc.Name,
            RequestedAtUtc = now,
            Status = LoginRequestStatus.Pending,
        };
        db.LoginRequests.Add(request);
        await db.SaveChangesAsync(ct);
        return Results.Ok(new ClientLoginRequestResponse(request.Id, LoginRequestLifecycle.WireStatus(LoginRequestStatus.Pending), now));
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

    // This PC's most recent request, while it is pending or was decided within the report window. Null otherwise.
    // Only this PC's own requests are ever read, and the query uses the index on PcId and RequestedAtUtc.
    private static async Task<HeartbeatLoginRequest?> HeartbeatLoginRequestAsync(AppDbContext db, int pcId, DateTimeOffset now, CancellationToken ct)
    {
        var latest = await db.LoginRequests
            .AsNoTracking()
            .Where(r => r.PcId == pcId)
            .OrderByDescending(r => r.RequestedAtUtc)
            .ThenByDescending(r => r.Id)
            .Select(r => new { r.Id, r.Status, r.Reason, r.DecidedAtUtc })
            .FirstOrDefaultAsync(ct);
        if (latest is null)
        {
            return null;
        }

        var isReported = latest.Status == LoginRequestStatus.Pending
            || (latest.DecidedAtUtc is { } decidedAt && LoginRequestLifecycle.IsDecisionReported(decidedAt, now));
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
