namespace LabGuard.Server.Clients;

// Wraps a file being sent to a PC and reports activity at most once per interval while it is read, so a long download keeps
// its transfer alive. A report that fails is logged and never breaks the download, but one that finds the transfer no longer
// in progress (cancelled or failed) stops it. Seeking passes through, so range requests work.
// After each read it also records how far into the file the download has got, at most once per ProgressInterval and always on
// the last read. The position is the stream's own, so a range request counts from where it started. A failed record is ignored.
internal sealed class ActivityReportingStream(
    Stream inner,
    TimeProvider clock,
    TimeSpan interval,
    Func<Task<bool>> reportActivity,
    Action<long> recordPosition,
    ILogger logger) : Stream
{
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(500);

    // The download start has just reported, so the first report while streaming is one interval later.
    private DateTimeOffset _lastReport = clock.GetUtcNow();
    // Set once a report finds the transfer no longer in progress. Every read after that fails too.
    private bool _isStopped;
    // Null until the first read, so the first position is recorded at once.
    private DateTimeOffset? _lastRecord;

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
        var read = await inner.ReadAsync(buffer, cancellationToken);
        RecordPositionIfDue(isLastRead: read == 0 && buffer.Length > 0);
        return read;
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
        if (_isStopped)
        {
            throw NoLongerTransferred();
        }

        var now = clock.GetUtcNow();
        if (now - _lastReport < interval)
        {
            return;
        }

        _lastReport = now;
        bool isInProgress;
        try
        {
            isInProgress = await reportActivity();
        }
        catch (OperationCanceledException)
        {
            // The PC hung up mid-download; nothing to record.
            return;
        }
        catch (Exception exception)
        {
            logger.LogWarning("Couldn't record download activity on a sent file: {ExceptionType}.", exception.GetType().Name);
            return;
        }

        if (!isInProgress)
        {
            _isStopped = true;
            throw NoLongerTransferred();
        }
    }

    private void RecordPositionIfDue(bool isLastRead)
    {
        try
        {
            var position = inner.Position;
            var now = clock.GetUtcNow();
            var isLast = isLastRead || position >= inner.Length;
            if (!isLast && _lastRecord is { } last && now - last < ProgressInterval)
            {
                return;
            }

            _lastRecord = now;
            recordPosition(position);
        }
        catch (Exception exception)
        {
            logger.LogWarning("Couldn't record download progress on a sent file: {ExceptionType}.", exception.GetType().Name);
        }
    }

    private static IOException NoLongerTransferred() => new("The sent file is no longer being transferred.");
}
