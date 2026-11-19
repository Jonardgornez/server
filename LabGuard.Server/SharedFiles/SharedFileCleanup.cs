using LabGuard.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace LabGuard.Server.SharedFiles;

// Applies the lifetimes of sent files: a transfer never handed over expires, one a PC stopped working on fails, old transfers
// are removed, and a shared file no transfer still needs loses its bytes. Runs at startup and then every few minutes.
public static class SharedFileCleanup
{
    public static async Task RunAsync(AppDbContext db, SharedFileStorage storage, DateTimeOffset now, CancellationToken ct)
    {
        await ExpireWaitingAsync(db, now, ct);
        await FailStuckAsync(db, now, ct);
        await DeleteOldTransfersAsync(db, now, ct);
        await RemoveUnusedFilesAsync(db, storage, now, ct);
        await RemoveOrphanFilesAsync(db, storage, now, ct);
        storage.SweepStaleTemps(now);
    }

    // A transfer still waiting after its lifetime is stored as expired. The heartbeat never hands such a transfer over.
    public static Task<int> ExpireWaitingAsync(AppDbContext db, DateTimeOffset now, CancellationToken ct)
    {
        var cutoff = now - SharedFileRules.WaitingLifetime;
        return db.PcFileTransfers
            .Where(t => t.Status == SharedFileRules.Waiting && t.CreatedUtc <= cutoff)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(t => t.Status, SharedFileRules.Expired)
                .SetProperty(t => t.UpdatedUtc, now), ct);
    }

    // A transfer a PC has been downloading or applying for too long fails. The status is matched again in the update, so a
    // report the PC sends at the same moment is never overwritten. Handing an abandoned transfer over again refreshes its
    // activity, but that stops SharedFileRules.HandOverAgainLifetime after it was created, so one that never finishes fails here.
    public static Task<int> FailStuckAsync(AppDbContext db, DateTimeOffset now, CancellationToken ct)
    {
        var cutoff = now - SharedFileRules.ActiveLifetime;
        return db.PcFileTransfers
            .Where(t => (t.Status == SharedFileRules.Downloading || t.Status == SharedFileRules.Applying) && t.UpdatedUtc <= cutoff)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(t => t.Status, SharedFileRules.Failed)
                .SetProperty(t => t.Error, SharedFileRules.StoppedRespondingError)
                .SetProperty(t => t.UpdatedUtc, now), ct);
    }

    public static Task<int> DeleteOldTransfersAsync(AppDbContext db, DateTimeOffset now, CancellationToken ct)
    {
        var cutoff = now - SharedFileRules.TransferLifetime;
        return db.PcFileTransfers.Where(t => t.CreatedUtc <= cutoff).ExecuteDeleteAsync(ct);
    }

    // Removes each shared file older than its lifetime that no transfer still needs, row first and then its bytes. Each row is
    // removed by a statement that checks the conditions again, so a send that just started using it keeps it.
    // Returns how many files were removed from disk. A file that couldn't be deleted is not counted; the orphan sweep retries it.
    public static Task<int> RemoveUnusedFilesAsync(AppDbContext db, SharedFileStorage storage, DateTimeOffset now, CancellationToken ct) =>
        storage.ExclusiveAsync(async () =>
        {
            var cutoff = now - SharedFileRules.UnusedFileLifetime;
            var candidates = await UnusedQuery(db, cutoff)
                .AsNoTracking()
                .Select(f => new { f.Id, f.Sha256 })
                .ToListAsync(ct);

            var removed = 0;
            foreach (var candidate in candidates)
            {
                var deleted = await UnusedQuery(db, cutoff).Where(f => f.Id == candidate.Id).ExecuteDeleteAsync(ct);
                if (deleted > 0 && SharedFileStorage.IsSha256(candidate.Sha256) && storage.TryDelete(storage.PathFor(candidate.Sha256)))
                {
                    removed++;
                }
            }

            return removed;
        }, ct);

    // Removes stored files that no shared file row names, left by a crash between moving a file in and storing its row, or by
    // a failed delete. Only files older than the unused lifetime go, and the check runs inside the storage gate, so an upload
    // that is just storing its row never loses its file. Returns how many files were actually deleted.
    public static Task<int> RemoveOrphanFilesAsync(AppDbContext db, SharedFileStorage storage, DateTimeOffset now, CancellationToken ct) =>
        storage.ExclusiveAsync(async () =>
        {
            var staleBefore = (now - SharedFileRules.UnusedFileLifetime).UtcDateTime;
            var oldFiles = storage.StoredFiles().Where(file => file.LastWriteUtc <= staleBefore).Select(file => file.Sha256).ToArray();
            if (oldFiles.Length == 0)
            {
                return 0;
            }

            var known = (await db.SharedFiles
                .AsNoTracking()
                .Where(f => oldFiles.Contains(f.Sha256))
                .Select(f => f.Sha256)
                .ToListAsync(ct))
                .ToHashSet(StringComparer.Ordinal);
            return oldFiles
                .Where(sha256 => !known.Contains(sha256))
                .Count(sha256 => storage.TryDelete(storage.PathFor(sha256)));
        }, ct);

    private static IQueryable<SharedFile> UnusedQuery(AppDbContext db, DateTimeOffset cutoff) =>
        db.SharedFiles.Where(f => f.CreatedUtc <= cutoff
            && !db.PcFileTransfers.Any(t => t.SharedFileId == f.Id && SharedFileRules.OpenStatuses.Contains(t.Status)));
}

// Runs the cleanup every few minutes. The first run is at startup, in Program.cs, so this one waits for its first tick.
public sealed class SharedFileCleanupService(
    IServiceScopeFactory scopes,
    SharedFileStorage storage,
    TimeProvider clock,
    ILogger<SharedFileCleanupService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The system timer, not the injected clock: the cleanup keeps its schedule even when a test moves the clock.
        using var timer = new PeriodicTimer(Interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                await SharedFileCleanup.RunAsync(db, storage, clock.GetUtcNow(), stoppingToken);
            }
            catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning("Couldn't clean up the sent files: {ExceptionType}.", exception.GetType().Name);
            }
        }
    }
}
