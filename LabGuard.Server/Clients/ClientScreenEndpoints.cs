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
        // The live address is kept on the socket, so a control request can check it against this machine's addresses.
        var pc = new ScreenSocket(socket, ScreenProtocol.SendTimeout) { RemoteAddress = http.Connection.RemoteIpAddress };
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
                    await hub.SetPcPausedAsync(viewer, pc, isPaused: true);
                    break;
                case PcMessageKind.Resumed:
                    await hub.SetPcPausedAsync(viewer, pc, isPaused: false);
                    break;
                case PcMessageKind.ControlOn:
                    await hub.ConfirmControlAsync(viewer, pc);
                    break;
                case PcMessageKind.ControlOff:
                    await hub.EndControlFromAsync(viewer, pc);
                    break;
            }
        }
    }

    // The PC's secure-desktop helper. It runs as SYSTEM in the console session, signs in with the PC's token, and stays connected
    // while idle. It streams only when told to, while a view is open and the PC's own stream is paused. One helper per PC.
    private static async Task<IResult> SecureScreenStreamAsync(HttpContext http, AppDbContext db, ScreenStreamHub hub, TimeProvider clock)
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

        if (!hub.TryReserveHelperAttach(pcId, clock.GetUtcNow()))
        {
            http.Response.Headers.RetryAfter = "1";
            return Results.Json(new ErrorResponse("The helper reconnected too soon."), statusCode: StatusCodes.Status429TooManyRequests);
        }

        var socket = await http.WebSockets.AcceptWebSocketAsync(new WebSocketAcceptContext
        {
            KeepAliveInterval = SecureHelperProtocol.KeepAliveInterval,
            KeepAliveTimeout = SecureHelperProtocol.KeepAliveTimeout,
        });
        // The token's hash is kept, so the monitor can close the helper once the PC's token is replaced or the PC is removed.
        var helper = new SecureHelperConnection(pcId, HashToken(token), new ScreenSocket(socket, ScreenProtocol.SendTimeout));
        try
        {
            await hub.AttachHelperAsync(helper);
            await ReceiveHelperMessagesAsync(hub, helper, ct);
        }
        finally
        {
            await hub.DetachHelperAsync(helper);
            await helper.Socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "The stream ended.");
            helper.Socket.Dispose();
        }

        return Results.Empty;
    }

    // Strict: anything but a relayable frame or a known text closes the helper. A replaced helper stops here.
    private static async Task ReceiveHelperMessagesAsync(ScreenStreamHub hub, SecureHelperConnection helper, CancellationToken cancellationToken)
    {
        while (await helper.Socket.ReceiveAsync(ScreenProtocol.PcLimits, cancellationToken) is { } message)
        {
            if (!hub.IsCurrentHelper(helper))
            {
                return;
            }

            var kind = SecureHelperProtocol.Classify(message.Type, message.Payload.Span);
            if (kind == HelperMessageKind.Invalid)
            {
                await helper.Socket.CloseAsync(SecureHelperProtocol.ProtocolErrorCloseStatus, "Unexpected message.");
                return;
            }

            var viewer = hub.CurrentFor(helper.PcId);
            switch (kind)
            {
                case HelperMessageKind.Frame:
                    await hub.RelayHelperFrameAsync(helper, message.Payload);
                    break;
                case HelperMessageKind.ControlOn when viewer is not null:
                    await hub.ConfirmControlAsync(viewer, helper.Socket);
                    break;
                case HelperMessageKind.ControlOff when viewer is not null:
                    await hub.EndControlFromAsync(viewer, helper.Socket);
                    break;
            }
        }
    }
}
