using LabGuard.Server.Clients;
using LabGuard.Server.SharedFiles;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace LabGuard.Server.Tests;

// The stream a sent file is downloaded through reports activity about once a minute while it is read, so a long download
// keeps its transfer alive, and a failed report never breaks the download. It also records how far the download has got.
public class FileTransferActivityStreamTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    private static ActivityReportingStream StreamOver(
        byte[] bytes,
        FakeTimeProvider clock,
        Func<Task<bool>> report,
        Action<long>? recordPosition = null) =>
        new(new MemoryStream(bytes), clock, Interval, report, recordPosition ?? (_ => { }), NullLogger.Instance);

    private static Task<bool> StillInProgress() => Task.FromResult(true);

    [Fact]
    public async Task Read_BeforeTheInterval_DoesNotReport()
    {
        var clock = new FakeTimeProvider();
        var reports = 0;
        await using var stream = StreamOver(new byte[10], clock, () => { reports++; return Task.FromResult(true); });

        await stream.ReadExactlyAsync(new byte[4]);
        clock.Advance(TimeSpan.FromSeconds(59));
        await stream.ReadExactlyAsync(new byte[4]);

        Assert.Equal(0, reports);
    }

    [Fact]
    public async Task Read_ReportsAtMostOncePerInterval()
    {
        var clock = new FakeTimeProvider();
        var reports = 0;
        await using var stream = StreamOver(new byte[100], clock, () => { reports++; return Task.FromResult(true); });

        clock.Advance(Interval);
        await stream.ReadExactlyAsync(new byte[10]);
        await stream.ReadExactlyAsync(new byte[10]);
        clock.Advance(TimeSpan.FromSeconds(30));
        await stream.ReadExactlyAsync(new byte[10]);
        clock.Advance(TimeSpan.FromSeconds(30));
        await stream.ReadExactlyAsync(new byte[10]);

        Assert.Equal(2, reports);
    }

    [Fact]
    public async Task Read_AFailingReport_DoesNotBreakTheDownload()
    {
        var clock = new FakeTimeProvider();
        var bytes = new byte[] { 1, 2, 3, 4 };
        await using var stream = StreamOver(bytes, clock, () => throw new InvalidOperationException("database busy"));
        clock.Advance(Interval);

        var copy = new MemoryStream();
        await stream.CopyToAsync(copy);

        Assert.Equal(bytes, copy.ToArray());
    }

    // The transfer was cancelled or failed while the bytes streamed, so the download stops.
    [Fact]
    public async Task Read_WhenTheTransferIsNoLongerInProgress_StopsTheDownload()
    {
        var clock = new FakeTimeProvider();
        await using var stream = StreamOver(new byte[100], clock, () => Task.FromResult(false));

        await stream.ReadExactlyAsync(new byte[10]);
        clock.Advance(Interval);

        await Assert.ThrowsAsync<IOException>(() => stream.ReadExactlyAsync(new byte[10]).AsTask());
        await Assert.ThrowsAsync<IOException>(() => stream.CopyToAsync(new MemoryStream()));
    }

    [Fact]
    public async Task Stream_PassesSeekingThrough_ForRangeRequests()
    {
        var clock = new FakeTimeProvider();
        await using var stream = StreamOver([10, 20, 30, 40], clock, () => Task.FromResult(true));

        Assert.True(stream.CanSeek);
        Assert.Equal(4, stream.Length);
        stream.Seek(2, SeekOrigin.Begin);
        var buffer = new byte[2];
        var read = await stream.ReadAsync(buffer);

        Assert.Equal(2, read);
        Assert.Equal(new byte[] { 30, 40 }, buffer);
        Assert.Equal(4, stream.Position);
    }

    [Fact]
    public void Stream_IsReadOnly()
    {
        using var stream = StreamOver([1], new FakeTimeProvider(), () => Task.FromResult(true));

        Assert.False(stream.CanWrite);
        Assert.Throws<NotSupportedException>(() => stream.Write([1], 0, 1));
    }

    // Progress

    [Fact]
    public async Task Read_RecordsThePositionAtOnce_ThenAtMostEvery500Milliseconds()
    {
        var clock = new FakeTimeProvider();
        var recorded = new List<long>();
        await using var stream = StreamOver(new byte[100], clock, StillInProgress, recorded.Add);

        await stream.ReadExactlyAsync(new byte[10]);
        await stream.ReadExactlyAsync(new byte[10]);
        clock.Advance(TimeSpan.FromMilliseconds(499));
        await stream.ReadExactlyAsync(new byte[10]);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        await stream.ReadExactlyAsync(new byte[10]);

        Assert.Equal([10, 40], recorded);
    }

    [Fact]
    public async Task Read_TheLastRead_IsAlwaysRecorded()
    {
        var clock = new FakeTimeProvider();
        var recorded = new List<long>();
        await using var stream = StreamOver(new byte[10], clock, StillInProgress, recorded.Add);

        await stream.ReadExactlyAsync(new byte[4]);
        await stream.ReadExactlyAsync(new byte[4]);
        await stream.ReadExactlyAsync(new byte[2]);

        Assert.Equal([4, 10], recorded);
    }

    // A range request starts partway into the file, so the position counts from there.
    [Fact]
    public async Task Read_AfterASeek_RecordsTheAbsolutePosition()
    {
        var clock = new FakeTimeProvider();
        var recorded = new List<long>();
        await using var stream = StreamOver(new byte[100], clock, StillInProgress, recorded.Add);

        stream.Seek(60, SeekOrigin.Begin);
        await stream.ReadExactlyAsync(new byte[5]);

        Assert.Equal([65], recorded);
    }

    [Fact]
    public async Task Read_AFailingRecord_DoesNotBreakTheDownload()
    {
        var clock = new FakeTimeProvider();
        var bytes = new byte[] { 1, 2, 3, 4 };
        await using var stream = StreamOver(bytes, clock, StillInProgress, _ => throw new InvalidOperationException("store broken"));

        var copy = new MemoryStream();
        await stream.CopyToAsync(copy);

        Assert.Equal(bytes, copy.ToArray());
    }

    // The progress store

    [Fact]
    public void Progress_KeepsTheFurthestPositionSeen()
    {
        var progress = new FileTransferProgress();

        progress.Record(7, 500);
        progress.Record(7, 200);
        progress.Record(8, 50);

        Assert.Equal(500, progress.BytesSent(7));
        Assert.Equal(50, progress.BytesSent(8));
        Assert.Null(progress.BytesSent(9));
    }

    [Fact]
    public void Progress_Clear_ForgetsOnlyTheNamedTransfers()
    {
        var progress = new FileTransferProgress();
        progress.Record(1, 10);
        progress.Record(2, 20);
        progress.Record(3, 30);

        progress.Clear(1);
        progress.Clear([2, 99]);

        Assert.Null(progress.BytesSent(1));
        Assert.Null(progress.BytesSent(2));
        Assert.Equal(30, progress.BytesSent(3));
        Assert.Equal([3], progress.TransferIds());
    }

    [Fact]
    public void Progress_AfterAClear_StartsAgainFromTheNextPosition()
    {
        var progress = new FileTransferProgress();
        progress.Record(1, 900);

        progress.Clear(1);
        progress.Record(1, 100);

        Assert.Equal(100, progress.BytesSent(1));
    }
}
