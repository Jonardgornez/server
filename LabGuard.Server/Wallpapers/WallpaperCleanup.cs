using LabGuard.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace LabGuard.Server.Wallpapers;

// Removes wallpapers that no PC refers to any more. It runs inside the requests that change or unlink a PC, so there is no
// background service: a wallpaper is never left behind by a removed PC, and an upload that is never given to a PC is removed
// once it is older than the retention.
internal static class WallpaperCleanup
{
    // An upload that no PC has been given within this time is removed.
    internal static readonly TimeSpan UnassignedRetention = TimeSpan.FromHours(1);

    // Removes the listed wallpapers that no PC refers to now. A listed wallpaper that a PC still uses is kept.
    internal static async Task RemoveUnusedAsync(AppDbContext db, int[] wallpaperIds, CancellationToken cancellationToken)
    {
        if (wallpaperIds.Length == 0)
        {
            return;
        }

        await db.Wallpapers
            .Where(w => wallpaperIds.Contains(w.Id) && !db.Pcs.Any(p => p.WallpaperId == w.Id))
            .ExecuteDeleteAsync(cancellationToken);
    }

    // Removes every upload older than the retention that no PC refers to.
    internal static async Task RemoveStaleUnassignedAsync(AppDbContext db, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var cutoff = now - UnassignedRetention;
        await db.Wallpapers
            .Where(w => w.CreatedAtUtc <= cutoff && !db.Pcs.Any(p => p.WallpaperId == w.Id))
            .ExecuteDeleteAsync(cancellationToken);
    }
}
