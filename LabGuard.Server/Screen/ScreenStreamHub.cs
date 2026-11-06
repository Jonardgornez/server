using System.Net.WebSockets;
using System.Security.Cryptography;

namespace LabGuard.Server.Screen;

// The in-memory registry of live views: at most one viewer per PC. Frames are relayed through it and never stored.
// It holds no frame beyond the one being sent, and a closed view can't be attached to or relay anything again.
public sealed class ScreenStreamHub(ScreenViewAudit audit, TimeProvider clock)
{
    private readonly Lock _sync = new();
    private readonly Dictionary<int, ScreenViewer> _viewersByPc = [];

    // A new viewer replaces the old one for the same PC, which is closed with 4001.
    public async Task<ScreenViewer> OpenAsync(int pcId, string mode, DateTimeOffset sessionDeadline, int auditId, ScreenSocket browser)
    {
        var viewer = new ScreenViewer(NewViewId(), pcId, mode, auditId, sessionDeadline, browser);
        ScreenViewer? replaced;
        lock (_sync)
        {
            _viewersByPc.Remove(pcId, out replaced);
            _viewersByPc[pcId] = viewer;
        }

        if (replaced is not null)
        {
            await CloseAsync(replaced, ScreenProtocol.ReplacedCloseStatus, "Replaced by another viewer.");
        }

        if (!await browser.SendAsync(ScreenProtocol.WaitingMessage, WebSocketMessageType.Text))
        {
            await CloseStalledAsync(viewer);
        }

        return viewer;
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

    // False when the view has already closed. A PC socket that replaces another gets the old one closed with 4001.
    public async Task<bool> AttachPcAsync(ScreenViewer viewer, ScreenSocket pc)
    {
        if (!viewer.TryAttachPc(pc, out var replaced))
        {
            return false;
        }

        if (replaced is not null)
        {
            await replaced.CloseAsync(ScreenProtocol.ReplacedCloseStatus, "Replaced by another connection.");
        }

        await RelayStateAsync(viewer, ScreenProtocol.LiveMessage);
        return true;
    }

    public async Task DetachPcAsync(ScreenViewer viewer, ScreenSocket pc)
    {
        if (viewer.TryDetachPc(pc))
        {
            await RelayStateAsync(viewer, ScreenProtocol.WaitingMessage);
        }
    }

    // A frame goes out only while the PC has a credit. Otherwise it is dropped on the spot, so nothing queues up.
    // A frame that arrives after the administrator's session has run out closes the view instead, so none is sent late.
    public async Task RelayFrameAsync(ScreenViewer viewer, ReadOnlyMemory<byte> frame)
    {
        if (clock.GetUtcNow() >= viewer.SessionDeadline)
        {
            await CloseAsync(viewer, ScreenProtocol.SessionEndedCloseStatus, ScreenProtocol.SessionEndedReason);
            return;
        }

        if (!viewer.TryTakeFrameCredit())
        {
            return;
        }

        if (!await viewer.Browser.SendAsync(frame, WebSocketMessageType.Binary))
        {
            await CloseStalledAsync(viewer);
        }
    }

    public async Task RelayStateAsync(ScreenViewer viewer, ReadOnlyMemory<byte> stateMessage)
    {
        if (!await viewer.Browser.SendAsync(stateMessage, WebSocketMessageType.Text))
        {
            await CloseStalledAsync(viewer);
        }
    }

    // Each browser ack returns one credit and is passed on to the PC, which may then send its next frame.
    public async Task RelayAckAsync(ScreenViewer viewer)
    {
        viewer.ReturnFrameCredit();
        if (viewer.CurrentPc is { } pc)
        {
            await pc.SendAsync(ScreenProtocol.AckMessage, WebSocketMessageType.Text);
        }
    }

    // Records the end, then closes the view and its PC socket with the same code. Only the first call does anything.
    // The end is recorded first, so by the time a peer sees the close, the audit row is already final.
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

        await audit.EndAsync(viewer.AuditId, clock.GetUtcNow());
        await Task.WhenAll(
            viewer.Browser.CloseAsync(status, reason),
            pc?.CloseAsync(status, reason) ?? Task.CompletedTask);
        viewer.CancelLifetime();
    }

    public async Task CloseAllAsync(WebSocketCloseStatus status, string reason)
    {
        await Task.WhenAll(Snapshot().Select(viewer => CloseAsync(viewer, status, reason)));
    }

    private Task CloseStalledAsync(ScreenViewer viewer) =>
        CloseAsync(viewer, WebSocketCloseStatus.PolicyViolation, "The viewer stopped reading.");

    private static string NewViewId() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
}
