using System.Net.WebSockets;
using System.Text;
using LabGuard.Server.Screen;

namespace LabGuard.Server.Tests;

public class ScreenSecureHelperProtocolTests
{
    [Fact]
    public void Classify_ARelayableJpeg_IsAFrame()
    {
        Assert.Equal(HelperMessageKind.Frame, SecureHelperProtocol.Classify(WebSocketMessageType.Binary, LiveViewClient.Frame("ok")));
    }

    [Fact]
    public void Classify_BinaryThatIsNotARelayableFrame_IsInvalid()
    {
        Assert.Equal(HelperMessageKind.Invalid, SecureHelperProtocol.Classify(WebSocketMessageType.Binary, "nope"u8));
        Assert.Equal(HelperMessageKind.Invalid, SecureHelperProtocol.Classify(WebSocketMessageType.Binary, LiveViewClient.Frame("big", width: 9000)));
    }

    [Theory]
    [InlineData("control-on", HelperMessageKind.ControlOn)]
    [InlineData("control-off", HelperMessageKind.ControlOff)]
    [InlineData("paused", HelperMessageKind.Ignored)]
    [InlineData("resumed", HelperMessageKind.Ignored)]
    [InlineData("ack", HelperMessageKind.Invalid)]
    [InlineData("Paused", HelperMessageKind.Invalid)]
    [InlineData("", HelperMessageKind.Invalid)]
    public void Classify_Text(string text, HelperMessageKind expected)
    {
        Assert.Equal(expected, SecureHelperProtocol.Classify(WebSocketMessageType.Text, Encoding.UTF8.GetBytes(text)));
    }

    [Fact]
    public void StartMessage_CarriesTheViewMode_AndRefusesAnUnknownOne()
    {
        Assert.Equal("{\"t\":\"start\",\"mode\":\"notified\"}", Encoding.UTF8.GetString(SecureHelperProtocol.StartMessage(ScreenProtocol.NotifiedMode).Span));
        Assert.Equal("{\"t\":\"start\",\"mode\":\"anonymous\"}", Encoding.UTF8.GetString(SecureHelperProtocol.StartMessage(ScreenProtocol.AnonymousMode).Span));
        Assert.Throws<ArgumentOutOfRangeException>(() => SecureHelperProtocol.StartMessage("other"));
    }

    [Fact]
    public void StateMessage_Secure()
    {
        Assert.Equal("{\"state\":\"secure\"}", Encoding.UTF8.GetString(ScreenProtocol.StateMessage(ScreenProtocol.SecureState).Span));
    }
}
