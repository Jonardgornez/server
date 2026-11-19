using LabGuard.Server.Data;

namespace LabGuard.Server.SharedFiles;

// Which transfers a PC is working on, as the heartbeat hand-over and the administrator's view both judge it. A transfer being
// downloaded or applied is active while it shows activity, and abandoned once it has shown none for SharedFileRules.AbandonedAfter.
public static class FileTransferQueries
{
    public static IQueryable<PcFileTransfer> Active(this IQueryable<PcFileTransfer> transfers, DateTimeOffset now)
    {
        var activeSince = now - SharedFileRules.AbandonedAfter;
        return transfers.Where(t => (t.Status == SharedFileRules.Downloading || t.Status == SharedFileRules.Applying)
            && t.UpdatedUtc > activeSince);
    }

    // Abandoned, but with hand-overs left and still within SharedFileRules.HandOverAgainLifetime: the PC's next heartbeat hands it over again.
    public static IQueryable<PcFileTransfer> ToHandOverAgain(this IQueryable<PcFileTransfer> transfers, DateTimeOffset now)
    {
        var handOverAgainSince = now - SharedFileRules.HandOverAgainLifetime;
        return transfers.Abandoned(now)
            .Where(t => t.HandOverCount < SharedFileRules.MaxHandOvers && t.CreatedUtc > handOverAgainSince);
    }

    // Abandoned after its last hand-over: the PC has stopped responding to it.
    public static IQueryable<PcFileTransfer> OutOfHandOvers(this IQueryable<PcFileTransfer> transfers, DateTimeOffset now) =>
        transfers.Abandoned(now).Where(t => t.HandOverCount >= SharedFileRules.MaxHandOvers);

    private static IQueryable<PcFileTransfer> Abandoned(this IQueryable<PcFileTransfer> transfers, DateTimeOffset now)
    {
        var activeSince = now - SharedFileRules.AbandonedAfter;
        return transfers.Where(t => (t.Status == SharedFileRules.Downloading || t.Status == SharedFileRules.Applying)
            && t.UpdatedUtc <= activeSince);
    }
}
