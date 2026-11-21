using System.Net.WebSockets;
using System.Text;

namespace LabGuard.Server.Screen;

// The in-memory registry of live views: one stream per PC, watched by up to ScreenProtocol.MaxViewersPerPc browsers. Frames are
// relayed through it and never stored. It holds no frame beyond the ones being sent, and a closed view can't be attached to or
// relay anything again. It also holds each PC's secure-desktop helper, which streams in the PC's place while the PC's stream is paused.
public sealed partial class ScreenStreamHub(ScreenViewAudit audit, ScreenControlAudit controlAudit, TimeProvider clock)
{
    private readonly Lock _sync = new();
    private readonly Dictionary<int, ScreenStream> _streamsByPc = [];
    private readonly Dictionary<int, DateTimeOffset> _lastAttachByPc = [];
    private long _epoch;

    // Bumped by every CloseAll, which is a logout or a password change. A view opened under an older epoch is refused.
    public long Epoch
    {
        get
        {
            lock (_sync)
            {
                return _epoch;
            }
        }
    }

    // Opens a full view of the PC. Null when the view is refused (the epoch moved on, the browser is gone, or the PC has too many views).
    public async Task<ScreenViewer?> OpenAsync(
        int pcId,
        string mode,
        DateTimeOffset sessionDeadline,
        int auditId,
        ScreenSocket browser,
        long epoch,
        ScreenViewOwner owner,
        string pcName) =>
        (await OpenAsync(pcId, mode, sessionDeadline, auditId, browser, epoch, owner, pcName, isPassive: false)).Viewer;

    // Joins the PC's stream, starting one if it has none. Busy when the stream is full: for a passive view always, and for a full
    // view only when no passive view's place can be taken. Decided under the lock that publishes the viewer, so opens racing each
    // other can't go past the cap. In every case that doesn't open, the audit row is ended and nothing stays registered.
    public async Task<ScreenOpenResult> OpenAsync(
        int pcId,
        string mode,
        DateTimeOffset sessionDeadline,
        int auditId,
        ScreenSocket browser,
        long epoch,
        ScreenViewOwner owner,
        string pcName,
        bool isPassive)
    {
        ScreenViewer? viewer = null;
        StreamJoin? join = null;
        try
        {
            // The waiting state goes out before the viewer can be found, so a later live state can't overtake it.
            if (!await browser.SendAsync(ScreenProtocol.StateMessage(ScreenProtocol.WaitingState), WebSocketMessageType.Text))
            {
                return ScreenOpenResult.Refused;
            }

            lock (_sync)
            {
                if (epoch != _epoch)
                {
                    return ScreenOpenResult.Refused;
                }

                (viewer, join) = JoinLocked(pcId, pcName, mode, sessionDeadline, auditId, browser, owner, isPassive);
                if (join.Status == StreamJoinStatus.Busy)
                {
                    return ScreenOpenResult.Busy;
                }
            }

            await SettleJoinAsync(viewer, join);
            return new ScreenOpenResult(ScreenOpenStatus.Opened, viewer);
        }
        catch when (join is { Status: StreamJoinStatus.Joined })
        {
            // The viewer whose place was taken is closed too, so it never stays half replaced with its socket and audit row open.
            await Task.WhenAll(
                CloseAsync(viewer!, WebSocketCloseStatus.InternalServerError, "The view could not start."),
                join.Replaced is { } replaced ? CloseAsync(replaced, ScreenProtocol.ReplacedCloseStatus, ReplacedReason) : Task.CompletedTask);
            // A helper taken out of the stream by this join is told to stop even when settling failed, so it doesn't keep streaming
            // frames that are all dropped.
            if (join.StoppedHelper is { } helper)
            {
                await StopTakenOutHelperAsync(viewer!.Stream, helper);
            }

            throw;
        }
        finally
        {
            if (join is not { Status: StreamJoinStatus.Joined })
            {
                viewer?.Dispose();
                await audit.EndAsync(auditId, clock.GetUtcNow());
            }
        }
    }

    // The PC's stream, while at least one view of it is open.
    public ScreenStream? StreamFor(int pcId)
    {
        lock (_sync)
        {
            return _streamsByPc.GetValueOrDefault(pcId);
        }
    }

    // The views of the PC that are still watching, oldest first.
    public IReadOnlyList<ScreenViewer> ViewersFor(int pcId) => StreamFor(pcId)?.Viewers ?? [];

    public IReadOnlyList<ScreenViewer> Snapshot()
    {
        IReadOnlyList<ScreenStream> streams;
        lock (_sync)
        {
            streams = _streamsByPc.Values.ToList();
        }

        return streams.SelectMany(stream => stream.Viewers).ToList();
    }

    // A PC may reconnect at most once per cooldown. Returns false when it reconnected too soon. Only attempts that got this far count.
    public bool TryReserveAttach(int pcId, DateTimeOffset now)
    {
        lock (_sync)
        {
            if (_lastAttachByPc.TryGetValue(pcId, out var last) && now - last < ScreenProtocol.AttachCooldown)
            {
                return false;
            }

            _lastAttachByPc[pcId] = now;
            return true;
        }
    }

    // False when the stream has already closed, or its view id is no longer the one the PC connected with, because the mode
    // changed meanwhile. A PC socket that replaces another ends the old one at once.
    // Control that was held through the old socket ends with it, and the controller is told why.
    public async Task<bool> AttachPcAsync(ScreenStream stream, ScreenSocket pc, string? viewId)
    {
        if (!stream.TryAttachPc(pc, viewId, out var replaced))
        {
            return false;
        }

        if (replaced is not null)
        {
            await EndControlAsync(stream, ControlReasons.Connection);
            await replaced.CloseAsync(ScreenProtocol.ReplacedCloseStatus, "Replaced by another connection.");
            replaced.Abort();
        }

        await SwitchSourceAsync(stream);
        return true;
    }

    // A helper streaming for the PC that left is told to stop, and the browsers hear that the view waits.
    public async Task DetachPcAsync(ScreenStream stream, ScreenSocket pc)
    {
        if (stream.TryDetachPc(pc))
        {
            await EndControlAsync(stream, ControlReasons.Connection);
            await SwitchSourceAsync(stream);
        }
    }

    // The PC says it can't capture its screen (paused) or can again (resumed). Ignored unless that PC is the attached one.
    // While paused, a connected helper streams in its place and the browsers hear secure. With no helper they hear paused.
    public async Task SetPcPausedAsync(ScreenStream stream, ScreenSocket pc, bool isPaused)
    {
        if (stream.TrySetPcPaused(pc, isPaused))
        {
            await SwitchSourceAsync(stream);
        }
    }

    // A frame goes out only while its sender has a credit, and only to the viewers with room in their own window. Otherwise it is
    // dropped on the spot, so nothing queues up. A view whose administrator's session has run out is closed instead, so no frame
    // is sent to it late. The sends run side by side, and a browser that stops reading closes only its own view.
    public async Task RelayFrameAsync(ScreenStream stream, ScreenSocket sender, ReadOnlyMemory<byte> frame)
    {
        var now = clock.GetUtcNow();
        var expired = stream.Viewers.Where(viewer => now >= viewer.SessionDeadline).ToList();
        if (expired.Count > 0)
        {
            await Task.WhenAll(expired.Select(viewer => CloseAsync(viewer, ScreenProtocol.SessionEndedCloseStatus, ScreenProtocol.SessionEndedReason)));
        }

        if (stream.TryTakeFrameCredit(sender) is not { } relay)
        {
            return;
        }

        if (relay.IsAckedAtOnce)
        {
            await sender.SendAsync(ScreenProtocol.AckMessage, WebSocketMessageType.Text);
            return;
        }

        await Task.WhenAll(relay.Recipients.Select(viewer => SendFrameAsync(viewer, frame)));
    }

    // Each browser ack returns one of its credits. The first ack for a frame is passed on to that frame's sender, the PC or the
    // helper. An ack with no outstanding credit is not passed on, and neither is one for a sender that is no longer the source.
    public async Task RelayAckAsync(ScreenViewer viewer)
    {
        if (viewer.Stream.TryReturnFrameCredit(viewer, out var sender) && sender is not null)
        {
            await sender.SendAsync(ScreenProtocol.AckMessage, WebSocketMessageType.Text);
        }
    }

    // The administrator asks for control, from either view mode. Refused when the PC is the server's own, when no PC is attached,
    // and while another view of the PC asks for or holds control. The PC is asked with control on, and has
    // ScreenProtocol.ControlConfirmTimeout to confirm before the request is abandoned.
    public async Task RequestControlAsync(ScreenViewer viewer, bool isServerPc)
    {
        var stream = viewer.Stream;
        await stream.StateGate.WaitAsync();
        try
        {
            if (viewer.ControlState != ScreenControlState.Off)
            {
                return;
            }

            if (clock.GetUtcNow() >= viewer.SessionDeadline)
            {
                await CloseAsync(viewer, ScreenProtocol.SessionEndedCloseStatus, ScreenProtocol.SessionEndedReason);
                return;
            }

            if (ControlRefusalFor(isServerPc) is { } refusal)
            {
                await SendControlMessageAsync(viewer, ScreenControlMessages.OffMessage(refusal));
                return;
            }

            var start = stream.TryBeginControlRequest(viewer);
            if (start.Request is not { } request)
            {
                await SendControlMessageAsync(viewer, ScreenControlMessages.OffMessage(start.Refusal ?? ControlReasons.Waiting));
                return;
            }

            await SendControlMessageAsync(viewer, ScreenControlMessages.RequestingMessage);
            // Bounded, and a PC that doesn't take the request in time is cut off, which ends the request through its connection.
            if (!await request.Pc.SendAsync(ScreenControlMessages.PcOnMessage, WebSocketMessageType.Text, ScreenProtocol.ControlInputSendTimeout))
            {
                request.Pc.Abort();
            }

            ScheduleConfirmTimeout(stream, request.Id);
        }
        finally
        {
            stream.StateGate.Release();
        }
    }

    // Whether a control request may go on to the server-PC check. Only a view that isn't asking for or holding control, before its
    // deadline and within the request rate. Nothing here reads the database, so a flood of requests costs little.
    public async Task<bool> PrepareControlRequestAsync(ScreenViewer viewer)
    {
        if (clock.GetUtcNow() >= viewer.SessionDeadline)
        {
            await CloseAsync(viewer, ScreenProtocol.SessionEndedCloseStatus, ScreenProtocol.SessionEndedReason);
            return false;
        }

        return viewer.ControlState == ScreenControlState.Off && viewer.TryTakeControlRequestSlot(clock.GetUtcNow());
    }

    // The PC has shown the notice and will replay input, or the helper is ready to. Only the socket that was asked can confirm,
    // while the request is pending.
    // The audit row is written before control turns on, so control is never on unrecorded. It names the administrator who asked,
    // and the mode the PC streams in. The write is bounded. A write that fails or runs out of time ends the request as unsupported,
    // and the PC stream stays open.
    public async Task ConfirmControlAsync(ScreenStream stream, ScreenSocket pc)
    {
        if (stream.RequesterAwaiting(pc) is not { } requester)
        {
            return;
        }

        int auditId;
        try
        {
            using var timeout = new CancellationTokenSource(ScreenProtocol.ControlStartTimeout);
            auditId = await controlAudit.StartAsync(requester.AdminUserName, stream.PcName, stream.Mode, clock.GetUtcNow(), timeout.Token);
        }
        catch (Exception)
        {
            // Any failure to record the period ends the request, so control is never turned on without its row.
            await EndControlPeriodAsync(stream, () => stream.TryEndRequestFrom(pc), ControlReasons.Unsupported);
            return;
        }

        var confirmed = false;
        await stream.StateGate.WaitAsync();
        try
        {
            confirmed = stream.TryConfirmControl(pc, auditId);
            if (confirmed && stream.Controller is { } controller)
            {
                await SendControlMessageAsync(controller, ScreenControlMessages.OnMessage);
            }
        }
        finally
        {
            stream.StateGate.Release();
        }

        if (!confirmed)
        {
            // The request ended or the view closed while the row was being written, so the row is ended at once.
            await controlAudit.EndAsync(auditId, clock.GetUtcNow());
        }
    }

    // Ends control for a reason: the connection that held it went away, or the PC ended it.
    public Task EndControlAsync(ScreenStream stream, string reason) =>
        EndControlPeriodAsync(stream, stream.TryEndControl, reason);

    // The PC or the helper says control has ended. Only the socket that carries control can end it, so the paused PC can't end
    // control that the helper carries.
    public Task EndControlFromAsync(ScreenStream stream, ScreenSocket source) =>
        EndControlPeriodAsync(stream, () => stream.TryEndControlFrom(source), ControlReasons.PcEnded);

    // The administrator's control off. It is always answered as stopped, even when this view holds no control.
    // Only this view's own control ends, and the PC is told off only if it was asked on, which is the case while requesting or on.
    public Task StopControlAsync(ScreenViewer viewer) =>
        EndControlPeriodAsync(viewer.Stream, () => viewer.Stream.TryEndControlOf(viewer), ControlReasons.Stopped, answered: viewer);

    // Input the browser sent. It is relayed in its canonical form, only from the view that holds control, only while control is on
    // and within the cap, to the socket that carries control. Ctrl+Alt+Del goes to the PC's helper whenever one is connected, since
    // only the helper can have it sent. Nothing is queued: an input that can't go out now is dropped.
    public async Task RelayInputAsync(ScreenViewer viewer, BrowserControlMessage input)
    {
        var now = clock.GetUtcNow();
        if (now >= viewer.SessionDeadline)
        {
            await CloseAsync(viewer, ScreenProtocol.SessionEndedCloseStatus, ScreenProtocol.SessionEndedReason);
            return;
        }

        // The target is chosen and sent to under the input gate, so a switch of source can't slip between the two.
        var stream = viewer.Stream;
        await stream.InputGate.WaitAsync();
        try
        {
            if (stream.TakeControlInputTarget(viewer, input, now, ConnectedHelperFor(stream.PcId)) is not { } pc)
            {
                return;
            }

            // Bounded, so one slow PC can't hold the browser's loop. A PC that doesn't take input in time is cut off, which ends control.
            if (!await pc.SendAsync(Encoding.UTF8.GetBytes(input.Canonical), WebSocketMessageType.Text, ScreenProtocol.ControlInputSendTimeout))
            {
                pc.Abort();
            }
        }
        finally
        {
            stream.InputGate.Release();
        }
    }

    // The PC's pointer shape, already in the browser's canonical form. Relayed only to the view that holds control, only while
    // control is on and carried by this PC socket, within the cursor cap. The other views show no pointer of their own, so they
    // aren't sent one. Sent under the state gate, so it can't arrive after the default sent when control ends.
    public async Task RelayCursorAsync(ScreenStream stream, ScreenSocket pc, ReadOnlyMemory<byte> browserCursor)
    {
        // A cheap first look, so a PC sending cursors while control is off never waits on the gate. The full check is under it.
        if (stream.ControlState != ScreenControlState.On)
        {
            return;
        }

        await stream.StateGate.WaitAsync();
        try
        {
            if (stream.TryTakeCursorSlot(pc, clock.GetUtcNow()) && stream.Controller is { } controller)
            {
                await SendControlMessageAsync(controller, browserCursor);
            }
        }
        finally
        {
            stream.StateGate.Release();
        }
    }

    // Closes the view's browser socket, then ends its audit row. Only the first call does anything. The browser closes first, so
    // the administrator sees the close without waiting for the database. The last view of a PC also closes the PC's socket with
    // the same code and ends any control period, and a helper that was streaming is told to stop and stays connected for the next
    // view. A view that leaves while others stay gives back the frame credits only it held, and ends the control it held.
    public async Task CloseAsync(ScreenViewer viewer, WebSocketCloseStatus status, string reason)
    {
        // Taken before the close handshake, so the audit rows record the moment the view closed.
        var closedAt = clock.GetUtcNow();
        if (!viewer.TryClose())
        {
            return;
        }

        var stream = viewer.Stream;
        var leave = stream.RemoveViewer(viewer);
        if (leave.IsLast)
        {
            lock (_sync)
            {
                if (ReferenceEquals(_streamsByPc.GetValueOrDefault(stream.PcId), stream))
                {
                    _streamsByPc.Remove(stream.PcId);
                }
            }
        }

        var pc = leave.ClosedPc;
        try
        {
            await Task.WhenAll(
                viewer.Browser.CloseAsync(status, reason),
                pc?.CloseAsync(status, reason) ?? Task.CompletedTask,
                leave.StreamingHelper is { } helper ? StopHelperOnCloseAsync(stream, helper) : Task.CompletedTask);

            // Cancelling the lifetime resets the connection, so each peer first gets a moment to receive the close code.
            await Task.WhenAll(
                viewer.Browser.WaitForPeerCloseAsync(ScreenProtocol.CloseHandshakeTimeout),
                pc?.WaitForPeerCloseAsync(ScreenProtocol.CloseHandshakeTimeout) ?? Task.CompletedTask);
        }
        finally
        {
            viewer.CancelLifetime();
            try
            {
                await FinishLeaveAsync(stream, viewer, leave, closedAt);
            }
            finally
            {
                await audit.EndAsync(viewer.AuditId, closedAt);
            }
        }
    }

    // Ends every view. Each logout and password change calls this, and it bumps the epoch so a view still being opened is refused.
    public async Task CloseAllAsync(WebSocketCloseStatus status, string reason)
    {
        lock (_sync)
        {
            _epoch++;
        }

        await Task.WhenAll(Snapshot().Select(viewer => CloseAsync(viewer, status, reason)));
    }

    // Closes the views one administrator opened, when that administrator's sessions end. Other administrators' views of the
    // same PCs stay open. The epoch still moves on, so a view that is being opened while the sessions end is refused.
    public async Task CloseAllOfAdminAsync(int adminId, WebSocketCloseStatus status, string reason)
    {
        lock (_sync)
        {
            _epoch++;
        }

        var viewers = Snapshot().Where(viewer => viewer.Owner.AdminId == adminId);
        await Task.WhenAll(viewers.Select(viewer => CloseAsync(viewer, status, reason)));
    }

    // Called with the hub's lock held. Joins the PC's stream, or a fresh one when it has none or the one it had has just closed.
    private (ScreenViewer Viewer, StreamJoin Join) JoinLocked(
        int pcId,
        string pcName,
        string mode,
        DateTimeOffset sessionDeadline,
        int auditId,
        ScreenSocket browser,
        ScreenViewOwner owner,
        bool isPassive)
    {
        if (_streamsByPc.GetValueOrDefault(pcId) is { } existing)
        {
            var joining = new ScreenViewer(existing, mode, auditId, sessionDeadline, browser, owner, isPassive);
            if (existing.TryJoin(joining) is { } joined)
            {
                return (joining, joined);
            }

            joining.Dispose();
        }

        var stream = new ScreenStream(pcId, pcName, mode);
        var viewer = new ScreenViewer(stream, mode, auditId, sessionDeadline, browser, owner, isPassive);
        var join = stream.TryJoin(viewer)!;
        _streamsByPc[pcId] = stream;
        return (viewer, join);
    }

    // What a join leaves to do once the viewer is published: close the passive view whose place it took, let go of what streams
    // without the notice the stream now needs, and tell the new viewer the stream's state.
    // Best effort on a failed open: the input gate is waited for at most a second, since the failure may have left it held.
    private async Task StopTakenOutHelperAsync(ScreenStream stream, ScreenSocket helper)
    {
        var hasGate = await stream.InputGate.WaitAsync(TimeSpan.FromSeconds(1));
        try
        {
            await StopHelperAsync(stream, helper);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The open is already failing; its own exception is the one passed on.
        }
        finally
        {
            if (hasGate)
            {
                stream.InputGate.Release();
            }
        }
    }

    private async Task SettleJoinAsync(ScreenViewer viewer, StreamJoin join)
    {
        if (join.Replaced is { } replaced)
        {
            await CloseAsync(replaced, ScreenProtocol.ReplacedCloseStatus, ReplacedReason);
        }

        var stream = viewer.Stream;
        if (join.DroppedPc is not null || join.StoppedHelper is not null)
        {
            await LetGoOfUnnoticedSourcesAsync(stream, join);
        }

        await stream.StateGate.WaitAsync();
        try
        {
            await SendStateAsync(viewer, stream.State);
        }
        finally
        {
            stream.StateGate.Release();
        }
    }

    // The stream became notified, so the PC and the helper that streamed without the notice were taken out of it. A control period
    // through them ended: the controller hears why, and the PC and the helper are told off, so what they hold down is released.
    // Then the helper is told to stop, the PC's socket is closed so it reconnects showing the notice, and the viewers hear waiting.
    private async Task LetGoOfUnnoticedSourcesAsync(ScreenStream stream, StreamJoin join)
    {
        if (join.EndedControl is { } ended)
        {
            await stream.StateGate.WaitAsync();
            try
            {
                if (ended.Controller is { } controller)
                {
                    await SendCursorResetAsync(controller, ended);
                    await SendControlMessageAsync(controller, ScreenControlMessages.OffMessage(ControlReasons.Connection));
                }

                await SendPcControlOffAsync(ended);
            }
            finally
            {
                stream.StateGate.Release();
            }

            if (ended.AuditId is { } auditId)
            {
                await controlAudit.EndAsync(auditId, clock.GetUtcNow());
            }
        }

        if (join.StoppedHelper is { } helper)
        {
            await stream.InputGate.WaitAsync();
            try
            {
                await StopHelperAsync(stream, helper);
            }
            finally
            {
                stream.InputGate.Release();
            }
        }

        if (join.DroppedPc is { } dropped)
        {
            await dropped.CloseAsync(ScreenProtocol.ReplacedCloseStatus, "The view mode changed.");
            // The PC gets a moment to receive the close code, so it waits for its next heartbeat instead of reconnecting.
            await dropped.WaitForPeerCloseAsync(ScreenProtocol.CloseHandshakeTimeout);
            dropped.Abort();
        }

        await SwitchSourceAsync(stream);
    }

    // The rest of a leave, once the browser has closed. The last view ends the stream and its control period. A view leaving a
    // stream that stays open passes on the acks it owed, and ends the control period it held.
    private async Task FinishLeaveAsync(ScreenStream stream, ScreenViewer viewer, ViewerLeave leave, DateTimeOffset closedAt)
    {
        if (leave.IsLast)
        {
            stream.CancelLifetime();
            try
            {
                // Control ends with the stream. Nothing more is sent to the PC, since its socket is closing.
                if (stream.EndControlOnClose() is { } controlAuditId)
                {
                    await controlAudit.EndAsync(controlAuditId, closedAt);
                }
            }
            finally
            {
                stream.Dispose();
            }

            return;
        }

        try
        {
            await Task.WhenAll(leave.Acks.Select(sender => sender.SendAsync(ScreenProtocol.AckMessage, WebSocketMessageType.Text)));
        }
        finally
        {
            if (leave.IsControllerLeaving)
            {
                await EndControlOfLeavingViewerAsync(stream, viewer, closedAt);
            }
        }
    }

    // The PC and the helper are told control is off, under the state gate so the off can't cross another view's request for control.
    // The wait for the gate is bounded, since this close may come from a send that holds the gate itself.
    private async Task EndControlOfLeavingViewerAsync(ScreenStream stream, ScreenViewer viewer, DateTimeOffset closedAt)
    {
        ControlPeriodEnd? ended;
        var isGated = await stream.StateGate.WaitAsync(ScreenProtocol.ControlInputSendTimeout);
        try
        {
            ended = stream.TryEndControlOf(viewer);
            await SendPcControlOffAsync(ended);
        }
        finally
        {
            if (isGated)
            {
                stream.StateGate.Release();
            }
        }

        if (ended?.AuditId is { } auditId)
        {
            await controlAudit.EndAsync(auditId, closedAt);
        }
    }

    // Ends the control period if the condition still holds, then tells the controller, the attached PC and the streaming helper,
    // and records the end. A view that asked to stop is answered even when it held nothing.
    // The state gate is held for the change and the sends, so the browser hears them in order. The audit write happens after it.
    private async Task EndControlPeriodAsync(ScreenStream stream, Func<ControlPeriodEnd?> tryEnd, string reason, ScreenViewer? answered = null)
    {
        ControlPeriodEnd? ended;
        await stream.StateGate.WaitAsync();
        try
        {
            ended = tryEnd();
            if ((ended?.Controller ?? answered) is { } told)
            {
                await SendCursorResetAsync(told, ended);
                await SendControlMessageAsync(told, ScreenControlMessages.OffMessage(reason));
            }

            await SendPcControlOffAsync(ended);
        }
        finally
        {
            stream.StateGate.Release();
        }

        if (ended?.AuditId is { } auditId)
        {
            await controlAudit.EndAsync(auditId, clock.GetUtcNow());
        }
    }

    // Called with the state gate held. A view that is closing is not sent anything more.
    private async Task SendControlMessageAsync(ScreenViewer viewer, ReadOnlyMemory<byte> message)
    {
        if (!viewer.IsClosed && !await viewer.Browser.SendAsync(message, WebSocketMessageType.Text))
        {
            await CloseStalledAsync(viewer);
        }
    }

    // Called with the state gate held. Sends the state unless the viewer was already sent it.
    private async Task SendStateAsync(ScreenViewer viewer, string state)
    {
        if (viewer.TrySetState(state) && !await viewer.Browser.SendAsync(ScreenProtocol.StateMessage(state), WebSocketMessageType.Text))
        {
            await CloseStalledAsync(viewer);
        }
    }

    // Called with the state gate held. The default pointer goes out before control off, while the browser still shows control.
    private async Task SendCursorResetAsync(ScreenViewer viewer, ControlPeriodEnd? ended)
    {
        if (ended is { ResetCursor: true })
        {
            await SendControlMessageAsync(viewer, PcCursorMessage.BrowserDefaultMessage);
        }
    }

    private async Task SendFrameAsync(ScreenViewer viewer, ReadOnlyMemory<byte> frame)
    {
        if (!await viewer.Browser.SendAsync(frame, WebSocketMessageType.Binary))
        {
            await CloseStalledAsync(viewer);
        }
    }

    private Task ExpireRequestAsync(ScreenStream stream, long requestId) =>
        EndControlPeriodAsync(stream, () => stream.TryExpireRequest(requestId), ControlReasons.Unsupported);

    // The wait ends early when the stream closes, and it is abandoned when the request has already been answered.
    private void ScheduleConfirmTimeout(ScreenStream stream, long requestId)
    {
        if (stream.LinkLifetime(CancellationToken.None) is { } linked)
        {
            _ = WaitForConfirmationAsync(stream, requestId, linked);
        }
    }

    private async Task WaitForConfirmationAsync(ScreenStream stream, long requestId, CancellationTokenSource linked)
    {
        using (linked)
        {
            try
            {
                await Task.Delay(ScreenProtocol.ControlConfirmTimeout, clock, linked.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }

        await ExpireRequestAsync(stream, requestId);
    }

    // The server's own PC is refused before anything else is checked. Either view mode may take control: a notified stream's PC
    // shows its controlling notice, while an anonymous stream's control is silent on the PC. Every control period is audited with its mode.
    private static string? ControlRefusalFor(bool isServerPc) =>
        isServerPc ? ControlReasons.ServerPc : null;

    private const string ReplacedReason = "A full view of this PC took this view's place.";

    private Task CloseStalledAsync(ScreenViewer viewer) =>
        CloseAsync(viewer, WebSocketCloseStatus.PolicyViolation, "The viewer stopped reading.");
}

public enum ScreenOpenStatus
{
    Opened,
    // The epoch moved on or the browser is gone.
    Refused,
    // The PC already has as many views as it may have, and none could make room.
    Busy,
}

// What an open did. Viewer is set only when it opened.
public readonly record struct ScreenOpenResult(ScreenOpenStatus Status, ScreenViewer? Viewer = null)
{
    public static readonly ScreenOpenResult Refused = new(ScreenOpenStatus.Refused);
    public static readonly ScreenOpenResult Busy = new(ScreenOpenStatus.Busy);
}
