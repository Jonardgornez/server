using System.Collections.Concurrent;

namespace LabGuard.Server.SharedFiles;

// How many bytes of each sent file have reached its PC, by transfer id, so the administrator sees how far a download has got.
// Kept in memory only: a restart forgets it, and the next read of the download records it again. A download that restarts or
// resumes keeps the furthest position seen, until the transfer is handed over again or finishes.
public sealed class FileTransferProgress
{
    private readonly ConcurrentDictionary<long, long> _bytesSent = new();

    public void Record(long transferId, long position)
    {
        if (position < 0)
        {
            return;
        }

        _bytesSent.AddOrUpdate(transferId, position, (_, seen) => Math.Max(seen, position));
    }

    public long? BytesSent(long transferId) => _bytesSent.TryGetValue(transferId, out var bytes) ? bytes : null;

    public void Clear(long transferId) => _bytesSent.TryRemove(transferId, out _);

    public void Clear(IEnumerable<long> transferIds)
    {
        foreach (var transferId in transferIds)
        {
            Clear(transferId);
        }
    }

    public long[] TransferIds() => _bytesSent.Keys.ToArray();
}
