using System.Security.Cryptography;
using System.Text;

namespace LabGuard.Server.Screen;

// One administrator's live view of one PC: the browser socket, the PC's socket while it is connected, the frame credits,
// the last state relayed, the lifetime that stops the relay loops and the remote control state. Everything mutable is guarded by one lock.
public sealed class ScreenViewer : IDisposable
{
    private readonly Lock _sync = new();
    private readonly Lifetime _lifetime = new();
    private readonly ScreenInputRateLimiter _moves = new(ScreenProtocol.MaxMovesPerSecond);
    private readonly ScreenInputRateLimiter _otherInput = new(ScreenProtocol.MaxOtherInputPerSecond);

    // Held around each state change and its send, so states reach the browser in the order they were recorded.
    // Control changes take it too. Closing the view doesn't, so a close never waits behind a send that is stuck.
    public SemaphoreSlim StateGate { get; } = new(1, 1);
    private ScreenSocket? _pc;
    private int _framesInFlight;
    private string? _lastState;
    private bool _isClosed;
    private ScreenControlState _control = ScreenControlState.Off;
    private ScreenSocket? _controlPc;
    private long _controlRequestId;
    private int? _controlAuditId;

    public ScreenViewer(
        string viewId,
        int pcId,
        string mode,
        int auditId,
        DateTimeOffset sessionDeadline,
        ScreenSocket browser,
        string adminUserName,
        string pcName)
    {
        ViewId = viewId;
        PcId = pcId;
        Mode = mode;
        AuditId = auditId;
        SessionDeadline = sessionDeadline;
        Browser = browser;
        AdminUserName = adminUserName;
        PcName = pcName;
    }

    public string ViewId { get; }
    public int PcId { get; }
    public string Mode { get; }
    public int AuditId { get; }

    // The time the administrator's session ends. The view closes then, because it never renews the session.
    public DateTimeOffset SessionDeadline { get; }
    public ScreenSocket Browser { get; }

    // Copied into each control audit row, so the row survives a removed PC or account.
    public string AdminUserName { get; }
    public string PcName { get; }

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

    public ScreenControlState ControlState
    {
        get
        {
            lock (_sync)
            {
                return _control;
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

    // Starts a request for control of the attached PC. Null when the view is closed, control is already in use or no PC is attached.
    public ControlRequest? TryBeginControlRequest()
    {
        lock (_sync)
        {
            if (_isClosed || _control != ScreenControlState.Off || _pc is null)
            {
                return null;
            }

            _control = ScreenControlState.Requesting;
            _controlPc = _pc;
            _controlRequestId++;
            return new ControlRequest(_controlRequestId, _pc);
        }
    }

    // True while the PC is the one asked for control and the request is still waiting for its confirmation.
    public bool IsAwaitingConfirmation(ScreenSocket pc)
    {
        lock (_sync)
        {
            return IsAwaitingConfirmationLocked(pc);
        }
    }

    // Turns control on, with the audit row that records it. False when the request is no longer waiting for this PC, or the view closed.
    public bool TryConfirmControl(ScreenSocket pc, int auditId)
    {
        lock (_sync)
        {
            if (!IsAwaitingConfirmationLocked(pc))
            {
                return false;
            }

            _control = ScreenControlState.On;
            _controlAuditId = auditId;
            return true;
        }
    }

    // Ends whatever control period is in progress. Null when there is none. The PC is handed back only while it is still the attached one.
    public ControlPeriodEnd? TryEndControl()
    {
        lock (_sync)
        {
            return _control == ScreenControlState.Off ? null : EndControlLocked();
        }
    }

    // Ends the request only while it is still the one with this id, so a timer left over from an earlier request does nothing.
    public ControlPeriodEnd? TryExpireRequest(long requestId)
    {
        lock (_sync)
        {
            return _control == ScreenControlState.Requesting && _controlRequestId == requestId ? EndControlLocked() : null;
        }
    }

    // The attached PC that input is relayed to. Only while control is on, and only within the cap for this kind of input. Null otherwise.
    public ScreenSocket? TakeControlInputTarget(InputRate rate, DateTimeOffset now)
    {
        lock (_sync)
        {
            if (_isClosed || _control != ScreenControlState.On || !ReferenceEquals(_pc, _controlPc))
            {
                return null;
            }

            var allowed = rate switch
            {
                InputRate.Move => _moves.TryTake(now),
                InputRate.Other => _otherInput.TryTake(now),
                _ => true,
            };
            return allowed ? _pc : null;
        }
    }

    // Control ends with the view. The audit row it had is handed back, so the caller can end it.
    public int? EndControlOnClose()
    {
        lock (_sync)
        {
            return EndControlLocked().AuditId;
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

    private bool IsAwaitingConfirmationLocked(ScreenSocket pc) =>
        !_isClosed && _control == ScreenControlState.Requesting && ReferenceEquals(_pc, pc) && ReferenceEquals(_controlPc, pc);

    // The caller holds the lock.
    private ControlPeriodEnd EndControlLocked()
    {
        var ended = new ControlPeriodEnd(_controlAuditId, ReferenceEquals(_controlPc, _pc) ? _pc : null);
        _control = ScreenControlState.Off;
        _controlPc = null;
        _controlAuditId = null;
        return ended;
    }
}

public enum ScreenControlState
{
    Off,
    Requesting,
    On,
}

// A request for control that has started: its id, which a timer uses to tell it apart from a later request, and the PC asked.
public readonly record struct ControlRequest(long Id, ScreenSocket Pc);

// What a control period leaves behind when it ends: its audit row, and the PC to tell, which is null when the PC is no longer attached.
public readonly record struct ControlPeriodEnd(int? AuditId, ScreenSocket? AttachedPc);
