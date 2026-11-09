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

    private sealed record Refusal(string Reason, string Message);

    private static readonly Refusal RemovedRefusal = new(
        LoginRequestLifecycle.PcRemoved,
        "That PC is no longer registered, so the request was closed.");
    private static readonly Refusal OfflineRefusal = new(
        LoginRequestLifecycle.PcOffline,
        "That PC is offline, so the request was closed.");
    private static readonly Refusal LockedRefusal = new(
        LoginRequestLifecycle.PcLocked,
        "That PC is locked. Unlock it first, then ask the student to request again.");
    private static readonly Refusal InUseRefusal = new(
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
            return AlreadyDecided();
        }

        // A request with no PC (the samples) or whose PC has gone is closed like any other refusal.
        var pc = request.PcId is { } pcId
            ? await db.Pcs.AsNoTracking().FirstOrDefaultAsync(p => p.Id == pcId, ct)
            : null;
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

        return await OpenAsync(db, transaction, requestId, pc, minutes, now, ct);
    }

    private static async Task<IResult> OpenAsync(
        AppDbContext db,
        IDbContextTransaction transaction,
        int requestId,
        Pc pc,
        int? minutes,
        DateTimeOffset now,
        CancellationToken ct)
    {
        db.PcSessions.Add(SessionLifecycle.NewLoginRequestSession(pc, requestId, minutes, now));
        await db.SaveChangesAsync(ct);

        var approved = await db.LoginRequests
            .Where(r => r.Id == requestId && r.Status == LoginRequestStatus.Pending)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(r => r.Status, LoginRequestStatus.Approved)
                .SetProperty(r => r.ApprovedMinutes, minutes)
                .SetProperty(r => r.DecidedAtUtc, now), ct);
        if (approved == 0)
        {
            // Not committed, so the session opened above is rolled back with it.
            return AlreadyDecided();
        }

        var stored = await db.LoginRequests.AsNoTracking().SingleAsync(r => r.Id == requestId, ct);
        await transaction.CommitAsync(ct);
        return Results.Ok(LoginRequestEndpoints.ToResponse(stored));
    }

    // Closes a request that can't be approved. Committed, unlike the other paths, so the refusal is kept.
    private static async Task<IResult> CloseAsync(
        AppDbContext db,
        IDbContextTransaction transaction,
        int requestId,
        Refusal refusal,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var closed = await db.LoginRequests
            .Where(r => r.Id == requestId && r.Status == LoginRequestStatus.Pending)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(r => r.Status, LoginRequestStatus.Denied)
                .SetProperty(r => r.ApprovedMinutes, (int?)null)
                .SetProperty(r => r.Reason, refusal.Reason)
                .SetProperty(r => r.DecidedAtUtc, now), ct);
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
