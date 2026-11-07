using System.Net.WebSockets;
using LabGuard.Server.Auth;
using LabGuard.Server.Data;
using LabGuard.Server.Screen;

namespace LabGuard.Server.Clients;

// The PC's end of a live view. It connects only while a heartbeat reports a viewer, and streams JPEG frames until the view ends.
public static partial class ClientEndpoints
{
    private static async Task<IResult> ScreenStreamAsync(string? viewId, HttpContext http, AppDbContext db, ScreenStreamHub hub, TimeProvider clock)
    {
        if (ReadBearerToken(http.Request) is not { } token)
        {
            return NotRegistered();
        }

        var ct = http.RequestAborted;
        if (await FindPcIdAsync(db, token, ct) is not { } pcId)
        {
            return NotRegistered();
        }

        if (!http.WebSockets.IsWebSocketRequest)
        {
            return AuthEndpoints.BadRequest("This address needs a WebSocket connection.");
        }

        // Checked against this PC's own view only, so one PC can't join another PC's view.
        if (hub.CurrentFor(pcId) is not { } viewer || !viewer.ViewIdMatches(viewId))
        {
            return Results.Json(new ErrorResponse("No live view is open for this PC."), statusCode: StatusCodes.Status404NotFound);
        }

        // A PC that reconnects too often is refused before the upgrade, so connect churn can't pile up sockets.
        if (!hub.TryReserveAttach(pcId, clock.GetUtcNow()))
        {
            http.Response.Headers.RetryAfter = "1";
            return Results.Json(new ErrorResponse("The PC reconnected too soon."), statusCode: StatusCodes.Status429TooManyRequests);
        }

        var socket = await http.WebSockets.AcceptWebSocketAsync();
        var pc = new ScreenSocket(socket, ScreenProtocol.SendTimeout);
        try
        {
            if (await hub.AttachPcAsync(viewer, pc))
            {
                await ReceivePcMessagesAsync(hub, viewer, pc, ct);
            }
        }
        finally
        {
            await hub.DetachPcAsync(viewer, pc);
            await pc.CloseAsync(WebSocketCloseStatus.NormalClosure, "The stream ended.");
            pc.Dispose();
        }

        return Results.Empty;
    }

    // Frames are relayed one at a time, so a slow browser holds this loop back instead of a queue growing.
    // A connection that has been replaced stops here, so its frames and states are never relayed.
    private static async Task ReceivePcMessagesAsync(ScreenStreamHub hub, ScreenViewer viewer, ScreenSocket pc, CancellationToken cancellationToken)
    {
        using var linked = viewer.LinkLifetime(cancellationToken);
        if (linked is null)
        {
            return;
        }

        while (await pc.ReceiveAsync(ScreenProtocol.PcLimits, linked.Token) is { } message)
        {
            if (!viewer.IsCurrentPc(pc))
            {
                return;
            }

            switch (ScreenProtocol.ClassifyPcMessage(message.Type, message.Payload.Span))
            {
                case PcMessageKind.Frame:
                    await hub.RelayFrameAsync(viewer, pc, message.Payload);
                    break;
                case PcMessageKind.Paused:
                    await hub.RelayPcStateAsync(viewer, pc, ScreenProtocol.PausedState);
                    break;
                case PcMessageKind.Resumed:
                    await hub.RelayPcStateAsync(viewer, pc, ScreenProtocol.LiveState);
                    break;
                case PcMessageKind.ControlOn:
                    await hub.ConfirmControlAsync(viewer, pc);
                    break;
                case PcMessageKind.ControlOff:
                    await hub.EndControlAsync(viewer, ControlReasons.PcEnded);
                    break;
            }
        }
    }
}
