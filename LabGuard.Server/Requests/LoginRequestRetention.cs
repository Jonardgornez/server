using LabGuard.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace LabGuard.Server.Requests;

// Decided login requests are kept for this long, then removed at startup. A pending request is never removed.
public static class LoginRequestRetention
{
    public static readonly TimeSpan KeepFor = TimeSpan.FromDays(90);

    // A request decided exactly KeepFor ago is kept. Returns how many were removed.
    public static Task<int> DeleteDecidedOlderThanAsync(AppDbContext db, DateTimeOffset now, CancellationToken ct) =>
        db.LoginRequests
            .Where(r => r.Status != LoginRequestStatus.Pending && r.DecidedAtUtc < now - KeepFor)
            .ExecuteDeleteAsync(ct);
}
