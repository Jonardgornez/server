using System.Net.WebSockets;
using System.Text;
using LabGuard.Server.Screen;

namespace LabGuard.Server.Tests;

public class ScreenControlMessagesTests
{
    private static BrowserControlMessage Parse(string json) =>
        ScreenControlMessages.Parse(WebSocketMessageType.Text, Encoding.UTF8.GetBytes(json));

    [Theory]
    [InlineData("{\"t\":\"move\",\"x\":0,\"y\":0}", "{\"t\":\"move\",\"x\":0,\"y\":0}")]
    [InlineData("{\"t\":\"move\",\"x\":65535,\"y\":65535}", "{\"t\":\"move\",\"x\":65535,\"y\":65535}")]
    [InlineData("{ \"y\": 20 , \"x\": 10, \"t\": \"move\" }", "{\"t\":\"move\",\"x\":10,\"y\":20}")]
    public void Parse_AMove_IsRelayedInTheCanonicalFieldOrder(string json, string canonical)
    {
        var message = Parse(json);

        Assert.Equal(BrowserControlKind.Move, message.Kind);
        Assert.Equal(canonical, message.Canonical);
    }

    [Theory]
    [InlineData("{\"t\":\"down\",\"b\":0,\"x\":1,\"y\":2}", "{\"t\":\"down\",\"b\":0,\"x\":1,\"y\":2}")]
    [InlineData("{\"x\":1,\"b\":4,\"y\":2,\"t\":\"up\"}", "{\"t\":\"up\",\"b\":4,\"x\":1,\"y\":2}")]
    [InlineData("{\"t\":\"down\",\"b\":3,\"x\":65535,\"y\":0}", "{\"t\":\"down\",\"b\":3,\"x\":65535,\"y\":0}")]
    public void Parse_AButtonEvent_IsRelayedInTheCanonicalFieldOrder(string json, string canonical)
    {
        var message = Parse(json);

        Assert.Equal(BrowserControlKind.Input, message.Kind);
        Assert.Equal(canonical, message.Canonical);
    }

    [Theory]
    [InlineData("{\"t\":\"wheel\",\"dx\":120,\"dy\":-120}", "{\"t\":\"wheel\",\"dx\":120,\"dy\":-120}")]
    [InlineData("{\"t\":\"wheel\",\"dx\":5000,\"dy\":-5000}", "{\"t\":\"wheel\",\"dx\":1200,\"dy\":-1200}")]
    [InlineData("{\"t\":\"wheel\",\"dx\":1200,\"dy\":-1200}", "{\"t\":\"wheel\",\"dx\":1200,\"dy\":-1200}")]
    public void Parse_AWheel_IsClampedToTheWheelRange(string json, string canonical)
    {
        var message = Parse(json);

        Assert.Equal(BrowserControlKind.Input, message.Kind);
        Assert.Equal(canonical, message.Canonical);
    }

    [Theory]
    [InlineData("{\"d\":false,\"c\":\"Digit1\",\"t\":\"key\"}", "{\"t\":\"key\",\"c\":\"Digit1\",\"d\":false}")]
    [InlineData("{\"t\":\"key\",\"c\":\"KeyA\",\"d\":true}", "{\"t\":\"key\",\"c\":\"KeyA\",\"d\":true}")]
    public void Parse_AKey_IsRelayedInTheCanonicalFieldOrder(string json, string canonical)
    {
        var message = Parse(json);

        Assert.Equal(BrowserControlKind.Input, message.Kind);
        Assert.Equal(canonical, message.Canonical);
    }

    [Fact]
    public void Parse_A32CharacterKeyCode_IsAccepted()
    {
        var code = new string('A', 32);

        var message = Parse($"{{\"t\":\"key\",\"c\":\"{code}\",\"d\":true}}");

        Assert.Equal(BrowserControlKind.Input, message.Kind);
        Assert.Contains(code, message.Canonical);
    }

    [Theory]
    [InlineData("{\"t\":\"move\",\"x\":1,\"y\":1}", InputRate.Move)]
    [InlineData("{\"t\":\"down\",\"b\":0,\"x\":1,\"y\":1}", InputRate.Other)]
    [InlineData("{\"t\":\"wheel\",\"dx\":0,\"dy\":120}", InputRate.Other)]
    [InlineData("{\"t\":\"key\",\"c\":\"KeyA\",\"d\":true}", InputRate.Other)]
    [InlineData("{\"t\":\"up\",\"b\":0,\"x\":1,\"y\":1}", InputRate.Exempt)]
    [InlineData("{\"t\":\"key\",\"c\":\"KeyA\",\"d\":false}", InputRate.Exempt)]
    [InlineData("{\"t\":\"release\"}", InputRate.Exempt)]
    public void Parse_SetsTheRateClass_OnlyMovesAndPressesAreCapped(string json, InputRate expected)
    {
        Assert.Equal(expected, Parse(json).Rate);
    }

    [Fact]
    public void Parse_Release_IsRelayedAsItself()
    {
        var message = Parse("{\"t\":\"release\"}");

        Assert.Equal(BrowserControlKind.Input, message.Kind);
        Assert.Equal("{\"t\":\"release\"}", message.Canonical);
    }

    [Theory]
    [InlineData("{\"t\":\"control\",\"on\":true}", BrowserControlKind.ControlOn)]
    [InlineData("{\"t\":\"control\",\"on\":false}", BrowserControlKind.ControlOff)]
    public void Parse_ControlOnOrOff_IsRecognised(string json, BrowserControlKind expected)
    {
        Assert.Equal(expected, Parse(json).Kind);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("\"move\"")]
    [InlineData("{\"t\":\"move\",\"x\":1")]
    [InlineData("{\"t\":5}")]
    [InlineData("{\"x\":1,\"y\":2}")]
    [InlineData("{\"t\":\"zap\"}")]
    [InlineData("{\"t\":\"move\",\"x\":1}")]
    [InlineData("{\"t\":\"move\",\"x\":1,\"y\":2,\"z\":3}")]
    [InlineData("{\"t\":\"move\",\"x\":1,\"y\":2,\"x\":3}")]
    [InlineData("{\"t\":\"move\",\"x\":-1,\"y\":0}")]
    [InlineData("{\"t\":\"move\",\"x\":0,\"y\":65536}")]
    [InlineData("{\"t\":\"move\",\"x\":1.5,\"y\":0}")]
    [InlineData("{\"t\":\"move\",\"x\":1e2,\"y\":0}")]
    [InlineData("{\"t\":\"move\",\"x\":\"1\",\"y\":0}")]
    [InlineData("{\"t\":\"move\",\"x\":true,\"y\":0}")]
    [InlineData("{\"t\":\"move\",\"x\":null,\"y\":0}")]
    [InlineData("{\"t\":\"down\",\"b\":5,\"x\":1,\"y\":1}")]
    [InlineData("{\"t\":\"up\",\"b\":-1,\"x\":1,\"y\":1}")]
    [InlineData("{\"t\":\"down\",\"x\":1,\"y\":1}")]
    [InlineData("{\"t\":\"wheel\",\"dx\":1.0,\"dy\":0}")]
    [InlineData("{\"t\":\"wheel\",\"dx\":0}")]
    [InlineData("{\"t\":\"key\",\"c\":\"Key A\",\"d\":true}")]
    [InlineData("{\"t\":\"key\",\"c\":\"KeyA-\",\"d\":true}")]
    [InlineData("{\"t\":\"key\",\"c\":\"\",\"d\":true}")]
    [InlineData("{\"t\":\"key\",\"c\":\"KeyA\",\"d\":\"true\"}")]
    [InlineData("{\"t\":\"key\",\"c\":\"KeyA\",\"d\":1}")]
    [InlineData("{\"t\":\"key\",\"c\":5,\"d\":true}")]
    [InlineData("{\"t\":\"key\",\"c\":\"KeyA\"}")]
    [InlineData("{\"t\":\"release\",\"x\":1}")]
    [InlineData("{\"t\":\"control\",\"on\":\"yes\"}")]
    [InlineData("{\"t\":\"control\"}")]
    [InlineData("{\"t\":\"control\",\"on\":true,\"x\":1}")]
    public void Parse_MalformedOrOutOfRange_IsDropped(string json)
    {
        Assert.Equal(BrowserControlKind.Invalid, Parse(json).Kind);
    }

    [Fact]
    public void Parse_A33CharacterKeyCode_IsDropped()
    {
        var code = new string('A', 33);

        Assert.Equal(BrowserControlKind.Invalid, Parse($"{{\"t\":\"key\",\"c\":\"{code}\",\"d\":true}}").Kind);
    }

    [Fact]
    public void Parse_AKeyCodeWithAnEscapedCharacter_IsReadAsItsValueAndChecked()
    {
        // The escape decodes to a letter, so this is a valid code, and it is rebuilt without the escape.
        var message = Parse("{\"t\":\"key\",\"c\":\"Key\\u0041\",\"d\":true}");

        Assert.Equal("{\"t\":\"key\",\"c\":\"KeyA\",\"d\":true}", message.Canonical);
    }

    [Fact]
    public void Parse_ABinaryMessage_IsDroppedEvenWithValidJson()
    {
        var message = ScreenControlMessages.Parse(WebSocketMessageType.Binary, "{\"t\":\"release\"}"u8.ToArray());

        Assert.Equal(BrowserControlKind.Invalid, message.Kind);
    }

    [Fact]
    public void Parse_AnExtraNestedObject_IsDropped()
    {
        Assert.Equal(BrowserControlKind.Invalid, Parse("{\"t\":\"release\",\"a\":{\"b\":{\"c\":{\"d\":{\"e\":1}}}}}").Kind);
    }

    [Fact]
    public void OffMessage_CarriesTheReason()
    {
        Assert.Equal("{\"control\":\"off\",\"reason\":\"stopped\"}", Encoding.UTF8.GetString(ScreenControlMessages.OffMessage(ControlReasons.Stopped)));
    }

    [Fact]
    public void BrowserMessages_AreTheDocumentedJson()
    {
        Assert.Equal("{\"control\":\"requesting\"}", Encoding.UTF8.GetString(ScreenControlMessages.RequestingMessage));
        Assert.Equal("{\"control\":\"on\"}", Encoding.UTF8.GetString(ScreenControlMessages.OnMessage));
    }

    [Fact]
    public void PcMessages_AreTheDocumentedJson()
    {
        Assert.Equal("{\"t\":\"control\",\"on\":true}", Encoding.UTF8.GetString(ScreenControlMessages.PcOnMessage));
        Assert.Equal("{\"t\":\"control\",\"on\":false}", Encoding.UTF8.GetString(ScreenControlMessages.PcOffMessage));
    }

    [Fact]
    public void ControlReasons_MatchTheContract()
    {
        Assert.Equal(
            new[] { "stopped", "anonymous", "server-pc", "waiting", "unsupported", "connection", "pc-ended" },
            new[]
            {
                ControlReasons.Stopped,
                ControlReasons.Anonymous,
                ControlReasons.ServerPc,
                ControlReasons.Waiting,
                ControlReasons.Unsupported,
                ControlReasons.Connection,
                ControlReasons.PcEnded,
            });
    }
}
