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

        if (CurrentFor(helper.PcId) is { } viewer)
        {
            await SwitchSourceAsync(viewer);
        }

        if (replaced is not null)
        {
            await CloseHelperSocketAsync(replaced, ScreenProtocol.ReplacedCloseStatus, "Replaced by another helper connection.");
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
            if (viewer.TrySwitchSource(ConnectedHelperFor(viewer.PcId)) is not { } change)
            {
                return;
            }

            ended = change.EndedControl;
            await SendSwitchMessagesAsync(viewer, change);
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

    private static async Task SendSwitchMessagesAsync(ScreenViewer viewer, SourceSwitch change)
    {
        if (change.StoppedHelper is { } stopped)
        {
            await SendOrAbortAsync(stopped, SecureHelperProtocol.StopMessage);
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
