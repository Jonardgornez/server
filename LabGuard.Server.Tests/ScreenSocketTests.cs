using System.Net.WebSockets;
using System.Text;
using LabGuard.Server.Screen;

namespace LabGuard.Server.Tests;

public class ScreenSocketTests
{
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromMilliseconds(100);

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

        Assert.Equal(((WebSocketCloseStatus)4003, "ended"), socket.CloseSent);
    }

    [Fact]
    public async Task ReceiveAsync_ReturnsTheWholeTextMessage()
    {
        var socket = new FakeWebSocket();
        socket.EnqueueMessage(WebSocketMessageType.Text, "ack"u8.ToArray());
        var screen = new ScreenSocket(socket, ShortTimeout);

        var message = await screen.ReceiveAsync(256, CancellationToken.None);

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

        var message = await screen.ReceiveAsync(64 * 1024, CancellationToken.None);

        Assert.NotNull(message);
        Assert.True(payload.AsSpan().SequenceEqual(message.Value.Payload.Span));
    }

    [Fact]
    public async Task ReceiveAsync_AMessageOverTheLimit_IsClosedWith1009AndGivesNull()
    {
        var socket = new FakeWebSocket();
        socket.EnqueueMessage(WebSocketMessageType.Binary, new byte[300]);
        var screen = new ScreenSocket(socket, ShortTimeout);

        var message = await screen.ReceiveAsync(256, CancellationToken.None);

        Assert.Null(message);
        Assert.Equal(WebSocketCloseStatus.MessageTooBig, socket.CloseSent!.Value.Status);
    }

    [Fact]
    public async Task ReceiveAsync_AClosedPeer_GivesNull()
    {
        var socket = new FakeWebSocket();
        socket.EnqueueClose();
        var screen = new ScreenSocket(socket, ShortTimeout);

        Assert.Null(await screen.ReceiveAsync(256, CancellationToken.None));
    }
}
