using System.Net;
using System.Net.WebSockets;

namespace LabGuard.Server.Screen;

public readonly record struct ScreenMessage(WebSocketMessageType Type, ReadOnlyMemory<byte> Payload);

// One WebSocket: writes serialized and time-limited, reads bounded by message type and by how long a started message may take,
// and an Abort that stops the socket at once. A WebSocket allows one send at a time, and one receive at a time.
public sealed class ScreenSocket(WebSocket socket, TimeSpan sendTimeout, TimeSpan? messageStallTimeout = null) : IDisposable
{
    private const int ReceiveChunkBytes = 16 * 1024;

    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly Lifetime _abort = new();
    private readonly TaskCompletionSource _receiveEnded = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly byte[] _receiveChunk = new byte[ReceiveChunkBytes];
    private readonly TimeSpan _messageStallTimeout = messageStallTimeout ?? ScreenProtocol.MessageStallTimeout;

    // The address the peer connected from, as the server saw it on the connection. Null when unknown.
    public IPAddress? RemoteAddress { get; init; }

    // Every way a socket can be gone: the peer left, the connection failed, the receive was cancelled or the socket was disposed.
    private static bool IsSocketGone(Exception exception) =>
        exception is WebSocketException or OperationCanceledException or InvalidOperationException or ObjectDisposedException or IOException;

    // False when the send failed or timed out. Never throws. A caller can give a shorter timeout than the socket's own.
    public async Task<bool> SendAsync(ReadOnlyMemory<byte> payload, WebSocketMessageType type, TimeSpan? timeout = null)
    {
        var limit = timeout ?? sendTimeout;
        if (!await _writeGate.WaitAsync(limit))
        {
            return false;
        }

        try
        {
            using var deadline = new CancellationTokenSource(limit);
            await socket.SendAsync(payload, type, endOfMessage: true, deadline.Token);
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

    // Sends a close frame. Never throws: a socket that can't be closed politely is stopped when its receive is cancelled.
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

    // Waits, up to the timeout, for the peer to answer a close frame or for the socket to end. Ending the connection before that
    // resets it, and a reset can discard the close frame still on its way, so the peer would never learn why the socket closed.
    public async Task WaitForPeerCloseAsync(TimeSpan timeout)
    {
        try
        {
            await _receiveEnded.Task.WaitAsync(timeout);
        }
        catch (TimeoutException)
        {
            // The peer didn't answer in time, so the socket is ended anyway.
        }
    }

    // Stops the socket now, with no close handshake. Used for a connection that was replaced, and to end any receive on it.
    public void Abort()
    {
        _abort.Cancel();
        socket.Abort();
    }

    // The next whole message. Null when the peer closed, the connection failed, the receive was cancelled or the socket was aborted.
    // A message over its type's limit is closed with 1009, and a started message that stalls is aborted. Both give null.
    // The limit is checked on every chunk, so an oversized first chunk is refused at once.
    public async Task<ScreenMessage?> ReceiveAsync(MessageLimits limits, CancellationToken cancellationToken)
    {
        var message = await ReceiveMessageAsync(limits, cancellationToken);
        if (message is null)
        {
            _receiveEnded.TrySetResult();
        }

        return message;
    }

    private async Task<ScreenMessage?> ReceiveMessageAsync(MessageLimits limits, CancellationToken cancellationToken)
    {
        using var linked = _abort.Link(cancellationToken);
        if (linked is null)
        {
            return null;
        }

        var message = new MemoryStream();
        CancellationTokenSource? stall = null;
        try
        {
            while (true)
            {
                var result = await socket.ReceiveAsync(_receiveChunk.AsMemory(), stall?.Token ?? linked.Token);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return null;
                }

                var limit = result.MessageType == WebSocketMessageType.Text ? limits.Text : limits.Binary;
                if (message.Length + result.Count > limit)
                {
                    await CloseAsync(WebSocketCloseStatus.MessageTooBig, "Message too large.");
                    return null;
                }

                message.Write(_receiveChunk, 0, result.Count);
                if (result.EndOfMessage)
                {
                    return new ScreenMessage(result.MessageType, message.GetBuffer().AsMemory(0, (int)message.Length));
                }

                // The clock starts with the first fragment. The idle wait before a message starts is not limited.
                stall ??= StartStallTimer(linked.Token);
            }
        }
        catch (OperationCanceledException) when (stall is { IsCancellationRequested: true } && !linked.IsCancellationRequested)
        {
            // The partial message stalled. Nothing more will arrive on it, so the socket is stopped.
            Abort();
            return null;
        }
        catch (Exception exception) when (IsSocketGone(exception))
        {
            return null;
        }
        finally
        {
            stall?.Dispose();
        }
    }

    private CancellationTokenSource StartStallTimer(CancellationToken cancellationToken)
    {
        var stall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        stall.CancelAfter(_messageStallTimeout);
        return stall;
    }

    public void Dispose() => _abort.Dispose();
}
