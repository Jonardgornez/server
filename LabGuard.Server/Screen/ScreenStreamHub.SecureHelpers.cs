using System.Net.WebSockets;

namespace LabGuard.Server.Screen;

// The secure-desktop helpers, at most one per PC: a newer connection replaces the older one. A helper idles until a view of its
// PC is open and the PC's stream is paused. It then streams and takes input in the PC's place until the PC resumes. Its frames go
// to every view of the PC, like the PC's own.
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

    // Registers the helper in place of any older one for its PC. A paused stream of the PC switches to the new helper first,
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
            if (StreamFor(helper.PcId) is { } stream)
            {
                await SwitchSourceAsync(stream);
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

    // The helper's connection ended. A stream it was streaming for falls back to the PC's own state.
    public async Task DetachHelperAsync(SecureHelperConnection helper)
    {
        lock (_sync)
        {
            if (ReferenceEquals(_helpersByPc.GetValueOrDefault(helper.PcId), helper))
            {
                _helpersByPc.Remove(helper.PcId);
            }
        }

        if (StreamFor(helper.PcId) is { } stream)
        {
            await SwitchSourceAsync(stream);
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

    // Unregisters the helper and closes its socket. Its receive loop then ends, and a stream it was streaming for falls back.
    public async Task CloseHelperAsync(SecureHelperConnection helper, WebSocketCloseStatus status, string reason)
    {
        lock (_sync)
        {
            if (ReferenceEquals(_helpersByPc.GetValueOrDefault(helper.PcId), helper))
            {
                _helpersByPc.Remove(helper.PcId);
            }
        }

        if (StreamFor(helper.PcId) is { } stream)
        {
            await SwitchSourceAsync(stream);
        }

        await CloseHelperSocketAsync(helper, status, reason);
    }

    // Closes every helper. Used when the server stops. A logout doesn't, since helpers belong to PCs, not administrators.
    public Task CloseAllHelpersAsync(WebSocketCloseStatus status, string reason) =>
        Task.WhenAll(HelperSnapshot().Select(helper => CloseHelperAsync(helper, status, reason)));

    // A frame from a helper. Relayed only while it streams for an open stream, with the same credits as the PC's frames.
    public async Task RelayHelperFrameAsync(SecureHelperConnection helper, ReadOnlyMemory<byte> frame)
    {
        if (StreamFor(helper.PcId) is { } stream)
        {
            await RelayFrameAsync(stream, helper.Socket, frame);
        }
    }

    // Whether a frame the helper sent is allowed at all. Within the frame cap, it is allowed while the helper streams for an open
    // view, while it was started and its stop hasn't gone out yet (the stop may wait behind a gate for a while), and for a short
    // grace after the stop, since frames already on the way are not its fault. Otherwise the helper is sending unasked, and the
    // caller closes it. Allowed is not relayed: a frame reaches browsers only while the helper is its stream's current source.
    //
    // The helper's frames carry no run id, so the server can't tell a frame of an old run from one of the run it just started. The
    // command gate keeps starts and stops in order, and a stream takes the helper's frames only while it is the source, so the gap
    // left is theoretical: a frame captured under an old start that arrives after a newer start. Closing it fully needs the client:
    // a run id in the start message that the helper echoes with each frame, so frames of any other run are dropped.
    public bool IsHelperFrameAllowed(SecureHelperConnection helper)
    {
        var now = clock.GetUtcNow();
        if (!helper.TryTakeFrameSlot(now))
        {
            return false;
        }

        if (helper.StreamingFor is not null || (StreamFor(helper.PcId) is { } stream && stream.IsCurrentSource(helper.Socket)))
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

    // Brings the stream's source in line with its PC and helper, and sends what the change asks for. Everything is sent with the
    // state gate held, so the browsers and the sockets hear the changes in order. Control messages go to the controller only, and
    // the new state to every view. A control period the change ended is recorded after.
    private async Task SwitchSourceAsync(ScreenStream stream)
    {
        ControlPeriodEnd? ended = null;
        await stream.StateGate.WaitAsync();
        try
        {
            SourceSwitch change;
            // The switch and its messages happen under the input gate, so no input is chosen for the old source and sent after
            // that source was released or stopped.
            await stream.InputGate.WaitAsync();
            try
            {
                if (stream.TrySwitchSource(ConnectedHelperFor(stream.PcId)) is not { } switched)
                {
                    return;
                }

                change = switched;
                await SendSwitchMessagesAsync(stream, change);
            }
            finally
            {
                stream.InputGate.Release();
            }

            if (change.ResetCursor && stream.Controller is { } controller)
            {
                await SendControlMessageAsync(controller, PcCursorMessage.BrowserDefaultMessage);
            }

            ended = change.EndedControl;
            if (ended is { } end)
            {
                if (end.Controller is { } previousController)
                {
                    await SendCursorResetAsync(previousController, end);
                    await SendControlMessageAsync(previousController, ScreenControlMessages.OffMessage(ControlReasons.Connection));
                }

                await SendPcControlOffAsync(end);
            }

            if (change.State is { } state)
            {
                await Task.WhenAll(stream.Viewers.Select(viewer => SendStateAsync(viewer, state)));
            }
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

    // The helper's stop when its stream closes. It waits, briefly, for a switch that is sending, so the stop goes out after that
    // switch's start. A close never waits longer than that bound, and the helper's command gate keeps the order after it anyway.
    private async Task StopHelperOnCloseAsync(ScreenStream stream, ScreenSocket helper)
    {
        var isGated = await stream.InputGate.WaitAsync(ScreenProtocol.ControlInputSendTimeout);
        try
        {
            await StopHelperAsync(stream, helper);
        }
        finally
        {
            if (isGated)
            {
                stream.InputGate.Release();
            }
        }
    }

    // Starts the helper for the stream, in the stream's mode, unless the stream has closed or is no longer the PC's stream by the
    // time the helper's command gate is free. So an old stream's late start can't restart the helper in that stream's mode.
    private async Task StartHelperAsync(ScreenStream stream, ScreenSocket helper)
    {
        if (HelperConnectionFor(helper) is not { } connection)
        {
            return;
        }

        await connection.CommandGate.WaitAsync();
        try
        {
            if (stream.IsClosed || !ReferenceEquals(StreamFor(stream.PcId), stream))
            {
                return;
            }

            connection.StreamingFor = stream;
            await SendOrAbortAsync(helper, SecureHelperProtocol.StartMessage(stream.Mode));
        }
        finally
        {
            connection.CommandGate.Release();
        }
    }

    // Tells the helper to stop for the stream, and notes when, so the frames it already had on the way are dropped rather than
    // refused. Skipped when another stream of the PC has started the helper since, so an old stream's stop can't end its run.
    private async Task StopHelperAsync(ScreenStream stream, ScreenSocket helper)
    {
        if (HelperConnectionFor(helper) is not { } connection)
        {
            // A helper that was replaced is being closed. It is still told to stop, so it stops streaming at once.
            await SendOrAbortAsync(helper, SecureHelperProtocol.StopMessage);
            return;
        }

        await connection.CommandGate.WaitAsync();
        try
        {
            if (connection.StreamingFor is { } current && !ReferenceEquals(current, stream))
            {
                return;
            }

            // Noted right before the stop goes out, so the grace for frames already on the way starts with it.
            connection.StreamingFor = null;
            connection.MarkStopped(clock.GetUtcNow());
            await SendOrAbortAsync(helper, SecureHelperProtocol.StopMessage);
        }
        finally
        {
            connection.CommandGate.Release();
        }
    }

    private SecureHelperConnection? HelperConnectionFor(ScreenSocket helper)
    {
        lock (_sync)
        {
            return _helpersByPc.Values.FirstOrDefault(candidate => ReferenceEquals(candidate.Socket, helper));
        }
    }

    private async Task SendSwitchMessagesAsync(ScreenStream stream, SourceSwitch change)
    {
        if (change.StoppedHelper is { } stopped)
        {
            await StopHelperAsync(stream, stopped);
        }

        if (change.ReleasedPc is { } released)
        {
            await SendOrAbortAsync(released, ScreenControlMessages.PcReleaseMessage);
        }

        if (change.StartedHelper is { } started)
        {
            await StartHelperAsync(stream, started);
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
