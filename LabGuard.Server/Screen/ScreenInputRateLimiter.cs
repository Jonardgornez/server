namespace LabGuard.Server.Screen;

// Caps one kind of input at a number of messages in any one second. A message over the cap is refused and not kept, so nothing queues.
// The window slides with each message, so the cap holds over every second, not only over calendar seconds.
public sealed class ScreenInputRateLimiter(int perSecond)
{
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(1);
    private readonly Lock _sync = new();
    private readonly Queue<DateTimeOffset> _taken = new();

    // True when a message may go through at this time. The caller passes the time from the clock.
    public bool TryTake(DateTimeOffset now)
    {
        lock (_sync)
        {
            while (_taken.Count > 0 && now - _taken.Peek() >= Window)
            {
                _taken.Dequeue();
            }

            if (_taken.Count >= perSecond)
            {
                return false;
            }

            _taken.Enqueue(now);
            return true;
        }
    }
}
