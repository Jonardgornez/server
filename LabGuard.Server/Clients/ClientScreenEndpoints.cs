using System.Net.WebSockets;
using LabGuard.Server.Auth;
using LabGuard.Server.Data;
using LabGuard.Server.Screen;

namespace LabGuard.Server.Clients;

// The PC's end of a live view. It connects only while a heartbeat reports a viewer, and streams JPEG frames until the view ends.
public static partial class ClientEndpoints
{
    private static async Task<IResult> ScreenStreamAsync(string? viewId, HttpContext http, AppDbContext db, ScreenStreamHub hub)
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

        var socket = await http.WebSockets.AcceptWebSocketAsync();
        var pc = new ScreenSocket(socket, ScreenProtocol.SendTimeout);
        if (!await hub.AttachPcAsync(viewer, pc))
        {
            await pc.CloseAsync(WebSocketCloseStatus.NormalClosure, "The viewer closed.");
            return Results.Empty;
        }

        try
        {
            await ReceivePcMessagesAsync(hub, viewer, pc, ct);
        }
        finally
        {
            await hub.DetachPcAsync(viewer, pc);
            await pc.CloseAsync(WebSocketCloseStatus.NormalClosure, "The stream ended.");
        }

        return Results.Empty;
    }

    // Frames are relayed one at a time, so a slow browser holds this loop back instead of a queue growing.
    private static async Task ReceivePcMessagesAsync(ScreenStreamHub hub, ScreenViewer viewer, ScreenSocket pc, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, viewer.Lifetime);
        while (await pc.ReceiveAsync(ScreenProtocol.MaxPcMessageBytes, linked.Token) is { } message)
        {
            switch (ScreenProtocol.ClassifyPcMessage(message.Type, message.Payload.Span))
            {
                case PcMessageKind.Frame:
                    await hub.RelayFrameAsync(viewer, message.Payload);
                    break;
                case PcMessageKind.Paused:
                    await hub.RelayStateAsync(viewer, ScreenProtocol.PausedMessage);
                    break;
                case PcMessageKind.Resumed:
                    await hub.RelayStateAsync(viewer, ScreenProtocol.LiveMessage);
                    break;
            }
        }
    }
}
