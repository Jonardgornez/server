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
    public const string LoggedIn = "logged-in";
    public const string CancelledByPc = "cancelled-by-pc";

    // A PC may send another request only this long after its latest one, whatever became of that one.
    public static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(30);

    // A decision stays on the PC's heartbeat for this long after it was made.
    public static readonly TimeSpan DecisionReportWindow = TimeSpan.FromMinutes(2);

    public static string WireStatus(LoginRequestStatus status) => status.ToString().ToLowerInvariant();

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
