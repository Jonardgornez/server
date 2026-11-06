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

    [Fact]
    public void IsRelayableFrame_AcceptsAJpegWithAUsableSize()
    {
        Assert.True(ScreenProtocol.IsRelayableFrame(LiveViewClient.Frame("ok")));
        Assert.True(ScreenProtocol.IsRelayableFrame(LiveViewClient.Frame("ok", width: 8192, height: 1953)));
    }

    [Theory]
    [InlineData(8193, 100)]
    [InlineData(100, 8193)]
    [InlineData(8192, 8192)]
    [InlineData(1, 0)]
    public void IsRelayableFrame_RefusesASideOverTheLimit_OrAnEmptySide(int width, int height)
    {
        // 8192 by 8192 is 64 megapixels, which is over the pixel limit even though each side is allowed.
        Assert.False(ScreenProtocol.IsRelayableFrame(LiveViewClient.Frame("big", width: width, height: height)));
    }

    [Fact]
    public void IsRelayableFrame_RefusesAFrameOver16Megapixels()
    {
        Assert.False(ScreenProtocol.IsRelayableFrame(LiveViewClient.Frame("big", width: 5000, height: 4000)));
        Assert.True(ScreenProtocol.IsRelayableFrame(LiveViewClient.Frame("edge", width: 4000, height: 4000)));
    }

    [Fact]
    public void IsRelayableFrame_RefusesBytesWithoutAJpegHeader()
    {
        Assert.False(ScreenProtocol.IsRelayableFrame(new byte[] { 0xFF, 0xD8, 0xFF }));
        Assert.False(ScreenProtocol.IsRelayableFrame("NOT-A-FRAME"u8));
        Assert.False(ScreenProtocol.IsRelayableFrame([]));
    }

    [Fact]
    public void ClassifyPcMessage_RelaysRelayableFramesOnly()
    {
        Assert.Equal(PcMessageKind.Frame, ScreenProtocol.ClassifyPcMessage(WebSocketMessageType.Binary, LiveViewClient.Frame("ok")));
        Assert.Equal(PcMessageKind.Ignored, ScreenProtocol.ClassifyPcMessage(WebSocketMessageType.Binary, new byte[] { 0x00, 0xD8, 0xFF }));
        Assert.Equal(PcMessageKind.Ignored, ScreenProtocol.ClassifyPcMessage(WebSocketMessageType.Binary, new byte[] { 0xFF, 0xD8, 0xFF }));
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
        Assert.Equal(PcMessageKind.Ignored, ScreenProtocol.ClassifyPcMessage(WebSocketMessageType.Text, LiveViewClient.Frame("ok")));
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
    public void Limits_MatchTheContract()
    {
        Assert.Equal(new MessageLimits(16, 2 * 1024 * 1024), ScreenProtocol.PcLimits);
        Assert.Equal(new MessageLimits(256, 256), ScreenProtocol.BrowserLimits);
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
        Assert.Equal("{\"state\":\"waiting\"}", Encoding.UTF8.GetString(ScreenProtocol.StateMessage(ScreenProtocol.WaitingState).Span));
        Assert.Equal("{\"state\":\"live\"}", Encoding.UTF8.GetString(ScreenProtocol.StateMessage(ScreenProtocol.LiveState).Span));
        Assert.Equal("{\"state\":\"paused\"}", Encoding.UTF8.GetString(ScreenProtocol.StateMessage(ScreenProtocol.PausedState).Span));
    }

    [Fact]
    public void StateMessage_RefusesAnUnknownState()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ScreenProtocol.StateMessage("streaming"));
    }
}
