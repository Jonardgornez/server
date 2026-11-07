using LabGuard.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace LabGuard.Server.Pcs;

// Messages are kept for this long, then removed at startup.
public static class PcMessageRetention
{
    public static readonly TimeSpan KeepFor = TimeSpan.FromDays(90);

    // A message created exactly KeepFor ago is kept. Returns how many were removed.
    public static Task<int> DeleteOlderThanAsync(AppDbContext db, DateTimeOffset now, CancellationToken ct) =>
        db.PcMessages
            .Where(m => m.CreatedUtc < now - KeepFor)
            .ExecuteDeleteAsync(ct);
}
