namespace LabGuard.Server.Clients;

// Wraps a file being sent to a PC and reports activity at most once per interval while it is read, so a long download keeps
// its transfer alive. A failed report is logged and never breaks the download. Seeking passes through, so range requests work.
internal sealed class ActivityReportingStream(
    Stream inner,
    TimeProvider clock,
    TimeSpan interval,
    Func<Task> reportActivity,
    ILogger logger) : Stream
{
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromMinutes(1);

    // The download start has just reported, so the first report while streaming is one interval later.
    private DateTimeOffset _lastReport = clock.GetUtcNow();

    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => false;
    public override long Length => inner.Length;
    public override long Position
    {
        get => inner.Position;
        set => inner.Position = value;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await ReportIfDueAsync();
        return await inner.ReadAsync(buffer, cancellationToken);
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    // The server writes responses asynchronously, so the synchronous path only passes through.
    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

    public override void Flush()
    {
    }

    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await inner.DisposeAsync();
        await base.DisposeAsync();
    }

    private async Task ReportIfDueAsync()
    {
        var now = clock.GetUtcNow();
        if (now - _lastReport < interval)
        {
            return;
        }

        _lastReport = now;
        try
        {
            await reportActivity();
        }
        catch (OperationCanceledException)
        {
            // The PC hung up mid-download; nothing to record.
        }
        catch (Exception exception)
        {
            logger.LogWarning("Couldn't record download activity on a sent file: {ExceptionType}.", exception.GetType().Name);
        }
    }
}
