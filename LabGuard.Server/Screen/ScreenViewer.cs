using System.Security.Cryptography;
using System.Text;

namespace LabGuard.Server.Screen;

// One administrator's live view of one PC: the browser socket, the PC's socket while it is connected, the frame credits,
// the last state relayed and the lifetime that stops the relay loops. Everything mutable is guarded by one lock.
public sealed class ScreenViewer : IDisposable
{
    private readonly Lock _sync = new();
    private readonly Lifetime _lifetime = new();

    // Held around each state change and its send, so states reach the browser in the order they were recorded.
    public SemaphoreSlim StateGate { get; } = new(1, 1);
    private ScreenSocket? _pc;
    private int _framesInFlight;
    private string? _lastState;
    private bool _isClosed;

    public ScreenViewer(string viewId, int pcId, string mode, int auditId, DateTimeOffset sessionDeadline, ScreenSocket browser)
    {
        ViewId = viewId;
        PcId = pcId;
        Mode = mode;
        AuditId = auditId;
        SessionDeadline = sessionDeadline;
        Browser = browser;
    }

    public string ViewId { get; }
    public int PcId { get; }
    public string Mode { get; }
    public int AuditId { get; }

    // The time the administrator's session ends. The view closes then, because it never renews the session.
    public DateTimeOffset SessionDeadline { get; }
    public ScreenSocket Browser { get; }

    public ScreenSocket? CurrentPc
    {
        get
        {
            lock (_sync)
            {
                return _pc;
            }
        }
    }

    public bool IsCurrentPc(ScreenSocket socket)
    {
        lock (_sync)
        {
            return ReferenceEquals(_pc, socket);
        }
    }

    // A source linked to the view's lifetime. Null once the view is disposed. The caller disposes the one it gets.
    public CancellationTokenSource? LinkLifetime(CancellationToken cancellationToken) => _lifetime.Link(cancellationToken);

    public void CancelLifetime() => _lifetime.Cancel();

    public void Dispose() => _lifetime.Dispose();

    // Constant time, so a PC can't learn the view id one character at a time.
    public bool ViewIdMatches(string? candidate) =>
        candidate is not null && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(ViewId), Encoding.UTF8.GetBytes(candidate));

    // Attaches the PC's socket. The credit window starts again for the new connection. False once the view is closed.
    // The socket it replaces is returned, so the caller can end it.
    public bool TryAttachPc(ScreenSocket socket, out ScreenSocket? replaced)
    {
        lock (_sync)
        {
            replaced = _pc;
            if (_isClosed)
            {
                return false;
            }

            _pc = socket;
            _framesInFlight = 0;
            return true;
        }
    }

    // True only when this socket was the attached one, so a replaced socket leaving doesn't report the view as waiting.
    public bool TryDetachPc(ScreenSocket socket)
    {
        lock (_sync)
        {
            if (!ReferenceEquals(_pc, socket))
            {
                return false;
            }

            _pc = null;
            return true;
        }
    }

    // Takes one frame credit for the attached PC only. False when the window is full, the view is closed, or the sender was replaced.
    public bool TryTakeFrameCredit(ScreenSocket sender)
    {
        lock (_sync)
        {
            if (_isClosed || !ReferenceEquals(_pc, sender) || _framesInFlight >= ScreenProtocol.MaxFramesInFlight)
            {
                return false;
            }

            _framesInFlight++;
            return true;
        }
    }

    // Returns one credit. False when no credit was outstanding, so a stray or repeated ack isn't passed on.
    public bool TryReturnFrameCredit()
    {
        lock (_sync)
        {
            if (_framesInFlight == 0)
            {
                return false;
            }

            _framesInFlight--;
            return true;
        }
    }

    // Records a new state. True only when it differs from the last one relayed, so a repeated state is never sent twice.
    // When a PC is named, the change counts only if that PC is still the attached one.
    public bool TrySetState(string state, ScreenSocket? requiredPc = null)
    {
        lock (_sync)
        {
            if (_isClosed || (requiredPc is not null && !ReferenceEquals(_pc, requiredPc)) || state == _lastState)
            {
                return false;
            }

            _lastState = state;
            return true;
        }
    }

    // True for the first close only. The PC socket that was attached is handed back so it can be closed too.
    public bool TryClose(out ScreenSocket? pc)
    {
        lock (_sync)
        {
            if (_isClosed)
            {
                pc = null;
                return false;
            }

            _isClosed = true;
            pc = _pc;
            _pc = null;
            return true;
        }
    }
}
