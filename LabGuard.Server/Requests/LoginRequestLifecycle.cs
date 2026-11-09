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

    // Expires every pending request past its lifetime. Runs before the requests are read, so they show as history.
    public static Task<int> ExpireStaleAsync(AppDbContext db, DateTimeOffset now, CancellationToken cancellationToken) =>
        ExpireStalePendingAsync(db, db.LoginRequests, now, cancellationToken);

    // Expires this PC's pending request if it is past its lifetime. Runs inside the caller's transaction.
    public static Task<int> ExpireStaleForPcAsync(AppDbContext db, int pcId, DateTimeOffset now, CancellationToken cancellationToken) =>
        ExpireStalePendingAsync(db, db.LoginRequests.Where(r => r.PcId == pcId), now, cancellationToken);

    // SQLite can't add the lifetime to a stored time inside an update, so each stale row gets one conditional update
    // with its own expiry time. A row decided meanwhile (by an approval, say) no longer matches and is left alone.
    private static async Task<int> ExpireStalePendingAsync(
        AppDbContext db,
        IQueryable<LoginRequest> requests,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var stale = await requests
            .AsNoTracking()
            .Where(r => r.Status == LoginRequestStatus.Pending && r.RequestedAtUtc <= now - PendingLifetime)
            .Select(r => new { r.Id, r.RequestedAtUtc })
            .ToListAsync(cancellationToken);

        var expired = 0;
        foreach (var request in stale)
        {
            expired += await db.LoginRequests
                .Where(r => r.Id == request.Id && r.Status == LoginRequestStatus.Pending)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(r => r.Status, LoginRequestStatus.Cancelled)
                    .SetProperty(r => r.Reason, Expired)
                    .SetProperty(r => r.DecidedAtUtc, ExpiredAt(request.RequestedAtUtc)), cancellationToken);
        }

        return expired;
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
