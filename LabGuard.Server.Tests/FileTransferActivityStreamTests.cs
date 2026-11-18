using LabGuard.Server.Clients;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace LabGuard.Server.Tests;

// The stream a sent file is downloaded through reports activity about once a minute while it is read, so a long download
// keeps its transfer alive, and a failed report never breaks the download.
public class FileTransferActivityStreamTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    private static ActivityReportingStream StreamOver(byte[] bytes, FakeTimeProvider clock, Func<Task> report) =>
        new(new MemoryStream(bytes), clock, Interval, report, NullLogger.Instance);

    [Fact]
    public async Task Read_BeforeTheInterval_DoesNotReport()
    {
        var clock = new FakeTimeProvider();
        var reports = 0;
        await using var stream = StreamOver(new byte[10], clock, () => { reports++; return Task.CompletedTask; });

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
        await using var stream = StreamOver(new byte[100], clock, () => { reports++; return Task.CompletedTask; });

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

    [Fact]
    public async Task Stream_PassesSeekingThrough_ForRangeRequests()
    {
        var clock = new FakeTimeProvider();
        await using var stream = StreamOver([10, 20, 30, 40], clock, () => Task.CompletedTask);

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
        using var stream = StreamOver([1], new FakeTimeProvider(), () => Task.CompletedTask);

        Assert.False(stream.CanWrite);
        Assert.Throws<NotSupportedException>(() => stream.Write([1], 0, 1));
    }
}
