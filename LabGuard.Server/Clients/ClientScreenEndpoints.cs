using System.Net.WebSockets;
using LabGuard.Server.Auth;
using LabGuard.Server.Data;
using LabGuard.Server.Screen;

namespace LabGuard.Server.Clients;

// The PC's end of a live view. It connects only while a heartbeat reports a stream, and streams JPEG frames until the stream ends.
// Every view of the PC shares this one connection.
public static partial class ClientEndpoints
{
    private static async Task<IResult> ScreenStreamAsync(
        string? viewId,
        HttpContext http,
        AppDbContext db,
        ScreenStreamHub hub,
        TimeProvider clock,
        ILoggerFactory loggerFactory)
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

        // Checked against this PC's own stream only, so one PC can't join another PC's stream.
        if (hub.StreamFor(pcId) is not { } stream || !stream.ViewIdMatches(viewId))
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
            if (await hub.AttachPcAsync(stream, pc, viewId))
            {
                await ReceivePcMessagesAsync(hub, stream, pc, loggerFactory.CreateLogger(nameof(ClientEndpoints)), ct);
            }
            else
            {
                // The view mode changed since the id was checked, or the view ended. The PC waits for its next heartbeat.
                await pc.CloseAsync(ScreenProtocol.ReplacedCloseStatus, "The view changed.");
            }
        }
        finally
        {
            await hub.DetachPcAsync(stream, pc);
            await pc.CloseAsync(WebSocketCloseStatus.NormalClosure, "The stream ended.");
            pc.Dispose();
        }

        return Results.Empty;
    }

    // A frame's sends to the browsers are not waited for, so one slow browser can't hold back the others. Nothing queues up: a
    // frame goes out only within the credit windows, which bound what is in flight to each browser.
    // A connection that has been replaced stops here, so its frames and states are never relayed.
    private static async Task ReceivePcMessagesAsync(ScreenStreamHub hub, ScreenStream stream, ScreenSocket pc, ILogger logger, CancellationToken cancellationToken)
    {
        using var linked = stream.LinkLifetime(cancellationToken);
        if (linked is null)
        {
            return;
        }

        while (await pc.ReceiveAsync(ScreenProtocol.PcLimits, linked.Token) is { } message)
        {
            if (!stream.IsCurrentPc(pc))
            {
                return;
            }

            var read = ScreenProtocol.ReadPcMessage(message.Type, message.Payload.Span);
            switch (read.Kind)
            {
                case PcMessageKind.Frame:
                    _ = RelayObserver.ObserveAsync(hub.RelayFrameAsync(stream, pc, message.Payload), logger);
                    break;
                case PcMessageKind.Paused:
                    await hub.SetPcPausedAsync(stream, pc, isPaused: true);
                    break;
                case PcMessageKind.Resumed:
                    await hub.SetPcPausedAsync(stream, pc, isPaused: false);
                    break;
                case PcMessageKind.ControlOn:
                    await hub.ConfirmControlAsync(stream, pc);
                    break;
                case PcMessageKind.ControlOff:
                    await hub.EndControlFromAsync(stream, pc);
                    break;
                case PcMessageKind.Cursor when read.BrowserCursor is { } cursor:
                    await hub.RelayCursorAsync(stream, pc, cursor);
                    break;
                case PcMessageKind.TooLarge:
                    await pc.CloseAsync(WebSocketCloseStatus.MessageTooBig, "Message too large.");
                    return;
            }
        }
    }

    // The PC's secure-desktop helper. It runs as SYSTEM in the console session, signs in with the PC's token, and stays connected
    // while idle. It streams only when told to, while a view is open and the PC's own stream is paused. One helper per PC.
    private static async Task<IResult> SecureScreenStreamAsync(
        HttpContext http,
        AppDbContext db,
        ScreenStreamHub hub,
        TimeProvider clock,
        ILoggerFactory loggerFactory)
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
            await ReceiveHelperMessagesAsync(hub, helper, loggerFactory.CreateLogger(nameof(ClientEndpoints)), ct);
        }
        finally
        {
            await hub.DetachHelperAsync(helper);
            // After a close this side sent (1008, 1009), the peer gets a moment to read the code before the connection ends.
            await helper.Socket.DrainUntilPeerClosesAsync(ScreenProtocol.CloseHandshakeTimeout);
            await helper.Socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "The stream ended.");
            helper.Socket.Dispose();
        }

        return Results.Empty;
    }

    // Strict: anything but a relayable frame or a known text closes the helper. A replaced helper stops here.
    private static async Task ReceiveHelperMessagesAsync(ScreenStreamHub hub, SecureHelperConnection helper, ILogger logger, CancellationToken cancellationToken)
    {
        while (await helper.Socket.ReceiveAsync(ScreenProtocol.HelperLimits, cancellationToken) is { } message)
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

            // A helper that sends frames unasked, or faster than the cap, is closed, so an idle helper can't flood the server.
            if (kind == HelperMessageKind.Frame && !hub.IsHelperFrameAllowed(helper))
            {
                await helper.Socket.CloseAsync(SecureHelperProtocol.ProtocolErrorCloseStatus, "Unexpected frame.");
                return;
            }

            var stream = hub.StreamFor(helper.PcId);
            switch (kind)
            {
                case HelperMessageKind.Frame:
                    // Not waited for, like the PC's frames, so one slow browser can't hold back the others.
                    _ = RelayObserver.ObserveAsync(hub.RelayHelperFrameAsync(helper, message.Payload), logger);
                    break;
                case HelperMessageKind.ControlOn when stream is not null:
                    await hub.ConfirmControlAsync(stream, helper.Socket);
                    break;
                case HelperMessageKind.ControlOff when stream is not null:
                    await hub.EndControlFromAsync(stream, helper.Socket);
                    break;
            }
        }
    }
}
