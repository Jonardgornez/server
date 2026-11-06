using System.Buffers;
using System.Net.WebSockets;

namespace LabGuard.Server.Screen;

public readonly record struct ScreenMessage(WebSocketMessageType Type, ReadOnlyMemory<byte> Payload);

// One WebSocket with its writes serialized and time-limited. A WebSocket allows one send at a time, and a stalled peer
// must not hold a frame or a lock for long: a send that doesn't finish in time fails, and the caller closes the view.
public sealed class ScreenSocket(WebSocket socket, TimeSpan sendTimeout)
{
    private const int ReceiveChunkBytes = 16 * 1024;

    private readonly SemaphoreSlim _writeGate = new(1, 1);

    // Every way a socket can be gone: the peer left, the connection failed, the receive was cancelled or the socket was disposed.
    private static bool IsSocketGone(Exception exception) =>
        exception is WebSocketException or OperationCanceledException or InvalidOperationException or ObjectDisposedException or IOException;

    // False when the send failed or timed out. Never throws.
    public async Task<bool> SendAsync(ReadOnlyMemory<byte> payload, WebSocketMessageType type)
    {
        if (!await _writeGate.WaitAsync(sendTimeout))
        {
            return false;
        }

        try
        {
            using var timeout = new CancellationTokenSource(sendTimeout);
            await socket.SendAsync(payload, type, endOfMessage: true, timeout.Token);
            return true;
        }
        catch (Exception exception) when (IsSocketGone(exception))
        {
            return false;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    // Sends a close frame. Never throws: a socket that can't be closed politely is aborted when its receive is cancelled.
    public async Task CloseAsync(WebSocketCloseStatus status, string reason)
    {
        if (!await _writeGate.WaitAsync(ScreenProtocol.CloseTimeout))
        {
            return;
        }

        try
        {
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                using var timeout = new CancellationTokenSource(ScreenProtocol.CloseTimeout);
                await socket.CloseOutputAsync(status, reason, timeout.Token);
            }
        }
        catch (Exception exception) when (IsSocketGone(exception))
        {
            // The peer is already gone, so there is nobody left to tell.
        }
        finally
        {
            _writeGate.Release();
        }
    }

    // The next whole message. Null when the peer closed, the connection failed or the receive was cancelled.
    // A message over maxBytes is closed with 1009 and also gives null.
    public async Task<ScreenMessage?> ReceiveAsync(int maxBytes, CancellationToken cancellationToken)
    {
        var chunk = ArrayPool<byte>.Shared.Rent(ReceiveChunkBytes);
        try
        {
            var message = new MemoryStream();
            while (true)
            {
                var result = await socket.ReceiveAsync(chunk.AsMemory(0, ReceiveChunkBytes), cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return null;
                }

                if (message.Length + result.Count > maxBytes)
                {
                    await CloseAsync(WebSocketCloseStatus.MessageTooBig, "Message too large.");
                    return null;
                }

                message.Write(chunk, 0, result.Count);
                if (result.EndOfMessage)
                {
                    return new ScreenMessage(result.MessageType, message.GetBuffer().AsMemory(0, (int)message.Length));
                }
            }
        }
        catch (Exception exception) when (IsSocketGone(exception))
        {
            return null;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunk);
        }
    }
}
