namespace LabGuard.Server.Screen;

// One administrator's live view of one PC: the browser socket, the stream of the PC it watches, the last state it was sent, and
// the lifetime that stops its receive loop. The PC side, frames and remote control live in the shared ScreenStream.
public sealed class ScreenViewer : IDisposable
{
    private readonly Lock _sync = new();
    private readonly Lifetime _lifetime = new();
    private readonly ScreenInputRateLimiter _controlRequests = new(ScreenProtocol.ControlRequestsPerSecond);
    // The waiting state is sent before the viewer is created, so it starts as the last state sent.
    private string? _lastState = ScreenProtocol.WaitingState;
    private bool _isClosed;

    public ScreenViewer(
        ScreenStream stream,
        string mode,
        int auditId,
        DateTimeOffset sessionDeadline,
        ScreenSocket browser,
        ScreenViewOwner owner,
        bool isPassive = false)
    {
        Stream = stream;
        Mode = mode;
        AuditId = auditId;
        SessionDeadline = sessionDeadline;
        Browser = browser;
        Owner = owner;
        IsPassive = isPassive;
    }

    public ScreenStream Stream { get; }
    public int PcId => Stream.PcId;

    // The mode this administrator asked for, and recorded in the view's audit row. The PC streams in the stream's mode.
    public string Mode { get; }
    public int AuditId { get; }

    // The time the administrator's session ends. The view closes then, because it never renews the session.
    public DateTimeOffset SessionDeadline { get; }
    public ScreenSocket Browser { get; }

    // The administrator who opened the view, and the session version it was opened under. The monitor ends the view if that session stops being current.
    public ScreenViewOwner Owner { get; }

    // A grid tile. Its place can be taken by a full view when the stream is full.
    public bool IsPassive { get; }

    // Copied into each control audit row, so the row survives a removed PC or account.
    public string AdminUserName => Owner.AdminUserName;
    public string PcName => Stream.PcName;

    // Off unless this viewer is the one asking for or holding control of the PC.
    public ScreenControlState ControlState => Stream.ControlStateFor(this);

    public bool IsClosed
    {
        get
        {
            lock (_sync)
            {
                return _isClosed;
            }
        }
    }

    // A source linked to the view's lifetime. Null once the view is disposed. The caller disposes the one it gets.
    public CancellationTokenSource? LinkLifetime(CancellationToken cancellationToken) => _lifetime.Link(cancellationToken);

    public void CancelLifetime() => _lifetime.Cancel();

    public void Dispose() => _lifetime.Dispose();

    // Records the state about to be sent. True only when it differs from the last one sent, so a repeated state is never sent twice.
    public bool TrySetState(string state)
    {
        lock (_sync)
        {
            if (_isClosed || state == _lastState)
            {
                return false;
            }

            _lastState = state;
            return true;
        }
    }

    // Takes one of the control requests this viewer may make per second. False when it has made its share for this second.
    public bool TryTakeControlRequestSlot(DateTimeOffset now) => _controlRequests.TryTake(now);

    // True for the first close only.
    public bool TryClose()
    {
        lock (_sync)
        {
            if (_isClosed)
            {
                return false;
            }

            _isClosed = true;
            return true;
        }
    }
}

// Who opened a view, and the session they opened it under. The session version is what the monitor checks while the view is open.
public readonly record struct ScreenViewOwner(int AdminId, int SessionVersion, string AdminUserName);
