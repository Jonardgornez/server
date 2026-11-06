using System.Net.WebSockets;
using System.Text;
using LabGuard.Server.Screen;

namespace LabGuard.Server.Tests;

public class ScreenProtocolTests
{
    [Fact]
    public void ParseMode_MissingMeansNotified()
    {
        Assert.Equal(ScreenProtocol.NotifiedMode, ScreenProtocol.ParseMode(null));
    }

    [Theory]
    [InlineData("notified", "notified")]
    [InlineData("anonymous", "anonymous")]
    public void ParseMode_AcceptsTheTwoModes(string text, string expected)
    {
        Assert.Equal(expected, ScreenProtocol.ParseMode(text));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Notified")]
    [InlineData("secret")]
    [InlineData(" anonymous")]
    public void ParseMode_RefusesAnythingElse(string text)
    {
        Assert.Null(ScreenProtocol.ParseMode(text));
    }

    [Theory]
    [InlineData("http://localhost:5173")]
    [InlineData("http://localhost")]
    [InlineData("https://LOCALHOST:8443")]
    [InlineData("http://127.0.0.1:5000")]
    [InlineData("http://[::1]:5173")]
    public void IsLoopbackOrigin_AcceptsLoopbackHostsOnAnyPort(string origin)
    {
        Assert.True(ScreenProtocol.IsLoopbackOrigin(origin));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("https://evil.example")]
    [InlineData("http://localhost.evil.example")]
    [InlineData("http://localhost@evil.example")]
    [InlineData("http://127.0.0.2:5173")]
    [InlineData("http://192.168.1.5:5000")]
    [InlineData("ws://evil.example")]
    [InlineData("file://localhost/etc")]
    public void IsLoopbackOrigin_RefusesEverythingElse(string? origin)
    {
        Assert.False(ScreenProtocol.IsLoopbackOrigin(origin));
    }

    [Fact]
    public void IsJpeg_NeedsTheStartOfMarker()
    {
        Assert.True(ScreenProtocol.IsJpeg(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }));
        Assert.False(ScreenProtocol.IsJpeg(new byte[] { 0xFF, 0xD8, 0x00, 0xE0 }));
        Assert.False(ScreenProtocol.IsJpeg(new byte[] { 0x89, 0x50, 0x4E, 0x47 }));
        Assert.False(ScreenProtocol.IsJpeg(new byte[] { 0xFF, 0xD8 }));
        Assert.False(ScreenProtocol.IsJpeg([]));
    }

    [Fact]
    public void ClassifyPcMessage_RelaysJpegFramesOnly()
    {
        Assert.Equal(PcMessageKind.Frame, ScreenProtocol.ClassifyPcMessage(WebSocketMessageType.Binary, new byte[] { 0xFF, 0xD8, 0xFF }));
        Assert.Equal(PcMessageKind.Ignored, ScreenProtocol.ClassifyPcMessage(WebSocketMessageType.Binary, new byte[] { 0x00, 0xD8, 0xFF }));
        Assert.Equal(PcMessageKind.Ignored, ScreenProtocol.ClassifyPcMessage(WebSocketMessageType.Binary, []));
    }

    [Fact]
    public void ClassifyPcMessage_ReadsPausedAndResumedText()
    {
        Assert.Equal(PcMessageKind.Paused, ScreenProtocol.ClassifyPcMessage(WebSocketMessageType.Text, "paused"u8));
        Assert.Equal(PcMessageKind.Resumed, ScreenProtocol.ClassifyPcMessage(WebSocketMessageType.Text, "resumed"u8));
        Assert.Equal(PcMessageKind.Ignored, ScreenProtocol.ClassifyPcMessage(WebSocketMessageType.Text, "PAUSED"u8));
        Assert.Equal(PcMessageKind.Ignored, ScreenProtocol.ClassifyPcMessage(WebSocketMessageType.Text, "ack"u8));
    }

    [Fact]
    public void ClassifyPcMessage_IgnoresTextThatLooksLikeAFrame()
    {
        Assert.Equal(PcMessageKind.Ignored, ScreenProtocol.ClassifyPcMessage(WebSocketMessageType.Text, new byte[] { 0xFF, 0xD8, 0xFF }));
    }

    [Theory]
    [InlineData("ack")]
    [InlineData("ACK")]
    [InlineData("ack ")]
    [InlineData("")]
    public void IsBrowserAck_OnlyTheExactTextAck(string text)
    {
        Assert.Equal(text == "ack", ScreenProtocol.IsBrowserAck(WebSocketMessageType.Text, Encoding.UTF8.GetBytes(text)));
    }

    [Fact]
    public void IsBrowserAck_IgnoresBinaryEvenWithTheSameBytes()
    {
        Assert.False(ScreenProtocol.IsBrowserAck(WebSocketMessageType.Binary, "ack"u8));
    }

    [Fact]
    public void CloseCodes_MatchTheContract()
    {
        Assert.Equal(4001, (int)ScreenProtocol.ReplacedCloseStatus);
        Assert.Equal(4002, (int)ScreenProtocol.PcOfflineCloseStatus);
        Assert.Equal(4003, (int)ScreenProtocol.SessionEndedCloseStatus);
    }

    [Fact]
    public void StateMessages_AreTheDocumentedJson()
    {
        Assert.Equal("{\"state\":\"waiting\"}", Encoding.UTF8.GetString(ScreenProtocol.WaitingMessage));
        Assert.Equal("{\"state\":\"live\"}", Encoding.UTF8.GetString(ScreenProtocol.LiveMessage));
        Assert.Equal("{\"state\":\"paused\"}", Encoding.UTF8.GetString(ScreenProtocol.PausedMessage));
    }
}
