namespace LabGuard.Server.Screen;

// A cancellation source that is safe to link to, cancel and dispose from different threads. Every use takes the same lock,
// so a link can't be made to a source that is being disposed, and a cancel after disposal does nothing.
public sealed class Lifetime : IDisposable
{
    private readonly Lock _sync = new();
    private readonly CancellationTokenSource _source = new();
    private bool _isDisposed;

    public void Cancel()
    {
        lock (_sync)
        {
            if (!_isDisposed)
            {
                _source.Cancel();
            }
        }
    }

    // A source linked to this lifetime and to another token. Null once disposed. The caller disposes the one it gets.
    public CancellationTokenSource? Link(CancellationToken other)
    {
        lock (_sync)
        {
            return _isDisposed ? null : CancellationTokenSource.CreateLinkedTokenSource(other, _source.Token);
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (!_isDisposed)
            {
                _isDisposed = true;
                _source.Dispose();
            }
        }
    }
}
