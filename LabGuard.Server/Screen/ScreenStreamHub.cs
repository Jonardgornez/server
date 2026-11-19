using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;

namespace LabGuard.Server.Screen;

// The in-memory registry of live views: at most one viewer per PC. Frames are relayed through it and never stored.
// It holds no frame beyond the one being sent, and a closed view can't be attached to or relay anything again.
// It also holds each PC's secure-desktop helper, which streams in the PC's place while the PC's stream is paused.
public sealed partial class ScreenStreamHub(ScreenViewAudit audit, ScreenControlAudit controlAudit, TimeProvider clock)
{
    private readonly Lock _sync = new();
    private readonly Dictionary<int, ScreenViewer> _viewersByPc = [];
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

    // Opens a view for the PC, replacing any older one. Returns null when the view is refused (the epoch moved on, or the browser
    // is gone). In every case that doesn't open, the audit row is ended and nothing stays registered.
    public async Task<ScreenViewer?> OpenAsync(
        int pcId,
        string mode,
        DateTimeOffset sessionDeadline,
        int auditId,
        ScreenSocket browser,
        long epoch,
        ScreenViewOwner owner,
        string pcName)
    {
        var viewer = new ScreenViewer(NewViewId(), pcId, mode, auditId, sessionDeadline, browser, owner, pcName);
        var published = false;
        try
        {
            // The waiting state goes out before the viewer can be found, so a later live state can't overtake it.
            viewer.TrySetState(ScreenProtocol.WaitingState);
            if (!await browser.SendAsync(ScreenProtocol.StateMessage(ScreenProtocol.WaitingState), WebSocketMessageType.Text))
            {
                return null;
            }

            ScreenViewer? replaced;
            lock (_sync)
            {
                if (epoch != _epoch)
                {
                    return null;
                }

                _viewersByPc.Remove(pcId, out replaced);
                _viewersByPc[pcId] = viewer;
                published = true;
            }

            if (replaced is not null)
            {
                await CloseAsync(replaced, ScreenProtocol.ReplacedCloseStatus, "Replaced by another viewer.");
            }

            return viewer;
        }
        catch when (published)
        {
            await CloseAsync(viewer, WebSocketCloseStatus.InternalServerError, "The view could not start.");
            throw;
        }
        finally
        {
            if (!published)
            {
                viewer.Dispose();
                await audit.EndAsync(auditId, clock.GetUtcNow());
            }
        }
    }

    public ScreenViewer? CurrentFor(int pcId)
    {
        lock (_sync)
        {
            return _viewersByPc.GetValueOrDefault(pcId);
        }
    }

    public IReadOnlyList<ScreenViewer> Snapshot()
    {
        lock (_sync)
        {
            return _viewersByPc.Values.ToList();
        }
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

    // False when the view has already closed. A PC socket that replaces another ends the old one at once.
    // Control that was held through the old socket ends with it, and the browser is told why.
    public async Task<bool> AttachPcAsync(ScreenViewer viewer, ScreenSocket pc)
    {
        if (!viewer.TryAttachPc(pc, out var replaced))
        {
            return false;
        }

        if (replaced is not null)
        {
            await EndControlAsync(viewer, ControlReasons.Connection);
            await replaced.CloseAsync(ScreenProtocol.ReplacedCloseStatus, "Replaced by another connection.");
            replaced.Abort();
        }

        await SwitchSourceAsync(viewer);
        return true;
    }

    // A helper streaming for the PC that left is told to stop, and the browser hears that the view waits.
    public async Task DetachPcAsync(ScreenViewer viewer, ScreenSocket pc)
    {
        if (viewer.TryDetachPc(pc))
        {
            await EndControlAsync(viewer, ControlReasons.Connection);
            await SwitchSourceAsync(viewer);
        }
    }

    // The PC says it can't capture its screen (paused) or can again (resumed). Ignored unless that PC is the attached one.
    // While paused, a connected helper streams in its place and the browser hears secure. With no helper the browser hears paused.
    public async Task SetPcPausedAsync(ScreenViewer viewer, ScreenSocket pc, bool isPaused)
    {
        if (viewer.TrySetPcPaused(pc, isPaused))
        {
            await SwitchSourceAsync(viewer);
        }
    }

    // A frame goes out only while the attached PC has a credit. Otherwise it is dropped on the spot, so nothing queues up.
    // A frame that arrives after the administrator's session has run out closes the view instead, so none is sent late.
    public async Task RelayFrameAsync(ScreenViewer viewer, ScreenSocket pc, ReadOnlyMemory<byte> frame)
    {
        if (clock.GetUtcNow() >= viewer.SessionDeadline)
        {
            await CloseAsync(viewer, ScreenProtocol.SessionEndedCloseStatus, ScreenProtocol.SessionEndedReason);
            return;
        }

        if (!viewer.TryTakeFrameCredit(pc))
        {
            return;
        }

        if (!await viewer.Browser.SendAsync(frame, WebSocketMessageType.Binary))
        {
            await CloseStalledAsync(viewer);
        }
    }

    // Each browser ack returns one credit and is passed on to the sender of the oldest frame, the PC or the helper.
    // An ack with no outstanding credit is not passed on, and neither is one for a sender that is no longer the source.
    public async Task RelayAckAsync(ScreenViewer viewer)
    {
        if (viewer.TryReturnFrameCredit(out var sender) && sender is not null)
        {
            await sender.SendAsync(ScreenProtocol.AckMessage, WebSocketMessageType.Text);
        }
    }

    // The administrator asks for control, from either view mode. Refused when the PC is the server's own, or when no PC is attached.
    // The PC is asked with control on, and has ScreenProtocol.ControlConfirmTimeout to confirm before the request is abandoned.
    public async Task RequestControlAsync(ScreenViewer viewer, bool isServerPc)
    {
        await viewer.StateGate.WaitAsync();
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

            if (viewer.TryBeginControlRequest() is not { } request)
            {
                await SendControlMessageAsync(viewer, ScreenControlMessages.OffMessage(ControlReasons.Waiting));
                return;
            }

            await SendControlMessageAsync(viewer, ScreenControlMessages.RequestingMessage);
            // Bounded, and a PC that doesn't take the request in time is cut off, which ends the request through its connection.
            if (!await request.Pc.SendAsync(ScreenControlMessages.PcOnMessage, WebSocketMessageType.Text, ScreenProtocol.ControlInputSendTimeout))
            {
                request.Pc.Abort();
            }

            ScheduleConfirmTimeout(viewer, request.Id);
        }
        finally
        {
            viewer.StateGate.Release();
        }
    }

    // Whether a control request may go on to the server-PC check. Only an off view, before its deadline and within the request rate.
    // Nothing here reads the database, so a flood of requests costs little.
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
    // The audit row is written before control turns on, so control is never on unrecorded. The write is bounded. A write that fails or
    // runs out of time ends the request as unsupported, and the PC stream stays open.
    public async Task ConfirmControlAsync(ScreenViewer viewer, ScreenSocket pc)
    {
        if (!viewer.IsAwaitingConfirmation(pc))
        {
            return;
        }

        int auditId;
        try
        {
            using var timeout = new CancellationTokenSource(ScreenProtocol.ControlStartTimeout);
            auditId = await controlAudit.StartAsync(viewer.AdminUserName, viewer.PcName, viewer.Mode, clock.GetUtcNow(), timeout.Token);
        }
        catch (Exception)
        {
            // Any failure to record the period ends the request, so control is never turned on without its row.
            await EndControlPeriodAsync(viewer, () => viewer.TryEndRequestFrom(pc), ControlReasons.Unsupported);
            return;
        }

        var confirmed = false;
        await viewer.StateGate.WaitAsync();
        try
        {
            confirmed = viewer.TryConfirmControl(pc, auditId);
            if (confirmed)
            {
                await SendControlMessageAsync(viewer, ScreenControlMessages.OnMessage);
            }
        }
        finally
        {
            viewer.StateGate.Release();
        }

        if (!confirmed)
        {
            // The request ended or the view closed while the row was being written, so the row is ended at once.
            await controlAudit.EndAsync(auditId, clock.GetUtcNow());
        }
    }

    // Ends control for a reason: the administrator stopped it, or the connection that held it went away.
    public Task EndControlAsync(ScreenViewer viewer, string reason) =>
        EndControlPeriodAsync(viewer, viewer.TryEndControl, reason);

    // The PC or the helper says control has ended. Only the socket that carries control can end it, so the paused PC can't end
    // control that the helper carries.
    public Task EndControlFromAsync(ScreenViewer viewer, ScreenSocket source) =>
        EndControlPeriodAsync(viewer, () => viewer.TryEndControlFrom(source), ControlReasons.PcEnded);

    // The administrator's control off. It is always answered as stopped, even when no control is in progress.
    // The PC is told off only if it was asked on, which is the case while requesting or on.
    public Task StopControlAsync(ScreenViewer viewer) =>
        EndControlPeriodAsync(viewer, viewer.TryEndControl, ControlReasons.Stopped, answerWhenIdle: true);

    // Input the browser sent. It is relayed in its canonical form, only while control is on and within the cap, to the socket that
    // carries control. Ctrl+Alt+Del goes to the PC's helper whenever one is connected, since only the helper can have it sent.
    // Nothing is queued: an input that can't go out now is dropped.
    public async Task RelayInputAsync(ScreenViewer viewer, BrowserControlMessage input)
    {
        var now = clock.GetUtcNow();
        if (now >= viewer.SessionDeadline)
        {
            await CloseAsync(viewer, ScreenProtocol.SessionEndedCloseStatus, ScreenProtocol.SessionEndedReason);
            return;
        }

        // The target is chosen and sent to under the input gate, so a switch of source can't slip between the two.
        await viewer.InputGate.WaitAsync();
        try
        {
            if (viewer.TakeControlInputTarget(input, now, ConnectedHelperFor(viewer.PcId)) is not { } pc)
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
            viewer.InputGate.Release();
        }
    }

    // The PC's pointer shape, already in the browser's canonical form. Relayed only while control is on and carried by this PC
    // socket, within the cursor cap. Sent under the state gate, so it can't arrive after the default sent when control ends.
    public async Task RelayCursorAsync(ScreenViewer viewer, ScreenSocket pc, ReadOnlyMemory<byte> browserCursor)
    {
        // A cheap first look, so a PC sending cursors while control is off never waits on the gate. The full check is under it.
        if (viewer.ControlState != ScreenControlState.On)
        {
            return;
        }

        await viewer.StateGate.WaitAsync();
        try
        {
            if (viewer.TryTakeCursorSlot(pc, clock.GetUtcNow()))
            {
                await SendControlMessageAsync(viewer, browserCursor);
            }
        }
        finally
        {
            viewer.StateGate.Release();
        }
    }

    // Closes the view and its sockets with the same code, then ends the audit rows. Only the first call does anything.
    // The sockets close first, so the administrator sees the close without waiting for the database. A helper that was streaming
    // is told to stop, and stays connected for the next view.
    public async Task CloseAsync(ScreenViewer viewer, WebSocketCloseStatus status, string reason)
    {
        // Taken before the close handshake, so both audit rows record the moment the view closed.
        var closedAt = clock.GetUtcNow();
        if (!viewer.TryClose(out var pc, out var helper))
        {
            return;
        }

        lock (_sync)
        {
            if (ReferenceEquals(_viewersByPc.GetValueOrDefault(viewer.PcId), viewer))
            {
                _viewersByPc.Remove(viewer.PcId);
            }
        }

        try
        {
            await Task.WhenAll(
                viewer.Browser.CloseAsync(status, reason),
                pc?.CloseAsync(status, reason) ?? Task.CompletedTask,
                helper is null ? Task.CompletedTask : StopHelperOnCloseAsync(viewer, helper));

            // Cancelling the lifetime resets the connection, so each peer first gets a moment to receive the close code.
            await Task.WhenAll(
                viewer.Browser.WaitForPeerCloseAsync(ScreenProtocol.CloseHandshakeTimeout),
                pc?.WaitForPeerCloseAsync(ScreenProtocol.CloseHandshakeTimeout) ?? Task.CompletedTask);
        }
        finally
        {
            viewer.CancelLifetime();
            // Control ends with the view. Nothing more is sent to the PC, since its socket is closing.
            if (viewer.EndControlOnClose() is { } controlAuditId)
            {
                await controlAudit.EndAsync(controlAuditId, closedAt);
            }

            await audit.EndAsync(viewer.AuditId, closedAt);
        }
    }

    // Ends every view. Each logout and password change calls this, and it bumps the epoch so a view still being opened is refused.
    public async Task CloseAllAsync(WebSocketCloseStatus status, string reason)
    {
        IReadOnlyList<ScreenViewer> viewers;
        lock (_sync)
        {
            _epoch++;
            viewers = _viewersByPc.Values.ToList();
        }

        await Task.WhenAll(viewers.Select(viewer => CloseAsync(viewer, status, reason)));
    }

    // Closes the views one administrator opened, when that administrator's sessions end.
    // The epoch still moves on, so a view that is being opened while the sessions end is refused.
    public async Task CloseAllOfAdminAsync(int adminId, WebSocketCloseStatus status, string reason)
    {
        IReadOnlyList<ScreenViewer> viewers;
        lock (_sync)
        {
            _epoch++;
            viewers = _viewersByPc.Values.Where(viewer => viewer.Owner.AdminId == adminId).ToList();
        }

        await Task.WhenAll(viewers.Select(viewer => CloseAsync(viewer, status, reason)));
    }

    // Ends the control period if the condition still holds, then tells the browser, the attached PC and the streaming helper,
    // and records the end.
    // The state gate is held for the change and the sends, so the browser hears them in order. The audit write happens after it.
    private async Task EndControlPeriodAsync(ScreenViewer viewer, Func<ControlPeriodEnd?> tryEnd, string reason, bool answerWhenIdle = false)
    {
        ControlPeriodEnd? ended;
        await viewer.StateGate.WaitAsync();
        try
        {
            ended = tryEnd();
            if (ended is null && !answerWhenIdle)
            {
                return;
            }

            await SendCursorResetAsync(viewer, ended);
            await SendControlMessageAsync(viewer, ScreenControlMessages.OffMessage(reason));
            await SendPcControlOffAsync(ended);
        }
        finally
        {
            viewer.StateGate.Release();
        }

        if (ended?.AuditId is { } auditId)
        {
            await controlAudit.EndAsync(auditId, clock.GetUtcNow());
        }
    }

    // Called with the state gate held.
    private async Task SendControlMessageAsync(ScreenViewer viewer, ReadOnlyMemory<byte> message)
    {
        if (!await viewer.Browser.SendAsync(message, WebSocketMessageType.Text))
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

    private Task ExpireRequestAsync(ScreenViewer viewer, long requestId) =>
        EndControlPeriodAsync(viewer, () => viewer.TryExpireRequest(requestId), ControlReasons.Unsupported);

    // The wait ends early when the view closes, and it is abandoned when the request has already been answered.
    private void ScheduleConfirmTimeout(ScreenViewer viewer, long requestId)
    {
        if (viewer.LinkLifetime(CancellationToken.None) is { } linked)
        {
            _ = WaitForConfirmationAsync(viewer, requestId, linked);
        }
    }

    private async Task WaitForConfirmationAsync(ScreenViewer viewer, long requestId, CancellationTokenSource linked)
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

        await ExpireRequestAsync(viewer, requestId);
    }

    // The server's own PC is refused before anything else is checked. Either view mode may take control: a notified view's PC
    // shows its controlling notice, while an anonymous view's control is silent on the PC. Every control period is audited with its mode.
    private static string? ControlRefusalFor(bool isServerPc) =>
        isServerPc ? ControlReasons.ServerPc : null;

    private Task CloseStalledAsync(ScreenViewer viewer) =>
        CloseAsync(viewer, WebSocketCloseStatus.PolicyViolation, "The viewer stopped reading.");

    private static string NewViewId() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
}
