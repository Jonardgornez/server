using LabGuard.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace LabGuard.Server.Requests;

// Why a login request is closed without an approval, and the rules a PC's requests follow.
// The reasons are stored and sent to the PC as they are, so these strings must not change.
public static class LoginRequestLifecycle
{
    public const string PcOffline = "pc-offline";
    public const string PcLocked = "pc-locked";
    public const string PcInUse = "pc-in-use";
    public const string PcRemoved = "pc-removed";
    // A request with no PC on it, from an older version or a development sample. It can't be approved.
    public const string PcUnknown = "pc-unknown";
    public const string LoggedIn = "logged-in";
    public const string CancelledByPc = "cancelled-by-pc";
    public const string Expired = "expired";

    // A PC may send another request only this long after its latest one, whatever became of that one.
    public static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(30);

    // A decision stays on the PC's heartbeat for this long after it was made.
    public static readonly TimeSpan DecisionReportWindow = TimeSpan.FromMinutes(2);

    // A pending request expires this long after it was asked, so a student who walked away can't be let in later.
    public static readonly TimeSpan PendingLifetime = TimeSpan.FromMinutes(10);

    public static string WireStatus(LoginRequestStatus status) => status.ToString().ToLowerInvariant();

    // A request exactly PendingLifetime old is expired.
    public static bool IsExpired(DateTimeOffset requestedAt, DateTimeOffset now) => now - requestedAt >= PendingLifetime;

    // The moment a pending request expired. It is recorded as the decision time, not the moment the expiry was noticed.
    public static DateTimeOffset ExpiredAt(DateTimeOffset requestedAt) => requestedAt + PendingLifetime;

    // Expires every pending request past its lifetime, in one statement. Runs before the requests are read, so they show as history.
    public static Task<int> ExpireStaleAsync(AppDbContext db, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var cancelled = nameof(LoginRequestStatus.Cancelled);
        var pending = nameof(LoginRequestStatus.Pending);
        var lifetimeMilliseconds = (long)PendingLifetime.TotalMilliseconds;
        var cutoffMilliseconds = (now - PendingLifetime).ToUnixTimeMilliseconds();
        return db.Database.ExecuteSqlAsync($"""
            UPDATE "LoginRequests"
            SET "Status" = {cancelled}, "Reason" = {Expired}, "DecidedAtUtc" = "RequestedAtUtc" + {lifetimeMilliseconds}
            WHERE "Status" = {pending} AND "RequestedAtUtc" <= {cutoffMilliseconds}
            """, cancellationToken);
    }

    // Expires this PC's pending request if it is past its lifetime. Runs inside the caller's transaction.
    public static Task<int> ExpireStaleForPcAsync(AppDbContext db, int pcId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var cancelled = nameof(LoginRequestStatus.Cancelled);
        var pending = nameof(LoginRequestStatus.Pending);
        var lifetimeMilliseconds = (long)PendingLifetime.TotalMilliseconds;
        var cutoffMilliseconds = (now - PendingLifetime).ToUnixTimeMilliseconds();
        return db.Database.ExecuteSqlAsync($"""
            UPDATE "LoginRequests"
            SET "Status" = {cancelled}, "Reason" = {Expired}, "DecidedAtUtc" = "RequestedAtUtc" + {lifetimeMilliseconds}
            WHERE "Status" = {pending} AND "PcId" = {pcId} AND "RequestedAtUtc" <= {cutoffMilliseconds}
            """, cancellationToken);
    }

    // Approves a request only while it is still pending and unexpired at the moment of the write, so a request that aged past
    // its lifetime while the write waited for the database lock is not approved. The clock is read here, right before the write.
    // Returns how many were approved, which is 0 when the request expired or was decided.
    public static Task<int> ApproveIfFreshAsync(AppDbContext db, int requestId, int? minutes, TimeProvider clock, CancellationToken cancellationToken)
    {
        var writtenAt = clock.GetUtcNow();
        var cutoff = writtenAt - PendingLifetime;
        return db.LoginRequests
            .Where(r => r.Id == requestId && r.Status == LoginRequestStatus.Pending && r.RequestedAtUtc > cutoff)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(r => r.Status, LoginRequestStatus.Approved)
                .SetProperty(r => r.ApprovedMinutes, minutes)
                .SetProperty(r => r.DecidedAtUtc, writtenAt), cancellationToken);
    }

    // Whole seconds the PC must still wait, rounded up so it never retries early. Null when it may send a request now.
    public static int? CooldownSecondsLeft(DateTimeOffset latestRequestedAt, DateTimeOffset now)
    {
        var left = latestRequestedAt + Cooldown - now;
        return left > TimeSpan.Zero ? (int)Math.Ceiling(left.TotalSeconds) : null;
    }

    public static bool IsDecisionReported(DateTimeOffset decidedAt, DateTimeOffset now) => now - decidedAt < DecisionReportWindow;

    // Cancels the PC's pending request, if it has one. Runs inside the caller's transaction.
    public static Task<int> CancelPendingForPcAsync(AppDbContext db, int pcId, string reason, DateTimeOffset now, CancellationToken cancellationToken) =>
        db.LoginRequests
            .Where(r => r.PcId == pcId && r.Status == LoginRequestStatus.Pending)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(r => r.Status, LoginRequestStatus.Cancelled)
                .SetProperty(r => r.Reason, reason)
                .SetProperty(r => r.DecidedAtUtc, now), cancellationToken);
}
