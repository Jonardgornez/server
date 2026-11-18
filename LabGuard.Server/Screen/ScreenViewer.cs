using System.Security.Cryptography;
using System.Text;

namespace LabGuard.Server.Screen;

// One administrator's live view of one PC: the browser socket, the PC's socket while it is connected, the secure-desktop helper
// while it is the source of frames, the frame credits, the last state relayed, the lifetime that stops the relay loops and the
// remote control state. Everything mutable is guarded by one lock.
public sealed class ScreenViewer : IDisposable
{
    private readonly Lock _sync = new();
    private readonly Lifetime _lifetime = new();
    private readonly ScreenInputRateLimiter _moves = new(ScreenProtocol.MaxMovesPerSecond);
    private readonly ScreenInputRateLimiter _otherInput = new(ScreenProtocol.MaxOtherInputPerSecond);
    private readonly ScreenInputRateLimiter _exemptInput = new(ScreenProtocol.MaxExemptInputPerSecond);
    private readonly ScreenInputRateLimiter _sasInput = new(ScreenProtocol.MaxSasPerSecond);
    private readonly ScreenInputRateLimiter _controlRequests = new(ScreenProtocol.ControlRequestsPerSecond);

    // The keys and buttons the server has relayed as down. An up or release is relayed only for one of these, and the set is cleared when control ends.
    private readonly HashSet<string> _held = [];

    // Held around each state change and its send, so states reach the browser in the order they were recorded.
    // Control changes take it too. Closing the view doesn't, so a close never waits behind a send that is stuck.
    public SemaphoreSlim StateGate { get; } = new(1, 1);

    // Held around choosing an input's target and sending it, and around the messages of a source switch and the helper's stop
    // on close. So input can't reach a source after it was released or stopped, and a helper's start and stop can't cross.
    // Taken after StateGate when both are needed, never before it.
    public SemaphoreSlim InputGate { get; } = new(1, 1);
    // Each frame the browser hasn't acknowledged yet, oldest first, with its sender. An ack goes back to the sender of the oldest one.
    private readonly Queue<InFlightFrame> _framesInFlight = new();
    private ScreenSocket? _pc;
    private bool _isPcPaused;
    // The secure-desktop helper while it streams in place of the paused PC. Null while the PC's own stream is the source.
    private ScreenSocket? _helper;
    // Counts the helper's streaming runs. A helper starts each run with a fresh window, so frames from an earlier run no longer count.
    private long _helperRun;
    private string? _lastState;
    private bool _isClosed;
    private ScreenControlState _control = ScreenControlState.Off;
    // The PC connection the control period belongs to. Control ends with that connection.
    private ScreenSocket? _controlPc;
    // The socket that carries control now: the one asked to confirm, and the one input goes to. The PC or the streaming helper.
    private ScreenSocket? _controlSource;
    private long _controlRequestId;
    private int? _controlAuditId;

    public ScreenViewer(
        string viewId,
        int pcId,
        string mode,
        int auditId,
        DateTimeOffset sessionDeadline,
        ScreenSocket browser,
        ScreenViewOwner owner,
        string pcName)
    {
        ViewId = viewId;
        PcId = pcId;
        Mode = mode;
        AuditId = auditId;
        SessionDeadline = sessionDeadline;
        Browser = browser;
        Owner = owner;
        PcName = pcName;
    }

    public string ViewId { get; }
    public int PcId { get; }
    public string Mode { get; }
    public int AuditId { get; }

    // The time the administrator's session ends. The view closes then, because it never renews the session.
    public DateTimeOffset SessionDeadline { get; }
    public ScreenSocket Browser { get; }

    // The administrator who opened the view, and the session version it was opened under. The monitor ends the view if that session stops being current.
    public ScreenViewOwner Owner { get; }

    // Copied into each control audit row, so the row survives a removed PC or account.
    public string AdminUserName => Owner.AdminUserName;
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

    // True while the socket is the source of frames: the helper streaming in the PC's place, or the PC when no helper does.
    public bool IsCurrentSource(ScreenSocket socket)
    {
        lock (_sync)
        {
            return !_isClosed && ReferenceEquals(_helper ?? _pc, socket);
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

    // Attaches the PC's socket. The credit window starts again for the new connection, which is not paused until it says so.
    // False once the view is closed. The socket it replaces is returned, so the caller can end it.
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
            _isPcPaused = false;
            _framesInFlight.Clear();
            return true;
        }
    }

    // Records whether the attached PC can capture its screen. False when the sender is not the attached PC.
    public bool TrySetPcPaused(ScreenSocket pc, bool isPaused)
    {
        lock (_sync)
        {
            if (_isClosed || !ReferenceEquals(_pc, pc))
            {
                return false;
            }

            _isPcPaused = isPaused;
            return true;
        }
    }

    // Makes the source of frames and input match the situation: the connected helper while the attached PC is paused, otherwise
    // the PC. Null when nothing changed. Otherwise it says what the hub must send: stop to the helper that was the source, start
    // to the one that is now, control on to the socket that carries control from now on, a release to the PC when control moves
    // away from it, and the browser's new state. Control carries across a switch, with its audit row. It ends only when the
    // helper that carried it went away while the PC is still paused, since nothing can take input then.
    public SourceSwitch? TrySwitchSource(ScreenSocket? connectedHelper)
    {
        lock (_sync)
        {
            if (_isClosed)
            {
                return null;
            }

            var next = _pc is not null && _isPcPaused ? connectedHelper : null;
            var state = StateForLocked(next);
            var stateToSend = state == _lastState ? null : state;
            _lastState = state;
            var previous = _helper;
            if (ReferenceEquals(previous, next))
            {
                return stateToSend is null ? null : new SourceSwitch(State: stateToSend);
            }

            _helper = next;
            if (next is not null)
            {
                _helperRun++;
            }

            if (_control == ScreenControlState.Off || _pc is null || !ReferenceEquals(_controlPc, _pc))
            {
                return new SourceSwitch(previous, next, State: stateToSend);
            }

            if (next is null && _isPcPaused)
            {
                return new SourceSwitch(previous, next, EndedControl: EndControlLocked(), State: stateToSend);
            }

            var releasedPc = previous is null && _control == ScreenControlState.On ? _pc : null;
            _controlSource = next ?? _pc;
            _held.Clear();
            return new SourceSwitch(previous, next, _controlSource, releasedPc, State: stateToSend);
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

    // Takes one frame credit for the current source only: the attached PC, or the helper while it streams in the PC's place.
    // Each sender has its own window, so frames the other source still has in flight around a switch don't cost it a credit.
    // False when the sender's window is full, the view is closed, or the sender is not the current source.
    public bool TryTakeFrameCredit(ScreenSocket sender)
    {
        lock (_sync)
        {
            if (_isClosed
                || !ReferenceEquals(_helper ?? _pc, sender)
                || _framesInFlight.Count >= ScreenProtocol.MaxFramesInFlightAcrossSwitch
                || _framesInFlight.Count(frame => ReferenceEquals(frame.Sender, sender) && IsCountedLocked(frame)) >= ScreenProtocol.MaxFramesInFlight)
            {
                return false;
            }

            _framesInFlight.Enqueue(new InFlightFrame(sender, _helper is null ? 0 : _helperRun));
            return true;
        }
    }

    // Returns one credit. False when no credit was outstanding, so a stray or repeated ack isn't passed on. The ack goes to the
    // sender of the oldest frame, while that frame still counts: the PC's while it is attached, the helper's during the same run.
    public bool TryReturnFrameCredit(out ScreenSocket? ackTarget)
    {
        lock (_sync)
        {
            ackTarget = null;
            if (!_framesInFlight.TryDequeue(out var frame))
            {
                return false;
            }

            ackTarget = IsCountedLocked(frame) ? frame.Sender : null;
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

    // Takes one of the control requests this viewer may make per second. False when it has made its share for this second.
    public bool TryTakeControlRequestSlot(DateTimeOffset now) => _controlRequests.TryTake(now);

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
            _controlSource = _helper ?? _pc;
            _controlRequestId++;
            return new ControlRequest(_controlRequestId, _controlSource);
        }
    }

    // True while the socket is the one asked for control and the request is still waiting for its confirmation.
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

    // Ends the request if it is still waiting for this socket. Null otherwise, so a request that moved on is left alone.
    public ControlPeriodEnd? TryEndRequestFrom(ScreenSocket pc)
    {
        lock (_sync)
        {
            return _control == ScreenControlState.Requesting && ReferenceEquals(_controlSource, pc) ? EndControlLocked() : null;
        }
    }

    // Ends control when the socket that carries it says it has ended. Null when that socket doesn't carry control, so the paused
    // PC can't end control that the helper carries.
    public ControlPeriodEnd? TryEndControlFrom(ScreenSocket source)
    {
        lock (_sync)
        {
            return _control != ScreenControlState.Off && ReferenceEquals(_controlSource, source) ? EndControlLocked() : null;
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

    // The socket the input is relayed to: the one that carries control, or for Ctrl+Alt+Del the connected helper when there is one.
    // Only while control is on, only for a key or button that is held when it must be, and only within the cap for its kind.
    // Null otherwise. A relayed input updates what the server holds down on the PC.
    public ScreenSocket? TakeControlInputTarget(BrowserControlMessage input, DateTimeOffset now, ScreenSocket? connectedHelper = null)
    {
        lock (_sync)
        {
            if (_isClosed || _control != ScreenControlState.On || !ReferenceEquals(_pc, _controlPc))
            {
                return null;
            }

            if (!HeldAllowsLocked(input) || !TakeRateLocked(input.Rate, now))
            {
                return null;
            }

            ApplyHeldLocked(input);
            return input.Rate == InputRate.Sas && connectedHelper is not null ? connectedHelper : _controlSource;
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

    // True for the first close only. The PC socket that was attached is handed back so it can be closed too, and the helper that
    // was streaming so it can be told to stop.
    public bool TryClose(out ScreenSocket? pc, out ScreenSocket? helper)
    {
        lock (_sync)
        {
            pc = null;
            helper = null;
            if (_isClosed)
            {
                return false;
            }

            _isClosed = true;
            pc = _pc;
            helper = _helper;
            _pc = null;
            _helper = null;
            return true;
        }
    }

    private bool IsAwaitingConfirmationLocked(ScreenSocket pc) =>
        !_isClosed
        && _control == ScreenControlState.Requesting
        && _pc is not null
        && ReferenceEquals(_controlPc, _pc)
        && ReferenceEquals(_controlSource, pc);

    private bool IsCountedLocked(InFlightFrame frame) =>
        ReferenceEquals(frame.Sender, _pc) || (ReferenceEquals(frame.Sender, _helper) && frame.HelperRun == _helperRun);

    private string StateForLocked(ScreenSocket? source) =>
        _pc is null ? ScreenProtocol.WaitingState
        : source is not null ? ScreenProtocol.SecureState
        : _isPcPaused ? ScreenProtocol.PausedState
        : ScreenProtocol.LiveState;

    // An up or release needs something held. A press needs room to be held, unless the same key is already held.
    private bool HeldAllowsLocked(BrowserControlMessage input) => input.Held switch
    {
        HeldEffect.Press => _held.Contains(input.HeldKey) || _held.Count < ScreenProtocol.MaxHeldInputs,
        HeldEffect.Release => _held.Contains(input.HeldKey),
        HeldEffect.ReleaseAll => _held.Count > 0,
        _ => true,
    };

    private void ApplyHeldLocked(BrowserControlMessage input)
    {
        switch (input.Held)
        {
            case HeldEffect.Press:
                _held.Add(input.HeldKey);
                break;
            case HeldEffect.Release:
                _held.Remove(input.HeldKey);
                break;
            case HeldEffect.ReleaseAll:
                _held.Clear();
                break;
        }
    }

    private bool TakeRateLocked(InputRate rate, DateTimeOffset now) => rate switch
    {
        InputRate.Move => _moves.TryTake(now),
        InputRate.Other => _otherInput.TryTake(now),
        InputRate.Sas => _sasInput.TryTake(now),
        _ => _exemptInput.TryTake(now),
    };

    // The caller holds the lock. The PC and the streaming helper are handed back only while the period belongs to the attached PC.
    private ControlPeriodEnd EndControlLocked()
    {
        var holdsPc = _pc is not null && ReferenceEquals(_controlPc, _pc);
        var ended = new ControlPeriodEnd(_controlAuditId, holdsPc ? _pc : null, holdsPc ? _helper : null);
        _control = ScreenControlState.Off;
        _controlPc = null;
        _controlSource = null;
        _controlAuditId = null;
        _held.Clear();
        return ended;
    }
}

public enum ScreenControlState
{
    Off,
    Requesting,
    On,
}

// A request for control that has started: its id, which a timer uses to tell it apart from a later request, and the socket asked,
// which is the PC or the helper streaming in its place.
public readonly record struct ControlRequest(long Id, ScreenSocket Pc);

// What a control period leaves behind when it ends: its audit row, and the sockets to tell. AttachedPc is null when the PC is no
// longer attached, and Helper is null unless a helper was streaming in the PC's place.
public readonly record struct ControlPeriodEnd(int? AuditId, ScreenSocket? AttachedPc, ScreenSocket? Helper = null);

// A frame relayed to the browser and not acknowledged yet: who sent it, and in which of the helper's runs (0 for the PC).
public readonly record struct InFlightFrame(ScreenSocket Sender, long HelperRun);

// What a change of source asks the hub to send, in this order: stop, release, start, control on. EndedControl is set when the
// change ended control. State is the browser's new state, or null when it didn't change.
public readonly record struct SourceSwitch(
    ScreenSocket? StoppedHelper = null,
    ScreenSocket? StartedHelper = null,
    ScreenSocket? ControlSource = null,
    ScreenSocket? ReleasedPc = null,
    ControlPeriodEnd? EndedControl = null,
    string? State = null);

// Who opened a view, and the session they opened it under. The session version is what the monitor checks while the view is open.
public readonly record struct ScreenViewOwner(int AdminId, int SessionVersion, string AdminUserName);
