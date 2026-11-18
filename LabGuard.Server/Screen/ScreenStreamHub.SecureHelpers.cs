using System.Net.WebSockets;

namespace LabGuard.Server.Screen;

// The secure-desktop helpers, at most one per PC: a newer connection replaces the older one. A helper idles until a view of its
// PC is open and the PC's stream is paused. It then streams and takes input in the PC's place until the PC resumes.
public sealed partial class ScreenStreamHub
{
    private readonly Dictionary<int, SecureHelperConnection> _helpersByPc = [];
    private readonly Dictionary<int, DateTimeOffset> _lastHelperAttachByPc = [];

    // A helper may reconnect at most once per cooldown, like the PC's stream. False when it reconnected too soon.
    public bool TryReserveHelperAttach(int pcId, DateTimeOffset now)
    {
        lock (_sync)
        {
            if (_lastHelperAttachByPc.TryGetValue(pcId, out var last) && now - last < ScreenProtocol.AttachCooldown)
            {
                return false;
            }

            _lastHelperAttachByPc[pcId] = now;
            return true;
        }
    }

    // Registers the helper in place of any older one for its PC. A paused view of the PC switches to the new helper first,
    // then the older helper is closed with 4001.
    public async Task AttachHelperAsync(SecureHelperConnection helper)
    {
        SecureHelperConnection? replaced;
        lock (_sync)
        {
            _helpersByPc.Remove(helper.PcId, out replaced);
            _helpersByPc[helper.PcId] = helper;
        }

        try
        {
            if (CurrentFor(helper.PcId) is { } viewer)
            {
                await SwitchSourceAsync(viewer);
            }
        }
        finally
        {
            // Closed however the switch ended, so a failed switch never leaves the replaced helper connected.
            if (replaced is not null)
            {
                await CloseHelperSocketAsync(replaced, ScreenProtocol.ReplacedCloseStatus, "Replaced by another helper connection.");
            }
        }
    }

    // The helper's connection ended. A view it was streaming for falls back to the PC's own state.
    public async Task DetachHelperAsync(SecureHelperConnection helper)
    {
        lock (_sync)
        {
            if (ReferenceEquals(_helpersByPc.GetValueOrDefault(helper.PcId), helper))
            {
                _helpersByPc.Remove(helper.PcId);
            }
        }

        if (CurrentFor(helper.PcId) is { } viewer)
        {
            await SwitchSourceAsync(viewer);
        }
    }

    public bool IsCurrentHelper(SecureHelperConnection helper)
    {
        lock (_sync)
        {
            return ReferenceEquals(_helpersByPc.GetValueOrDefault(helper.PcId), helper);
        }
    }

    public IReadOnlyList<SecureHelperConnection> HelperSnapshot()
    {
        lock (_sync)
        {
            return _helpersByPc.Values.ToList();
        }
    }

    // Closes the PC's helper, if it has one. Used when the PC is removed.
    public async Task CloseHelperForPcAsync(int pcId, WebSocketCloseStatus status, string reason)
    {
        SecureHelperConnection? helper;
        lock (_sync)
        {
            helper = _helpersByPc.GetValueOrDefault(pcId);
        }

        if (helper is not null)
        {
            await CloseHelperAsync(helper, status, reason);
        }
    }

    // Unregisters the helper and closes its socket. Its receive loop then ends, and a view it was streaming for falls back.
    public async Task CloseHelperAsync(SecureHelperConnection helper, WebSocketCloseStatus status, string reason)
    {
        lock (_sync)
        {
            if (ReferenceEquals(_helpersByPc.GetValueOrDefault(helper.PcId), helper))
            {
                _helpersByPc.Remove(helper.PcId);
            }
        }

        if (CurrentFor(helper.PcId) is { } viewer)
        {
            await SwitchSourceAsync(viewer);
        }

        await CloseHelperSocketAsync(helper, status, reason);
    }

    // Closes every helper. Used when the server stops. A logout doesn't, since helpers belong to PCs, not administrators.
    public Task CloseAllHelpersAsync(WebSocketCloseStatus status, string reason) =>
        Task.WhenAll(HelperSnapshot().Select(helper => CloseHelperAsync(helper, status, reason)));

    // A frame from a helper. Relayed only while it streams for an open view, with the same credits as the PC's frames.
    public async Task RelayHelperFrameAsync(SecureHelperConnection helper, ReadOnlyMemory<byte> frame)
    {
        if (CurrentFor(helper.PcId) is { } viewer)
        {
            await RelayFrameAsync(viewer, helper.Socket, frame);
        }
    }

    // Whether a frame the helper sent is allowed at all. Within the frame cap, it is allowed while the helper streams for an open
    // view, and for a short grace after it was told to stop, since frames already on the way are not its fault. Otherwise the
    // helper is sending unasked, and the caller closes it.
    public bool IsHelperFrameAllowed(SecureHelperConnection helper)
    {
        var now = clock.GetUtcNow();
        if (!helper.TryTakeFrameSlot(now))
        {
            return false;
        }

        if (CurrentFor(helper.PcId) is { } viewer && viewer.IsCurrentSource(helper.Socket))
        {
            return true;
        }

        return helper.LastStoppedAt is { } stoppedAt && now - stoppedAt < SecureHelperProtocol.StopGrace;
    }

    private ScreenSocket? ConnectedHelperFor(int pcId)
    {
        lock (_sync)
        {
            return _helpersByPc.GetValueOrDefault(pcId)?.Socket;
        }
    }

    // Brings the view's source in line with its PC and helper, and sends what the change asks for. Everything is sent with the
    // state gate held, so the browser and the sockets hear the changes in order. A control period the change ended is recorded after.
    private async Task SwitchSourceAsync(ScreenViewer viewer)
    {
        ControlPeriodEnd? ended = null;
        await viewer.StateGate.WaitAsync();
        try
        {
            SourceSwitch change;
            // The switch and its messages happen under the input gate, so no input is chosen for the old source and sent after
            // that source was released or stopped.
            await viewer.InputGate.WaitAsync();
            try
            {
                if (viewer.TrySwitchSource(ConnectedHelperFor(viewer.PcId)) is not { } switched)
                {
                    return;
                }

                change = switched;
                await SendSwitchMessagesAsync(viewer, change);
                // The view may have closed while the helper was being started. Its close might have told the helper to stop before
                // the start went out, so the helper is told again and never stays started with no view.
                if (change.StartedHelper is { } started && viewer.IsClosed)
                {
                    await StopHelperAsync(started);
                }
            }
            finally
            {
                viewer.InputGate.Release();
            }

            ended = change.EndedControl;
            if (ended is not null)
            {
                await SendControlMessageAsync(viewer, ScreenControlMessages.OffMessage(ControlReasons.Connection));
                await SendPcControlOffAsync(ended);
            }

            if (change.State is { } state && !await viewer.Browser.SendAsync(ScreenProtocol.StateMessage(state), WebSocketMessageType.Text))
            {
                await CloseStalledAsync(viewer);
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

    // The helper's stop when its view closes. It waits, briefly, for a switch that is sending, so the stop goes out after that
    // switch's start. A close never waits longer than that bound.
    private async Task StopHelperOnCloseAsync(ScreenViewer viewer, ScreenSocket helper)
    {
        var isGated = await viewer.InputGate.WaitAsync(ScreenProtocol.ControlInputSendTimeout);
        try
        {
            await StopHelperAsync(helper);
        }
        finally
        {
            if (isGated)
            {
                viewer.InputGate.Release();
            }
        }
    }

    // Tells the helper to stop, and notes when, so the frames it already had on the way are dropped rather than refused.
    private async Task StopHelperAsync(ScreenSocket helper)
    {
        SecureHelperConnection? connection;
        lock (_sync)
        {
            connection = _helpersByPc.Values.FirstOrDefault(candidate => ReferenceEquals(candidate.Socket, helper));
        }

        connection?.MarkStopped(clock.GetUtcNow());
        await SendOrAbortAsync(helper, SecureHelperProtocol.StopMessage);
    }

    private async Task SendSwitchMessagesAsync(ScreenViewer viewer, SourceSwitch change)
    {
        if (change.StoppedHelper is { } stopped)
        {
            await StopHelperAsync(stopped);
        }

        if (change.ReleasedPc is { } released)
        {
            await SendOrAbortAsync(released, ScreenControlMessages.PcReleaseMessage);
        }

        if (change.StartedHelper is { } started)
        {
            await SendOrAbortAsync(started, SecureHelperProtocol.StartMessage(viewer.Mode));
        }

        if (change.ControlSource is { } source)
        {
            await SendOrAbortAsync(source, ScreenControlMessages.PcOnMessage);
        }
    }

    // Tells the PC and the streaming helper that control is off.
    private static async Task SendPcControlOffAsync(ControlPeriodEnd? ended)
    {
        if (ended?.AttachedPc is { } pc)
        {
            await pc.SendAsync(ScreenControlMessages.PcOffMessage, WebSocketMessageType.Text);
        }

        if (ended?.Helper is { } helper)
        {
            await helper.SendAsync(ScreenControlMessages.PcOffMessage, WebSocketMessageType.Text);
        }
    }

    // Bounded, so one slow peer can't hold up the others. A peer that doesn't take the message in time is cut off.
    private static async Task SendOrAbortAsync(ScreenSocket socket, ReadOnlyMemory<byte> message)
    {
        if (!await socket.SendAsync(message, WebSocketMessageType.Text, ScreenProtocol.ControlInputSendTimeout))
        {
            socket.Abort();
        }
    }

    // The peer gets a moment to receive the close code before the connection is stopped.
    private static async Task CloseHelperSocketAsync(SecureHelperConnection helper, WebSocketCloseStatus status, string reason)
    {
        await helper.Socket.CloseAsync(status, reason);
        await helper.Socket.WaitForPeerCloseAsync(ScreenProtocol.CloseHandshakeTimeout);
        helper.Socket.Abort();
    }
}
