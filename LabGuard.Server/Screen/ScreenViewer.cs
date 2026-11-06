using System.Security.Cryptography;
using System.Text;

namespace LabGuard.Server.Screen;

// One administrator's live view of one PC: the browser socket, the PC's socket while it is connected, the frame credits
// and the lifetime that stops the relay loops. Everything mutable is guarded by one lock, so a close and an attach can't interleave.
public sealed class ScreenViewer
{
    private readonly Lock _sync = new();
    private readonly CancellationTokenSource _lifetime = new();
    private ScreenSocket? _pc;
    private int _framesInFlight;
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
    public CancellationToken Lifetime => _lifetime.Token;

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

    // Constant time, so a PC can't learn the view id one character at a time.
    public bool ViewIdMatches(string? candidate) =>
        candidate is not null && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(ViewId), Encoding.UTF8.GetBytes(candidate));

    // False once the view is closed. The socket it replaces is returned so the caller can close it.
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

    // Takes one frame credit. False when the window is full or the view is closed; the frame is then dropped.
    public bool TryTakeFrameCredit()
    {
        lock (_sync)
        {
            if (_isClosed || _framesInFlight >= ScreenProtocol.MaxFramesInFlight)
            {
                return false;
            }

            _framesInFlight++;
            return true;
        }
    }

    public void ReturnFrameCredit()
    {
        lock (_sync)
        {
            if (_framesInFlight > 0)
            {
                _framesInFlight--;
            }
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

    public void CancelLifetime() => _lifetime.Cancel();
}
