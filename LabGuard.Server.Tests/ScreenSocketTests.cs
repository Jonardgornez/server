using System.Net.WebSockets;
using System.Text;
using LabGuard.Server.Screen;

namespace LabGuard.Server.Tests;

public class ScreenSocketTests
{
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromMilliseconds(100);
    private static readonly MessageLimits Generous = new(256, 64 * 1024);

    [Fact]
    public async Task SendAsync_DeliversThePayloadWhenThePeerReads()
    {
        var socket = new FakeWebSocket();
        var screen = new ScreenSocket(socket, ShortTimeout);

        var sent = await screen.SendAsync("hello"u8.ToArray(), WebSocketMessageType.Text);

        Assert.True(sent);
        Assert.Equal("hello", Encoding.UTF8.GetString(socket.Sent.Single().Payload));
    }

    [Fact]
    public async Task SendAsync_AStalledPeer_FailsWithinTheTimeoutInsteadOfBlocking()
    {
        var socket = new FakeWebSocket { StallSends = true };
        var screen = new ScreenSocket(socket, ShortTimeout);

        var sent = await screen.SendAsync(new byte[] { 0xFF, 0xD8, 0xFF }, WebSocketMessageType.Binary);

        Assert.False(sent);
    }

    [Fact]
    public async Task SendAsync_AfterATimedOutSend_TheNextSendIsNotBlocked()
    {
        var socket = new FakeWebSocket { StallSends = true };
        var screen = new ScreenSocket(socket, ShortTimeout);
        await screen.SendAsync("first"u8.ToArray(), WebSocketMessageType.Text);

        socket.StallSends = false;
        var sent = await screen.SendAsync("second"u8.ToArray(), WebSocketMessageType.Text);

        Assert.True(sent);
    }

    [Fact]
    public async Task CloseAsync_SendsTheCloseOnceAndNeverThrows()
    {
        var socket = new FakeWebSocket();
        var screen = new ScreenSocket(socket, ShortTimeout);

        await screen.CloseAsync((WebSocketCloseStatus)4003, "ended");
        await screen.CloseAsync((WebSocketCloseStatus)4001, "again");

        Assert.Equal((WebSocketCloseStatus)4003, socket.CloseSent!.Value.Status);
    }

    [Fact]
    public async Task ReceiveAsync_ReturnsTheWholeTextMessage()
    {
        var socket = new FakeWebSocket();
        socket.EnqueueMessage(WebSocketMessageType.Text, "ack"u8.ToArray());
        var screen = new ScreenSocket(socket, ShortTimeout);

        var message = await screen.ReceiveAsync(new MessageLimits(16, 16), CancellationToken.None);

        Assert.NotNull(message);
        Assert.Equal(WebSocketMessageType.Text, message.Value.Type);
        Assert.Equal("ack", Encoding.UTF8.GetString(message.Value.Payload.Span));
    }

    [Fact]
    public async Task ReceiveAsync_JoinsAMessageThatArrivesInPieces()
    {
        var payload = new byte[40 * 1024];
        Random.Shared.NextBytes(payload);
        var socket = new FakeWebSocket();
        socket.EnqueueMessage(WebSocketMessageType.Binary, payload);
        var screen = new ScreenSocket(socket, ShortTimeout);

        var message = await screen.ReceiveAsync(Generous, CancellationToken.None);

        Assert.NotNull(message);
        Assert.True(payload.AsSpan().SequenceEqual(message.Value.Payload.Span));
    }

    [Fact]
    public async Task ReceiveAsync_ABinaryMessageOverItsLimit_IsClosedWith1009AndGivesNull()
    {
        var socket = new FakeWebSocket();
        socket.EnqueueMessage(WebSocketMessageType.Binary, new byte[300]);
        var screen = new ScreenSocket(socket, ShortTimeout);

        var message = await screen.ReceiveAsync(new MessageLimits(256, 256), CancellationToken.None);

        Assert.Null(message);
        Assert.Equal(WebSocketCloseStatus.MessageTooBig, socket.CloseSent!.Value.Status);
    }

    [Fact]
    public async Task ReceiveAsync_ATextMessageOver16Bytes_IsClosedWith1009_OnItsFirstChunk()
    {
        var socket = new FakeWebSocket();
        socket.EnqueueMessage(WebSocketMessageType.Text, new byte[17]);
        var screen = new ScreenSocket(socket, ShortTimeout);

        var message = await screen.ReceiveAsync(ScreenProtocol.PcLimits, CancellationToken.None);

        Assert.Null(message);
        Assert.Equal(WebSocketCloseStatus.MessageTooBig, socket.CloseSent!.Value.Status);
    }

    [Fact]
    public async Task ReceiveAsync_ABinaryFrameOfTheTextLimit_IsStillAFrame()
    {
        var socket = new FakeWebSocket();
        socket.EnqueueMessage(WebSocketMessageType.Binary, new byte[17]);
        var screen = new ScreenSocket(socket, ShortTimeout);

        var message = await screen.ReceiveAsync(ScreenProtocol.PcLimits, CancellationToken.None);

        Assert.NotNull(message);
        Assert.Equal(WebSocketMessageType.Binary, message.Value.Type);
    }

    [Fact]
    public async Task ReceiveAsync_APartialMessageThatStalls_IsAbortedAndGivesNull()
    {
        var socket = new FakeWebSocket();
        socket.EnqueueFragment(WebSocketMessageType.Binary, new byte[10]);
        var screen = new ScreenSocket(socket, ShortTimeout, messageStallTimeout: TimeSpan.FromMilliseconds(100));

        var message = await screen.ReceiveAsync(Generous, CancellationToken.None);

        Assert.Null(message);
        Assert.True(socket.Aborted);
        Assert.Null(socket.CloseSent);
    }

    [Fact]
    public async Task ReceiveAsync_IdleBeforeAMessageStarts_IsNotAborted()
    {
        var socket = new FakeWebSocket();
        var screen = new ScreenSocket(socket, ShortTimeout, messageStallTimeout: TimeSpan.FromMilliseconds(50));
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        var message = await screen.ReceiveAsync(Generous, cancel.Token);

        Assert.Null(message);
        Assert.False(socket.Aborted);
    }

    [Fact]
    public async Task Abort_EndsAPendingReceive_AndStopsTheSocket()
    {
        var socket = new FakeWebSocket();
        var screen = new ScreenSocket(socket, ShortTimeout);
        var receive = screen.ReceiveAsync(Generous, CancellationToken.None);

        screen.Abort();

        Assert.Null(await receive.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(socket.Aborted);
    }

    [Fact]
    public async Task ReceiveAsync_AfterDispose_GivesNullWithoutReading()
    {
        var socket = new FakeWebSocket();
        socket.EnqueueMessage(WebSocketMessageType.Text, "ack"u8.ToArray());
        var screen = new ScreenSocket(socket, ShortTimeout);
        screen.Dispose();

        Assert.Null(await screen.ReceiveAsync(Generous, CancellationToken.None));
    }

    [Fact]
    public async Task ReceiveAsync_AClosedPeer_GivesNull()
    {
        var socket = new FakeWebSocket();
        socket.EnqueueClose();
        var screen = new ScreenSocket(socket, ShortTimeout);

        Assert.Null(await screen.ReceiveAsync(Generous, CancellationToken.None));
    }
}
