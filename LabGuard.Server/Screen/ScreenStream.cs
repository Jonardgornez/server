using System.Security.Cryptography;
using System.Text;

namespace LabGuard.Server.Screen;

// One PC's live stream, shared by every administrator viewing that PC: the PC's socket while it is connected, the secure-desktop
// helper while it is the source of frames, the frame credits, the state relayed, the remote control state, the viewers (at most
// ScreenProtocol.MaxViewersPerPc) and the lifetime that stops the PC's relay loop. Everything mutable is guarded by one lock.
// The PC streams under one view id and one mode. The mode is notified while any viewer is notified, and anonymous only when every
// viewer is. The view id changes with the mode, so a PC that still streams under the old mode can't reconnect under it.
public sealed class ScreenStream : IDisposable
{
    private readonly Lock _sync = new();
    private readonly Lifetime _lifetime = new();
    private readonly ScreenInputRateLimiter _moves = new(ScreenProtocol.MaxMovesPerSecond);
    private readonly ScreenInputRateLimiter _otherInput = new(ScreenProtocol.MaxOtherInputPerSecond);
    private readonly ScreenInputRateLimiter _exemptInput = new(ScreenProtocol.MaxExemptInputPerSecond);
    private readonly ScreenInputRateLimiter _sasInput = new(ScreenProtocol.MaxSasPerSecond);
    private readonly ScreenInputRateLimiter _textInput = new(ScreenProtocol.MaxTextPerSecond);
    private readonly ScreenInputRateLimiter _cursors = new(ScreenProtocol.MaxCursorsPerSecond);

    // The keys and buttons the server has relayed as down. An up or release is relayed only for one of these, and the set is cleared when control ends.
    private readonly HashSet<string> _held = [];

    // The viewers, oldest first, each with the frames it hasn't acknowledged yet.
    private readonly List<ViewerSlot> _viewers = [];

    // Each frame no viewer has acknowledged yet, oldest first. These are the PC's (or the helper's) frames in flight.
    private readonly List<InFlightFrame> _framesInFlight = [];
    private string _viewId = NewViewId();
    private string _mode;
    private ScreenSocket? _pc;
    private bool _isPcPaused;
    // The secure-desktop helper while it streams in place of the paused PC. Null while the PC's own stream is the source.
    private ScreenSocket? _helper;
    // Counts the helper's streaming runs. A helper starts each run with a fresh window, so frames from an earlier run no longer count.
    private long _helperRun;
    private string _state = ScreenProtocol.WaitingState;
    private bool _isClosed;
    private ScreenControlState _control = ScreenControlState.Off;
    // The one viewer that asked for control, while control is requesting or on. Every other viewer only watches.
    private ScreenViewer? _controller;
    // The PC connection the control period belongs to. Control ends with that connection.
    private ScreenSocket? _controlPc;
    // The socket that carries control now: the one asked to confirm, and the one input goes to. The PC or the streaming helper.
    private ScreenSocket? _controlSource;
    private long _controlRequestId;
    private int? _controlAuditId;
    // True once a pointer shape has been relayed to the controller since control turned on or last moved to the PC. The browser is
    // then told the default shape when control ends or moves to the helper.
    private bool _hasRelayedCursor;

    public ScreenStream(int pcId, string pcName, string mode)
    {
        PcId = pcId;
        PcName = pcName;
        _mode = mode;
    }

    public int PcId { get; }
    public string PcName { get; }

    // Held around each state change and its sends, so states reach the browsers in the order they were recorded.
    // Control changes take it too. Closing a view doesn't, so a close never waits long behind a send that is stuck.
    public SemaphoreSlim StateGate { get; } = new(1, 1);

    // Held around choosing an input's target and sending it, and around the messages of a source switch and the helper's stop
    // on close. So input can't reach a source after it was released or stopped, and a helper's start and stop can't cross.
    // Taken after StateGate when both are needed, never before it.
    public SemaphoreSlim InputGate { get; } = new(1, 1);

    public string ViewId
    {
        get
        {
            lock (_sync)
            {
                return _viewId;
            }
        }
    }

    public string Mode
    {
        get
        {
            lock (_sync)
            {
                return _mode;
            }
        }
    }

    public string State
    {
        get
        {
            lock (_sync)
            {
                return _state;
            }
        }
    }

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

    public ScreenViewer? Controller
    {
        get
        {
            lock (_sync)
            {
                return _controller;
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

    // True when no other viewer can join without one leaving.
    public bool IsFull
    {
        get
        {
            lock (_sync)
            {
                return PresentLocked().Count() >= ScreenProtocol.MaxViewersPerPc;
            }
        }
    }

    // The viewers that are still watching, oldest first. A viewer that is closing or being replaced is left out.
    public IReadOnlyList<ScreenViewer> Viewers
    {
        get
        {
            lock (_sync)
            {
                return PresentLocked().Select(slot => slot.Viewer).ToList();
            }
        }
    }

    // What the PC is told in its heartbeat. Null once the stream has closed.
    public ScreenStreamView? Describe()
    {
        lock (_sync)
        {
            return _isClosed ? null : new ScreenStreamView(_viewId, _mode);
        }
    }

    // The control state as one viewer sees it: off unless that viewer is the one asking for or holding control.
    public ScreenControlState ControlStateFor(ScreenViewer viewer)
    {
        lock (_sync)
        {
            return ReferenceEquals(_controller, viewer) ? _control : ScreenControlState.Off;
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

    // A source linked to the stream's lifetime. Null once the stream is disposed. The caller disposes the one it gets.
    public CancellationTokenSource? LinkLifetime(CancellationToken cancellationToken) => _lifetime.Link(cancellationToken);

    public void CancelLifetime() => _lifetime.Cancel();

    public void Dispose() => _lifetime.Dispose();

    // Constant time, so a PC can't learn the view id one character at a time.
    public bool ViewIdMatches(string? candidate)
    {
        lock (_sync)
        {
            return ViewIdMatchesLocked(candidate);
        }
    }

    // Adds the viewer within the cap. A passive viewer is Busy when the stream is full. A full view takes the place of the oldest
    // passive viewer that isn't controlling, and is Busy only when there is none. Null when the stream has already closed.
    // When the viewer makes the stream notified, everything that streams without the notice is let go at once, so not one more of
    // its frames is relayed: the attached PC and the helper streaming in its place are handed back, to be closed and stopped. A
    // control period through them ends here too, while they are still known, so both can be told control is off.
    public StreamJoin? TryJoin(ScreenViewer viewer)
    {
        lock (_sync)
        {
            if (_isClosed)
            {
                return null;
            }

            ScreenViewer? replaced = null;
            if (PresentLocked().Count() >= ScreenProtocol.MaxViewersPerPc)
            {
                var oldestPassive = viewer.IsPassive
                    ? null
                    : PresentLocked().FirstOrDefault(slot => slot.Viewer.IsPassive && !ReferenceEquals(slot.Viewer, _controller));
                if (oldestPassive is null)
                {
                    return StreamJoin.Busy;
                }

                oldestPassive.IsLeaving = true;
                replaced = oldestPassive.Viewer;
            }

            _viewers.Add(new ViewerSlot(viewer));
            if (!UpdateModeLocked() || (_pc is null && _helper is null))
            {
                return new StreamJoin(StreamJoinStatus.Joined, replaced);
            }

            ControlPeriodEnd? endedControl = _control == ScreenControlState.Off ? null : EndControlLocked();
            var droppedPc = _pc;
            var stoppedHelper = _helper;
            _pc = null;
            _helper = null;
            _helperRun++;
            return new StreamJoin(StreamJoinStatus.Joined, replaced, droppedPc, stoppedHelper, endedControl);
        }
    }

    // Takes the viewer out. Frames only it still held are acknowledged on its behalf, so the PC doesn't lose their credits.
    // The last viewer to leave closes the stream and hands back the PC and the helper, so they can be closed and stopped.
    // Otherwise the mode is worked out again. Control stays with a leaving controller until the hub ends it under the state gate.
    public ViewerLeave RemoveViewer(ScreenViewer viewer)
    {
        lock (_sync)
        {
            var index = _viewers.FindIndex(slot => ReferenceEquals(slot.Viewer, viewer));
            if (index < 0)
            {
                return new ViewerLeave([]);
            }

            var slot = _viewers[index];
            _viewers.RemoveAt(index);
            if (_viewers.Count == 0)
            {
                _isClosed = true;
                var pc = _pc;
                var helper = _helper;
                _pc = null;
                _helper = null;
                _framesInFlight.Clear();
                return new ViewerLeave([], IsLast: true, ClosedPc: pc, StreamingHelper: helper);
            }

            var acks = new List<ScreenSocket>();
            foreach (var frame in slot.Frames)
            {
                frame.Pending--;
                if (frame.Pending == 0 && TryAckLocked(frame) is { } sender)
                {
                    acks.Add(sender);
                }
            }

            UpdateModeLocked();
            return new ViewerLeave(acks, IsControllerLeaving: ReferenceEquals(_controller, viewer));
        }
    }

    // Attaches the PC's socket, only under the view id the stream has now. The id is checked again here, under the lock, because
    // the mode may have changed since the PC's connection was checked, and a PC that streams under the old mode must not attach.
    // The new connection starts with a fresh credit window and is not paused until it says so. The old connection's frames stay
    // queued, no longer counted, so the browsers' late acks for them are used up there and never credit the new connection.
    // False once the stream is closed or the id has moved on. The socket it replaces is returned, so the caller can end it.
    public bool TryAttachPc(ScreenSocket socket, string? viewId, out ScreenSocket? replaced)
    {
        lock (_sync)
        {
            replaced = null;
            if (_isClosed || !ViewIdMatchesLocked(viewId))
            {
                return false;
            }

            replaced = _pc;
            _pc = socket;
            _isPcPaused = false;
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
    // away from it, and the browsers' new state. Control carries across a switch, with its audit row. It ends only when the
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
            var stateToSend = state == _state ? null : state;
            _state = state;
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
            var resetCursor = TakeCursorResetLocked();
            return new SourceSwitch(previous, next, _controlSource, releasedPc, State: stateToSend, ResetCursor: resetCursor);
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
    // Null when the sender's window is full, the stream is closed, or the sender is not the current source.
    // The frame goes to every viewer with room in its own window, by the same rules, and a viewer without room skips it.
    // A frame no viewer has room for is dropped and its credit goes back at once, so viewers that fell behind never stall the PC.
    // A helper's frames carry no run id, so one captured under an older start can't be told apart once the helper is the source
    // again. The hub's per-helper command gate keeps that theoretical; the full fix is a run id in the start that frames echo.
    public FrameRelay? TryTakeFrameCredit(ScreenSocket sender)
    {
        lock (_sync)
        {
            if (_isClosed || !ReferenceEquals(_helper ?? _pc, sender) || !HasRoomLocked(_framesInFlight, sender))
            {
                return null;
            }

            var frame = new InFlightFrame(sender, _helper is null ? 0 : _helperRun);
            var recipients = new List<ScreenViewer>();
            foreach (var slot in PresentLocked().Where(slot => HasRoomLocked(slot.Frames, sender)))
            {
                slot.Frames.Enqueue(frame);
                recipients.Add(slot.Viewer);
            }

            frame.Pending = recipients.Count;
            if (recipients.Count == 0)
            {
                return new FrameRelay([], IsAckedAtOnce: true);
            }

            _framesInFlight.Add(frame);
            return new FrameRelay(recipients);
        }
    }

    // One viewer's ack returns one of its credits. False when it had no credit outstanding, so a stray or repeated ack isn't passed
    // on. The first viewer to acknowledge a frame passes the ack on, to the sender of that frame while the frame still counts: the
    // PC's while it is attached, the helper's during the same run. So the PC goes at the pace of its fastest viewer.
    public bool TryReturnFrameCredit(ScreenViewer viewer, out ScreenSocket? ackTarget)
    {
        lock (_sync)
        {
            ackTarget = null;
            var slot = _viewers.Find(candidate => ReferenceEquals(candidate.Viewer, viewer));
            if (slot is null || !slot.Frames.TryDequeue(out var frame))
            {
                return false;
            }

            frame.Pending--;
            ackTarget = TryAckLocked(frame);
            return true;
        }
    }

    // Starts the viewer's request for control of the attached PC. Refused while another viewer asks for or holds control, and when
    // the stream is closed or no PC is attached.
    public ControlRequestStart TryBeginControlRequest(ScreenViewer viewer)
    {
        lock (_sync)
        {
            if (_control != ScreenControlState.Off)
            {
                return new ControlRequestStart(null, ControlReasons.InUse);
            }

            if (_isClosed || _pc is null || !IsPresentLocked(viewer))
            {
                return new ControlRequestStart(null, ControlReasons.Waiting);
            }

            _control = ScreenControlState.Requesting;
            _controller = viewer;
            _controlPc = _pc;
            _controlSource = _helper ?? _pc;
            _controlRequestId++;
            return new ControlRequestStart(new ControlRequest(_controlRequestId, _controlSource));
        }
    }

    // The viewer whose request the socket was asked to confirm, while the request is still waiting for it. Null otherwise.
    public ScreenViewer? RequesterAwaiting(ScreenSocket pc)
    {
        lock (_sync)
        {
            return IsAwaitingConfirmationLocked(pc) ? _controller : null;
        }
    }

    // Turns control on, with the audit row that records it. False when the request is no longer waiting for this PC, or the stream closed.
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

    // Ends the control period only if this viewer holds it, so one administrator can't end another's control.
    public ControlPeriodEnd? TryEndControlOf(ScreenViewer viewer)
    {
        lock (_sync)
        {
            return _control != ScreenControlState.Off && ReferenceEquals(_controller, viewer) ? EndControlLocked() : null;
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
    // Only from the controller, only while control is on, only for a key or button that is held when it must be, and only within
    // the cap for its kind. Null otherwise. A relayed input updates what the server holds down on the PC.
    public ScreenSocket? TakeControlInputTarget(ScreenViewer viewer, BrowserControlMessage input, DateTimeOffset now, ScreenSocket? connectedHelper = null)
    {
        lock (_sync)
        {
            if (_isClosed
                || _control != ScreenControlState.On
                || !ReferenceEquals(_controller, viewer)
                || !IsPresentLocked(viewer)
                || !ReferenceEquals(_pc, _controlPc))
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

    // Takes one of the pointer shapes the controller may be sent per second. Only while control is on and carried by this PC
    // socket, so a paused PC can't change the pointer while the helper carries control. False otherwise.
    public bool TryTakeCursorSlot(ScreenSocket pc, DateTimeOffset now)
    {
        lock (_sync)
        {
            if (_isClosed
                || _control != ScreenControlState.On
                || _pc is null
                || !ReferenceEquals(_pc, pc)
                || !ReferenceEquals(_controlPc, pc)
                || !ReferenceEquals(_controlSource, pc)
                || !_cursors.TryTake(now))
            {
                return false;
            }

            _hasRelayedCursor = true;
            return true;
        }
    }

    // Control ends with the stream. The audit row it had is handed back, so the caller can end it.
    public int? EndControlOnClose()
    {
        lock (_sync)
        {
            return EndControlLocked().AuditId;
        }
    }

    private static string NewViewId() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

    private bool ViewIdMatchesLocked(string? candidate) =>
        candidate is not null && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(_viewId), Encoding.UTF8.GetBytes(candidate));

    private IEnumerable<ViewerSlot> PresentLocked() => _viewers.Where(slot => !slot.IsLeaving && !slot.Viewer.IsClosed);

    // A viewer that is closing, being replaced or gone can neither take control nor send input.
    private bool IsPresentLocked(ScreenViewer viewer) => PresentLocked().Any(slot => ReferenceEquals(slot.Viewer, viewer));

    // Works the mode out from the viewers still watching, and takes a new view id when it changed. True when it became notified.
    private bool UpdateModeLocked()
    {
        var present = PresentLocked().ToList();
        if (present.Count == 0)
        {
            return false;
        }

        var mode = present.Exists(slot => slot.Viewer.Mode == ScreenProtocol.NotifiedMode)
            ? ScreenProtocol.NotifiedMode
            : ScreenProtocol.AnonymousMode;
        if (mode == _mode)
        {
            return false;
        }

        _mode = mode;
        _viewId = NewViewId();
        return mode == ScreenProtocol.NotifiedMode;
    }

    // Marks the frame acknowledged, once. Its sender is handed back while the frame still counts, so the caller can pass the ack on.
    private ScreenSocket? TryAckLocked(InFlightFrame frame)
    {
        if (!_framesInFlight.Remove(frame))
        {
            return null;
        }

        return IsCountedLocked(frame) ? frame.Sender : null;
    }

    private bool HasRoomLocked(IEnumerable<InFlightFrame> frames, ScreenSocket sender)
    {
        var total = 0;
        var fromSender = 0;
        foreach (var frame in frames)
        {
            total++;
            if (ReferenceEquals(frame.Sender, sender) && IsCountedLocked(frame))
            {
                fromSender++;
            }
        }

        return total < ScreenProtocol.MaxFramesInFlightAcrossSwitch && fromSender < ScreenProtocol.MaxFramesInFlight;
    }

    private bool TakeCursorResetLocked()
    {
        var hadCursor = _hasRelayedCursor;
        _hasRelayedCursor = false;
        return hadCursor;
    }

    // The viewer that asked must still be watching, so a requester that closed while its audit row was written can't get control.
    private bool IsAwaitingConfirmationLocked(ScreenSocket pc) =>
        !_isClosed
        && _control == ScreenControlState.Requesting
        && _controller is not null
        && IsPresentLocked(_controller)
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
        InputRate.Text => _textInput.TryTake(now),
        _ => _exemptInput.TryTake(now),
    };

    // The caller holds the lock. The PC and the streaming helper are handed back only while the period belongs to the attached PC.
    private ControlPeriodEnd EndControlLocked()
    {
        var holdsPc = _pc is not null && ReferenceEquals(_controlPc, _pc);
        var ended = new ControlPeriodEnd(_controlAuditId, holdsPc ? _pc : null, holdsPc ? _helper : null, TakeCursorResetLocked(), _controller);
        _control = ScreenControlState.Off;
        _controller = null;
        _controlPc = null;
        _controlSource = null;
        _controlAuditId = null;
        _held.Clear();
        return ended;
    }

    // A viewer and the frames it hasn't acknowledged yet. A viewer being replaced is leaving: it gets no more frames and doesn't count.
    private sealed class ViewerSlot(ScreenViewer viewer)
    {
        public ScreenViewer Viewer { get; } = viewer;
        public Queue<InFlightFrame> Frames { get; } = new();
        public bool IsLeaving { get; set; }
    }
}

public enum ScreenControlState
{
    Off,
    Requesting,
    On,
}

// The view id and mode the PC streams under.
public readonly record struct ScreenStreamView(string ViewId, string Mode);

public enum StreamJoinStatus
{
    Joined,
    // The stream is full, and nobody's place could be taken.
    Busy,
}

// What a join did. Replaced is the passive viewer whose place a full view took, to be closed. When the stream became notified,
// DroppedPc is the PC socket let go, to be closed so the PC reconnects showing the notice, StoppedHelper is the helper that was
// streaming in its place, to be told to stop, and EndedControl is the control period that went through them.
public sealed record StreamJoin(
    StreamJoinStatus Status,
    ScreenViewer? Replaced = null,
    ScreenSocket? DroppedPc = null,
    ScreenSocket? StoppedHelper = null,
    ControlPeriodEnd? EndedControl = null)
{
    public static readonly StreamJoin Busy = new(StreamJoinStatus.Busy);
}

// What a viewer leaving asks the hub to do. Acks go to the senders of frames only that viewer still held. The last viewer closes the
// stream: its PC is closed and its streaming helper stopped. A leaving controller's control period is ended by the hub.
public sealed record ViewerLeave(
    IReadOnlyList<ScreenSocket> Acks,
    bool IsLast = false,
    ScreenSocket? ClosedPc = null,
    ScreenSocket? StreamingHelper = null,
    bool IsControllerLeaving = false);

// Who a frame goes to. IsAckedAtOnce is true when no viewer had room, so the frame is dropped and the sender gets its credit back now.
public sealed record FrameRelay(IReadOnlyList<ScreenViewer> Recipients, bool IsAckedAtOnce = false);

// A request for control that has started: its id, which a timer uses to tell it apart from a later request, and the socket asked,
// which is the PC or the helper streaming in its place.
public readonly record struct ControlRequest(long Id, ScreenSocket Pc);

// A request that started, or the reason it was refused.
public readonly record struct ControlRequestStart(ControlRequest? Request, string? Refusal = null);

// What a control period leaves behind when it ends: its audit row, and the sockets to tell. AttachedPc is null when the PC is no
// longer attached, and Helper is null unless a helper was streaming in the PC's place. ResetCursor is true when the controller was
// shown the PC's pointer shape, so it must be told the default one. Controller is the viewer that held control, to be told it ended.
public readonly record struct ControlPeriodEnd(
    int? AuditId,
    ScreenSocket? AttachedPc,
    ScreenSocket? Helper = null,
    bool ResetCursor = false,
    ScreenViewer? Controller = null);

// A frame relayed and not acknowledged by every viewer it went to: who sent it, in which of the helper's runs (0 for the PC), and
// how many of its viewers still owe an ack. A reference type, so every viewer's queue holds the same frame.
public sealed class InFlightFrame(ScreenSocket sender, long helperRun)
{
    public ScreenSocket Sender { get; } = sender;
    public long HelperRun { get; } = helperRun;
    public int Pending { get; set; }
}

// What a change of source asks the hub to send, in this order: stop, release, start, control on. EndedControl is set when the
// change ended control. State is the browsers' new state, or null when it didn't change. ResetCursor is true when control moved
// away from the PC after its pointer shape was shown, so the controller must be told the default one.
public readonly record struct SourceSwitch(
    ScreenSocket? StoppedHelper = null,
    ScreenSocket? StartedHelper = null,
    ScreenSocket? ControlSource = null,
    ScreenSocket? ReleasedPc = null,
    ControlPeriodEnd? EndedControl = null,
    string? State = null,
    bool ResetCursor = false);
