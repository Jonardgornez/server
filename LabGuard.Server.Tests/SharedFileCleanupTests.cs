using LabGuard.Server.Data;
using LabGuard.Server.SharedFiles;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static LabGuard.Server.Tests.PcShutdownEndpointTests;
using static LabGuard.Server.Tests.SharedFileTestSupport;

namespace LabGuard.Server.Tests;

// The lifetimes of sent files: waiting transfers expire, stuck ones fail, old ones go, and unused files lose their bytes.
public class SharedFileCleanupTests
{
    private static Task<int> RunAsync(TestApp app, Func<AppDbContext, SharedFileStorage, DateTimeOffset, Task<int>> step) =>
        app.WithDbAsync(db => step(db, app.Services.GetRequiredService<SharedFileStorage>(), app.Time.GetUtcNow()));

    private static Task<bool> SharedFileExistsAsync(TestApp app, int id) =>
        app.WithDbAsync(db => db.SharedFiles.AnyAsync(f => f.Id == id));

    [Fact]
    public async Task ExpireWaiting_StoresExpired_OnlyForWaitingPastTheHour()
    {
        using var app = new TestApp();
        var pc = await AddPcAsync(app);
        var now = app.Time.GetUtcNow();
        var old = await SeedTransferAsync(app, pc.Id, "waiting", createdUtc: now - TimeSpan.FromHours(1));
        var fresh = await SeedTransferAsync(app, pc.Id, "waiting", createdUtc: now - TimeSpan.FromMinutes(59));
        var oldDownloading = await SeedTransferAsync(app, pc.Id, "downloading", createdUtc: now - TimeSpan.FromHours(2), updatedUtc: now);

        var changed = await RunAsync(app, (db, _, at) => SharedFileCleanup.ExpireWaitingAsync(db, at, CancellationToken.None));

        Assert.Equal(1, changed);
        Assert.Equal("expired", (await StoredTransferAsync(app, old)).Status);
        Assert.Equal("waiting", (await StoredTransferAsync(app, fresh)).Status);
        Assert.Equal("downloading", (await StoredTransferAsync(app, oldDownloading)).Status);
    }

    [Theory]
    [InlineData("downloading")]
    [InlineData("applying")]
    public async Task FailStuck_FailsATransferWorkedOnForAnHour(string status)
    {
        using var app = new TestApp();
        var pc = await AddPcAsync(app);
        var now = app.Time.GetUtcNow();
        var stuck = await SeedTransferAsync(app, pc.Id, status, createdUtc: now - TimeSpan.FromHours(2), updatedUtc: now - TimeSpan.FromHours(1));
        var busy = await SeedTransferAsync(app, pc.Id, status, createdUtc: now - TimeSpan.FromHours(2), updatedUtc: now - TimeSpan.FromMinutes(30));

        await RunAsync(app, (db, _, at) => SharedFileCleanup.FailStuckAsync(db, at, CancellationToken.None));

        var failed = await StoredTransferAsync(app, stuck);
        Assert.Equal("failed", failed.Status);
        Assert.Equal("PC stopped responding", failed.Error);
        Assert.Equal(status, (await StoredTransferAsync(app, busy)).Status);
    }

    [Fact]
    public async Task FailStuck_LeavesOtherStatusesAlone()
    {
        using var app = new TestApp();
        var pc = await AddPcAsync(app);
        var old = app.Time.GetUtcNow() - TimeSpan.FromHours(3);
        var exists = await SeedTransferAsync(app, pc.Id, "exists", createdUtc: old);
        var saved = await SeedTransferAsync(app, pc.Id, "saved", createdUtc: old);
        var cancelled = await SeedTransferAsync(app, pc.Id, "cancelled", createdUtc: old);

        var changed = await RunAsync(app, (db, _, at) => SharedFileCleanup.FailStuckAsync(db, at, CancellationToken.None));

        Assert.Equal(0, changed);
        Assert.Equal("exists", (await StoredTransferAsync(app, exists)).Status);
        Assert.Equal("saved", (await StoredTransferAsync(app, saved)).Status);
        Assert.Equal("cancelled", (await StoredTransferAsync(app, cancelled)).Status);
    }

    [Fact]
    public async Task ExpireWaiting_LeavesACancelledTransferCancelled()
    {
        using var app = new TestApp();
        var pc = await AddPcAsync(app);
        var cancelled = await SeedTransferAsync(app, pc.Id, "cancelled", createdUtc: app.Time.GetUtcNow() - TimeSpan.FromHours(2));

        var changed = await RunAsync(app, (db, _, at) => SharedFileCleanup.ExpireWaitingAsync(db, at, CancellationToken.None));

        Assert.Equal(0, changed);
        Assert.Equal("cancelled", (await StoredTransferAsync(app, cancelled)).Status);
    }

    [Fact]
    public async Task DeleteOldTransfers_RemovesThoseCreated24HoursAgo()
    {
        using var app = new TestApp();
        var pc = await AddPcAsync(app);
        var now = app.Time.GetUtcNow();
        var old = await SeedTransferAsync(app, pc.Id, "saved", createdUtc: now - TimeSpan.FromHours(24));
        var recent = await SeedTransferAsync(app, pc.Id, "saved", createdUtc: now - TimeSpan.FromHours(23));

        await RunAsync(app, (db, _, at) => SharedFileCleanup.DeleteOldTransfersAsync(db, at, CancellationToken.None));

        Assert.False(await TransferExistsAsync(app, old));
        Assert.True(await TransferExistsAsync(app, recent));
    }

    [Theory]
    [InlineData("saved")]
    [InlineData("cancelled")]
    public async Task RemoveUnusedFiles_RemovesAnOldFileWithOnlyFinishedTransfers_RowAndBytes(string finalStatus)
    {
        using var app = new TestApp();
        var pc = await AddPcAsync(app);
        var now = app.Time.GetUtcNow();
        var bytes = Bytes("finished");
        var fileId = await SeedSharedFileAsync(app, bytes, now - TimeSpan.FromHours(1));
        var transfer = await SeedTransferAsync(app, pc.Id, finalStatus, sharedFileId: fileId);

        var removed = await RunAsync(app, (db, storage, at) => SharedFileCleanup.RemoveUnusedFilesAsync(db, storage, at, CancellationToken.None));

        Assert.Equal(1, removed);
        Assert.False(await SharedFileExistsAsync(app, fileId));
        Assert.Empty(StoredFilesIn(app));
        // The finished transfer stays for the administrator, without its link to the removed file.
        Assert.Null((await StoredTransferAsync(app, transfer)).SharedFileId);
    }

    [Theory]
    [InlineData("waiting")]
    [InlineData("downloading")]
    [InlineData("exists")]
    [InlineData("applying")]
    public async Task RemoveUnusedFiles_KeepsAFileAnOpenTransferStillNeeds(string status)
    {
        using var app = new TestApp();
        var pc = await AddPcAsync(app);
        var bytes = Bytes("needed");
        var fileId = await SeedSharedFileAsync(app, bytes, app.Time.GetUtcNow() - TimeSpan.FromHours(5));
        await SeedTransferAsync(app, pc.Id, status, sharedFileId: fileId);

        var removed = await RunAsync(app, (db, storage, at) => SharedFileCleanup.RemoveUnusedFilesAsync(db, storage, at, CancellationToken.None));

        Assert.Equal(0, removed);
        Assert.True(await SharedFileExistsAsync(app, fileId));
        Assert.Equal([Sha256Of(bytes)], StoredFilesIn(app));
    }

    [Fact]
    public async Task RemoveUnusedFiles_KeepsAFileUploadedLessThanAnHourAgo()
    {
        using var app = new TestApp();
        var fileId = await SeedSharedFileAsync(app, Bytes("just uploaded"), app.Time.GetUtcNow() - TimeSpan.FromMinutes(59));

        await RunAsync(app, (db, storage, at) => SharedFileCleanup.RemoveUnusedFilesAsync(db, storage, at, CancellationToken.None));

        Assert.True(await SharedFileExistsAsync(app, fileId));
        Assert.Single(StoredFilesIn(app));
    }

    // A file still open, as by a PC's download, can't be deleted on Windows. Its row goes, but it is not counted as removed.
    [Fact]
    public async Task RemoveUnusedFiles_CountsOnlyFilesActuallyDeleted()
    {
        using var app = new TestApp();
        var bytes = Bytes("held open");
        var fileId = await SeedSharedFileAsync(app, bytes, app.Time.GetUtcNow() - TimeSpan.FromHours(2));
        var path = Path.Combine(app.SharedFilesFolder, Sha256Of(bytes));

        int removed;
        await using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            removed = await RunAsync(app, (db, storage, at) => SharedFileCleanup.RemoveUnusedFilesAsync(db, storage, at, CancellationToken.None));
        }

        Assert.Equal(0, removed);
        Assert.False(await SharedFileExistsAsync(app, fileId));
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task RemoveOrphanFiles_CountsOnlyFilesActuallyDeleted()
    {
        using var app = new TestApp();
        Directory.CreateDirectory(app.SharedFilesFolder);
        var path = Path.Combine(app.SharedFilesFolder, new string('f', 64));
        await File.WriteAllTextAsync(path, "x");
        File.SetLastWriteTimeUtc(path, app.Time.GetUtcNow().UtcDateTime - TimeSpan.FromHours(2));

        int removed;
        await using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            removed = await RunAsync(app, (db, storage, at) => SharedFileCleanup.RemoveOrphanFilesAsync(db, storage, at, CancellationToken.None));
        }

        Assert.Equal(0, removed);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task RemoveOrphanFiles_RemovesOldHashFilesWithNoRow_AndKeepsTheRest()
    {
        using var app = new TestApp();
        var storage = app.Services.GetRequiredService<SharedFileStorage>();
        var known = Bytes("known");
        await SeedSharedFileAsync(app, known, app.Time.GetUtcNow() - TimeSpan.FromHours(5));
        var oldOrphan = Path.Combine(app.SharedFilesFolder, new string('d', 64));
        var freshOrphan = Path.Combine(app.SharedFilesFolder, new string('e', 64));
        var otherFile = Path.Combine(app.SharedFilesFolder, "readme.txt");
        foreach (var path in new[] { oldOrphan, freshOrphan, otherFile })
        {
            await File.WriteAllTextAsync(path, "x");
        }

        var longAgo = app.Time.GetUtcNow().UtcDateTime - TimeSpan.FromHours(2);
        File.SetLastWriteTimeUtc(oldOrphan, longAgo);
        File.SetLastWriteTimeUtc(otherFile, longAgo);
        File.SetLastWriteTimeUtc(Path.Combine(app.SharedFilesFolder, Sha256Of(known)), longAgo);
        File.SetLastWriteTimeUtc(freshOrphan, app.Time.GetUtcNow().UtcDateTime - TimeSpan.FromMinutes(30));

        var removed = await RunAsync(app, (db, _, at) => SharedFileCleanup.RemoveOrphanFilesAsync(db, storage, at, CancellationToken.None));

        Assert.Equal(1, removed);
        Assert.False(File.Exists(oldOrphan));
        Assert.True(File.Exists(freshOrphan));
        Assert.True(File.Exists(otherFile));
        Assert.True(File.Exists(Path.Combine(app.SharedFilesFolder, Sha256Of(known))));
    }

    [Fact]
    public async Task Run_AppliesEveryLifetime()
    {
        using var app = new TestApp();
        var pc = await AddPcAsync(app);
        var now = app.Time.GetUtcNow();
        var fileId = await SeedSharedFileAsync(app, Bytes("only waiting"), now - TimeSpan.FromHours(2));
        // Waiting for two hours: it expires, so the file it was keeping is no longer needed.
        var waiting = await SeedTransferAsync(app, pc.Id, "waiting", createdUtc: now - TimeSpan.FromHours(2), sharedFileId: fileId);
        var stuck = await SeedTransferAsync(app, pc.Id, "applying", createdUtc: now - TimeSpan.FromHours(3), updatedUtc: now - TimeSpan.FromHours(2));
        var old = await SeedTransferAsync(app, pc.Id, "saved", createdUtc: now - TimeSpan.FromDays(2));
        var oldCancelled = await SeedTransferAsync(app, pc.Id, "cancelled", createdUtc: now - TimeSpan.FromDays(2));

        await app.WithDbAsync(async db =>
        {
            await SharedFileCleanup.RunAsync(db, app.Services.GetRequiredService<SharedFileStorage>(), now, CancellationToken.None);
            return 0;
        });

        Assert.Equal("expired", (await StoredTransferAsync(app, waiting)).Status);
        Assert.Equal("failed", (await StoredTransferAsync(app, stuck)).Status);
        Assert.False(await TransferExistsAsync(app, old));
        Assert.False(await TransferExistsAsync(app, oldCancelled));
        Assert.False(await SharedFileExistsAsync(app, fileId));
        Assert.Empty(StoredFilesIn(app));
    }
}
