using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;

namespace LabGuard.Server.Screen;

// The in-memory registry of live views: at most one viewer per PC. Frames are relayed through it and never stored.
// It holds no frame beyond the one being sent, and a closed view can't be attached to or relay anything again.
public sealed class ScreenStreamHub(ScreenViewAudit audit, ScreenControlAudit controlAudit, TimeProvider clock)
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
        string adminUserName,
        string pcName)
    {
        var viewer = new ScreenViewer(NewViewId(), pcId, mode, auditId, sessionDeadline, browser, adminUserName, pcName);
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

        await SetStateAsync(viewer, ScreenProtocol.LiveState, requiredPc: pc);
        return true;
    }

    public async Task DetachPcAsync(ScreenViewer viewer, ScreenSocket pc)
    {
        if (viewer.TryDetachPc(pc))
        {
            await EndControlAsync(viewer, ControlReasons.Connection);
            await SetStateAsync(viewer, ScreenProtocol.WaitingState, requiredPc: null);
        }
    }

    // A PC's own state change (paused or resumed). Ignored unless that PC is the attached one.
    public Task RelayPcStateAsync(ScreenViewer viewer, ScreenSocket pc, string state) =>
        SetStateAsync(viewer, state, requiredPc: pc);

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

    // Each browser ack returns one credit and is passed on to the PC. An ack with no outstanding credit is not passed on.
    public async Task RelayAckAsync(ScreenViewer viewer)
    {
        if (!viewer.TryReturnFrameCredit())
        {
            return;
        }

        if (viewer.CurrentPc is { } pc)
        {
            await pc.SendAsync(ScreenProtocol.AckMessage, WebSocketMessageType.Text);
        }
    }

    // The administrator asks for control. Refused when the view is anonymous, when the PC is the server's own, or when no PC is attached.
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

            if (ControlRefusalFor(viewer, isServerPc) is { } refusal)
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
            await request.Pc.SendAsync(ScreenControlMessages.PcOnMessage, WebSocketMessageType.Text);
            ScheduleConfirmTimeout(viewer, request.Id);
        }
        finally
        {
            viewer.StateGate.Release();
        }
    }

    // The PC has shown the notice and will replay input. Only the attached PC that was asked can confirm, while the request is pending.
    // The audit row is written before control turns on, so a failed write leaves control off rather than unrecorded.
    public async Task ConfirmControlAsync(ScreenViewer viewer, ScreenSocket pc)
    {
        await viewer.StateGate.WaitAsync();
        try
        {
            if (!viewer.IsAwaitingConfirmation(pc))
            {
                return;
            }

            var auditId = await controlAudit.StartAsync(viewer.AdminUserName, viewer.PcName, clock.GetUtcNow(), CancellationToken.None);
            if (!viewer.TryConfirmControl(pc, auditId))
            {
                // The view closed while the row was being written, so the row is ended at once.
                await controlAudit.EndAsync(auditId, clock.GetUtcNow());
                return;
            }

            await SendControlMessageAsync(viewer, ScreenControlMessages.OnMessage);
        }
        finally
        {
            viewer.StateGate.Release();
        }
    }

    // Ends control for a reason: the administrator stopped it, the PC ended it, or the connection that held it went away.
    public Task EndControlAsync(ScreenViewer viewer, string reason) =>
        EndControlPeriodAsync(viewer, viewer.TryEndControl, reason);

    // The administrator's control off. It is always answered as stopped, even when no control is in progress.
    // The PC is told off only if it was asked on, which is the case while requesting or on.
    public Task StopControlAsync(ScreenViewer viewer) =>
        EndControlPeriodAsync(viewer, viewer.TryEndControl, ControlReasons.Stopped, answerWhenIdle: true);

    // Input the browser sent. It is relayed in its canonical form, to the attached PC, only while control is on and within the cap.
    // Nothing is queued: an input that can't go out now is dropped.
    public async Task RelayInputAsync(ScreenViewer viewer, BrowserControlMessage input)
    {
        var now = clock.GetUtcNow();
        if (now >= viewer.SessionDeadline)
        {
            await CloseAsync(viewer, ScreenProtocol.SessionEndedCloseStatus, ScreenProtocol.SessionEndedReason);
            return;
        }

        if (viewer.TakeControlInputTarget(input.Rate, now) is not { } pc)
        {
            return;
        }

        await pc.SendAsync(Encoding.UTF8.GetBytes(input.Canonical), WebSocketMessageType.Text);
    }

    // Closes the view and its sockets with the same code, then ends the audit rows. Only the first call does anything.
    // The sockets close first, so the administrator sees the close without waiting for the database.
    public async Task CloseAsync(ScreenViewer viewer, WebSocketCloseStatus status, string reason)
    {
        if (!viewer.TryClose(out var pc))
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
                pc?.CloseAsync(status, reason) ?? Task.CompletedTask);

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
                await controlAudit.EndAsync(controlAuditId, clock.GetUtcNow());
            }

            await audit.EndAsync(viewer.AuditId, clock.GetUtcNow());
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

    // Records the state if it changed, and sends it. Only a change is sent, so repeated states don't reach the browser.
    private async Task SetStateAsync(ScreenViewer viewer, string state, ScreenSocket? requiredPc)
    {
        await viewer.StateGate.WaitAsync();
        try
        {
            if (!viewer.TrySetState(state, requiredPc))
            {
                return;
            }

            if (!await viewer.Browser.SendAsync(ScreenProtocol.StateMessage(state), WebSocketMessageType.Text))
            {
                await CloseStalledAsync(viewer);
            }
        }
        finally
        {
            viewer.StateGate.Release();
        }
    }

    // Ends the control period if the condition still holds, then tells the browser and the attached PC, and records the end.
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

            await SendControlMessageAsync(viewer, ScreenControlMessages.OffMessage(reason));
            if (ended?.AttachedPc is { } pc)
            {
                await pc.SendAsync(ScreenControlMessages.PcOffMessage, WebSocketMessageType.Text);
            }
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

    // The first refusal that applies. Anonymous views and the server's own PC are refused before anything else is checked.
    private static string? ControlRefusalFor(ScreenViewer viewer, bool isServerPc) =>
        viewer.Mode != ScreenProtocol.NotifiedMode ? ControlReasons.Anonymous
        : isServerPc ? ControlReasons.ServerPc
        : null;

    private Task CloseStalledAsync(ScreenViewer viewer) =>
        CloseAsync(viewer, WebSocketCloseStatus.PolicyViolation, "The viewer stopped reading.");

    private static string NewViewId() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
}
