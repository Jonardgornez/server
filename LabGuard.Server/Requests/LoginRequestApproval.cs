using LabGuard.Server.Auth;
using LabGuard.Server.Clients;
using LabGuard.Server.Data;
using LabGuard.Server.Pcs;
using LabGuard.Server.Sessions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace LabGuard.Server.Requests;

// Approving a request opens a session on the PC. It runs under the PC login gate, so it can't race a student's login.
// A request that can't be approved is closed as denied with a reason, in a committed transaction, so the
// administrator's list and the PC both learn the outcome.
internal static class LoginRequestApproval
{
    private const int MaxAttempts = 2;
    private const string NotFoundMessage = "Login request not found.";
    private const string AlreadyDecidedMessage = "This request has already been decided.";
    private const string OpenFailedMessage = "Couldn't open that session. Try again.";
    private const string UnknownMessage = "This request came from an older version and can't be approved. Ask the student to request again.";
    private const string ExpiredMessage = "This request expired. Ask the student to request again.";

    private sealed record Refusal(LoginRequestStatus Status, string Reason, string Message);

    private static readonly Refusal ExpiredRefusal = new(LoginRequestStatus.Cancelled, LoginRequestLifecycle.Expired, ExpiredMessage);
    private static readonly Refusal UnknownRefusal = new(LoginRequestStatus.Denied, LoginRequestLifecycle.PcUnknown, UnknownMessage);
    private static readonly Refusal RemovedRefusal = new(
        LoginRequestStatus.Denied,
        LoginRequestLifecycle.PcRemoved,
        "That PC is no longer registered, so the request was closed.");
    private static readonly Refusal OfflineRefusal = new(
        LoginRequestStatus.Denied,
        LoginRequestLifecycle.PcOffline,
        "That PC is offline, so the request was closed.");
    private static readonly Refusal LockedRefusal = new(
        LoginRequestStatus.Denied,
        LoginRequestLifecycle.PcLocked,
        "That PC is locked. Unlock it first, then ask the student to request again.");
    private static readonly Refusal InUseRefusal = new(
        LoginRequestStatus.Denied,
        LoginRequestLifecycle.PcInUse,
        "Someone is already using that PC, so the request was closed.");

    public static async Task<IResult> ApproveAsync(AppDbContext db, int requestId, int? minutes, TimeProvider clock, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await ClientEndpoints.InGateAsync(() => OpenSessionAsync(db, requestId, minutes, clock, ct), ct);
            }
            catch (ServerBusyException)
            {
                return ClientEndpoints.ServerBusy();
            }
            catch (Exception exception) when (attempt < MaxAttempts && IsRetryable(exception))
            {
                // A session appeared, or the database was busy, between the reads and the write. Start over once,
                // the same way a student's login does.
                db.ChangeTracker.Clear();
            }
            catch (Exception exception) when (ClientStorageErrorFilter.IsDatabaseBusy(exception))
            {
                return ClientEndpoints.ServerBusy();
            }
            catch (DbUpdateException exception) when (ClientEndpoints.IsUniqueViolation(exception))
            {
                // Nothing was opened or closed, so the request stays pending for the administrator to try again.
                return Conflict(OpenFailedMessage);
            }
        }
    }

    private static bool IsRetryable(Exception exception) =>
        exception is DbUpdateException dbException && ClientEndpoints.IsUniqueViolation(dbException)
        || ClientStorageErrorFilter.IsDatabaseBusy(exception);

    private static async Task<IResult> OpenSessionAsync(AppDbContext db, int requestId, int? minutes, TimeProvider clock, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        var request = await db.LoginRequests
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == requestId, ct);
        if (request is null)
        {
            return Results.Json(new ErrorResponse(NotFoundMessage), statusCode: StatusCodes.Status404NotFound);
        }

        if (request.Status != LoginRequestStatus.Pending)
        {
            // A request the list already expired reads as expired, not as an ordinary decision.
            return request.Reason == LoginRequestLifecycle.Expired ? Conflict(ExpiredMessage) : AlreadyDecided();
        }

        // Checked before the PC, so a stale request is never opened, whatever state the PC is in.
        if (LoginRequestLifecycle.IsExpired(request.RequestedAtUtc, now))
        {
            return await CloseAsync(db, transaction, requestId, ExpiredRefusal, LoginRequestLifecycle.ExpiredAt(request.RequestedAtUtc), ct);
        }

        // A request with no PC predates the PC link (or is a development sample), so it can't be matched to any PC.
        if (request.PcId is not { } pcId)
        {
            return await CloseAsync(db, transaction, requestId, UnknownRefusal, now, ct);
        }

        var pc = await db.Pcs.AsNoTracking().FirstOrDefaultAsync(p => p.Id == pcId, ct);
        if (pc is null)
        {
            return await CloseAsync(db, transaction, requestId, RemovedRefusal, now, ct);
        }

        if (!PcEndpoints.IsOnline(pc.LastSeenUtc, now))
        {
            return await CloseAsync(db, transaction, requestId, OfflineRefusal, now, ct);
        }

        if (pc.LockedAtUtc is not null)
        {
            return await CloseAsync(db, transaction, requestId, LockedRefusal, now, ct);
        }

        // An expired session no longer holds the PC, so it is closed first and only a session still running blocks the approval.
        await SessionLifecycle.EndExpiredForPcAsync(db, pc.Id, now, ct);
        if (await db.PcSessions.AnyAsync(s => s.PcId == pc.Id && s.EndedAtUtc == null, ct))
        {
            return await CloseAsync(db, transaction, requestId, InUseRefusal, now, ct);
        }

        return await OpenAsync(db, transaction, requestId, pc, minutes, now, clock, ct);
    }

    private static async Task<IResult> OpenAsync(
        AppDbContext db,
        IDbContextTransaction transaction,
        int requestId,
        Pc pc,
        int? minutes,
        DateTimeOffset now,
        TimeProvider clock,
        CancellationToken ct)
    {
        db.PcSessions.Add(SessionLifecycle.NewLoginRequestSession(pc, requestId, minutes, now));
        await db.SaveChangesAsync(ct);

        if (await LoginRequestLifecycle.ApproveIfFreshAsync(db, requestId, minutes, clock, ct) == 0)
        {
            return await NotApprovedAsync(db, transaction, requestId, clock, ct);
        }

        var stored = await db.LoginRequests.AsNoTracking().SingleAsync(r => r.Id == requestId, ct);
        await transaction.CommitAsync(ct);
        return Results.Ok(LoginRequestEndpoints.ToResponse(stored));
    }

    // Nothing was approved, so the session opened above is rolled back. The request may have aged past its lifetime while the
    // write waited for the database lock; if so it is stored as expired, as the earlier check would have done.
    private static async Task<IResult> NotApprovedAsync(
        AppDbContext db,
        IDbContextTransaction transaction,
        int requestId,
        TimeProvider clock,
        CancellationToken ct)
    {
        await transaction.RollbackAsync(ct);
        db.ChangeTracker.Clear();
        await LoginRequestLifecycle.ExpireStaleAsync(db, clock.GetUtcNow(), ct);

        var request = await db.LoginRequests.AsNoTracking().SingleAsync(r => r.Id == requestId, ct);
        return request.Reason == LoginRequestLifecycle.Expired ? Conflict(ExpiredMessage) : AlreadyDecided();
    }

    // Closes a request that can't be approved. Committed, unlike the other paths, so the refusal is kept.
    private static async Task<IResult> CloseAsync(
        AppDbContext db,
        IDbContextTransaction transaction,
        int requestId,
        Refusal refusal,
        DateTimeOffset decidedAt,
        CancellationToken ct)
    {
        var closed = await db.LoginRequests
            .Where(r => r.Id == requestId && r.Status == LoginRequestStatus.Pending)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(r => r.Status, refusal.Status)
                .SetProperty(r => r.ApprovedMinutes, (int?)null)
                .SetProperty(r => r.Reason, refusal.Reason)
                .SetProperty(r => r.DecidedAtUtc, decidedAt), ct);
        if (closed == 0)
        {
            return AlreadyDecided();
        }

        await transaction.CommitAsync(ct);
        return Conflict(refusal.Message);
    }

    private static IResult AlreadyDecided() => Conflict(AlreadyDecidedMessage);

    private static IResult Conflict(string message) =>
        Results.Json(new ErrorResponse(message), statusCode: StatusCodes.Status409Conflict);
}
