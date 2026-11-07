namespace LabGuard.Server.Pcs;

// Bounds message sends for the whole server. A send that finds both slots taken is refused at once, rather than queued,
// so a burst of sends can't hold connections open. Held in memory: a restart clears it.
public sealed class PcMessageSends
{
    public const int MaxInFlight = 2;

    // Taken for the whole of a send, including the wait for the PCs.
    public SemaphoreSlim InFlight { get; } = new(MaxInFlight, MaxInFlight);

    // Held only while checking the PCs and storing the message, never while waiting for them.
    public SemaphoreSlim Store { get; } = new(1, 1);
}
