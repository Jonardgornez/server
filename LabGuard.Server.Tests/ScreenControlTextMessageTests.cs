using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using LabGuard.Server.Screen;

namespace LabGuard.Server.Tests;

// The paste-text chunk: {"t":"text","s":"..."} with 1 to 64 UTF-16 units, only line breaks and tabs as controls, and no unpaired surrogate.
public class ScreenControlTextMessageTests
{
    // The canonical message is at most this size: 64 units of 3 UTF-8 bytes plus the fields. The PC's limit (512 bytes) is above it.
    private const int MaxCanonicalBytes = 211;

    private static readonly string Grinning = char.ConvertFromUtf32(0x1F600);
    private static readonly string Family = string.Join("\u200D", char.ConvertFromUtf32(0x1F468), char.ConvertFromUtf32(0x1F469), char.ConvertFromUtf32(0x1F467));

    private static BrowserControlMessage Parse(string json) =>
        ScreenControlMessages.Parse(WebSocketMessageType.Text, Encoding.UTF8.GetBytes(json));

    // As the browser's JSON.stringify writes it: only the quote, the backslash and the controls are escaped.
    private static string Browser(string text)
    {
        var escaped = text.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\t", "\\t");
        return $"{{\"t\":\"text\",\"s\":\"{escaped}\"}}";
    }

    private static string? TextOf(string canonical)
    {
        using var document = JsonDocument.Parse(canonical);
        return document.RootElement.GetProperty("s").GetString();
    }

    [Fact]
    public void Parse_AsciiText_IsRelayedAsTheCanonicalMessage()
    {
        var message = Parse("{\"t\":\"text\",\"s\":\"Hello, world!\"}");

        Assert.Equal(BrowserControlKind.Input, message.Kind);
        Assert.Equal("{\"t\":\"text\",\"s\":\"Hello, world!\"}", message.Canonical);
    }

    [Fact]
    public void Parse_FieldsInAnotherOrderWithSpaces_AreRelayedInTheCanonicalOrder()
    {
        var message = Parse("{ \"s\" : \"abc\" , \"t\" : \"text\" }");

        Assert.Equal("{\"t\":\"text\",\"s\":\"abc\"}", message.Canonical);
    }

    [Fact]
    public void Parse_AnEmojiPair_IsAccepted_AndWrittenAsItself()
    {
        var message = Parse(Browser(Grinning));

        Assert.Equal(BrowserControlKind.Input, message.Kind);
        Assert.Equal($"{{\"t\":\"text\",\"s\":\"{Grinning}\"}}", message.Canonical);
    }

    [Fact]
    public void Parse_AnEmojiWithZeroWidthJoiners_IsAccepted()
    {
        var message = Parse(Browser(Family));

        Assert.Equal(BrowserControlKind.Input, message.Kind);
        Assert.Equal(Family, TextOf(message.Canonical));
    }

    [Fact]
    public void Parse_LineBreaksAndTabs_AreAccepted_AndEscaped()
    {
        var message = Parse("{\"t\":\"text\",\"s\":\"a\\nb\\tc\"}");

        Assert.Equal(BrowserControlKind.Input, message.Kind);
        Assert.Equal("{\"t\":\"text\",\"s\":\"a\\nb\\tc\"}", message.Canonical);
    }

    [Fact]
    public void Parse_QuoteAndBackslash_AreEscaped_AndOtherEscapesAreWrittenAsTheirCharacter()
    {
        var message = Parse("{\"t\":\"text\",\"s\":\"\\\"\\\\\\/\\u0041\\u00e9<&'\"}");

        Assert.Equal("{\"t\":\"text\",\"s\":\"\\\"\\\\/A\u00e9<&'\"}", message.Canonical);
    }

    [Fact]
    public void Parse_ACarriageReturn_IsDropped()
    {
        Assert.Equal(BrowserControlKind.Invalid, Parse("{\"t\":\"text\",\"s\":\"a\\r\\nb\"}").Kind);
        Assert.Equal(BrowserControlKind.Invalid, Parse("{\"t\":\"text\",\"s\":\"\\r\"}").Kind);
    }

    [Theory]
    [InlineData("\\u0000")]
    [InlineData("\\u0001")]
    [InlineData("\\u0008")]
    [InlineData("\\u000b")]
    [InlineData("\\u000c")]
    [InlineData("\\u001b")]
    [InlineData("\\u001f")]
    [InlineData("\\u007f")]
    [InlineData("\\u0080")]
    [InlineData("\\u0085")]
    [InlineData("\\u009f")]
    public void Parse_AnyOtherControlCharacter_IsDropped(string escape)
    {
        Assert.Equal(BrowserControlKind.Invalid, Parse($"{{\"t\":\"text\",\"s\":\"ok{escape}\"}}").Kind);
    }

    [Theory]
    [InlineData("\\u0020")]
    [InlineData("\\u007e")]
    [InlineData("\\u00a0")]
    [InlineData("\\u00ad")]
    [InlineData("\\u2028")]
    [InlineData("\\ufeff")]
    [InlineData("\\ue000")]
    public void Parse_CharactersNextToTheRefusedRanges_AreAccepted(string escape)
    {
        Assert.Equal(BrowserControlKind.Input, Parse($"{{\"t\":\"text\",\"s\":\"{escape}\"}}").Kind);
    }

    [Theory]
    [InlineData("\\ud83d")]
    [InlineData("\\ud83dA")]
    [InlineData("\\ude00")]
    [InlineData("A\\ude00\\ud83d")]
    public void Parse_AnUnpairedSurrogate_IsDropped(string escape)
    {
        Assert.Equal(BrowserControlKind.Invalid, Parse($"{{\"t\":\"text\",\"s\":\"{escape}\"}}").Kind);
    }

    [Fact]
    public void Parse_EmptyText_IsDropped()
    {
        Assert.Equal(BrowserControlKind.Invalid, Parse("{\"t\":\"text\",\"s\":\"\"}").Kind);
    }

    [Fact]
    public void Parse_64Units_IsAccepted_And65IsDropped()
    {
        Assert.Equal(BrowserControlKind.Input, Parse(Browser(new string('a', ScreenControlMessages.MaxTextLength))).Kind);
        Assert.Equal(BrowserControlKind.Invalid, Parse(Browser(new string('a', ScreenControlMessages.MaxTextLength + 1))).Kind);
    }

    [Fact]
    public void Parse_LengthCountsUtf16Units_So32EmojiPairsFit_And33DoNot()
    {
        Assert.Equal(BrowserControlKind.Input, Parse(Browser(string.Concat(Enumerable.Repeat(Grinning, 32)))).Kind);
        Assert.Equal(BrowserControlKind.Invalid, Parse(Browser(string.Concat(Enumerable.Repeat(Grinning, 33)))).Kind);
    }

    [Theory]
    [InlineData("{\"t\":\"text\"}")]
    [InlineData("{\"t\":\"text\",\"s\":\"a\",\"d\":true}")]
    [InlineData("{\"t\":\"text\",\"s\":\"a\",\"s\":\"b\"}")]
    [InlineData("{\"t\":\"text\",\"text\":\"a\"}")]
    [InlineData("{\"t\":\"Text\",\"s\":\"a\"}")]
    [InlineData("{\"t\":\"text\",\"s\":5}")]
    [InlineData("{\"t\":\"text\",\"s\":null}")]
    [InlineData("{\"t\":\"text\",\"s\":true}")]
    [InlineData("{\"t\":\"text\",\"s\":[\"a\"]}")]
    [InlineData("{\"t\":\"text\",\"s\":{\"v\":\"a\"}}")]
    [InlineData("[\"text\",\"a\"]")]
    public void Parse_AnythingButExactlyTAndAStringS_IsDropped(string json)
    {
        Assert.Equal(BrowserControlKind.Invalid, Parse(json).Kind);
    }

    [Fact]
    public void Parse_Text_HasItsOwnRateClass_AndHoldsNothing()
    {
        var message = Parse(Browser("abc"));

        Assert.Equal(InputRate.Text, message.Rate);
        Assert.Equal(HeldEffect.None, message.Held);
        Assert.Equal("", message.HeldKey);
    }

    [Theory]
    [InlineData("plain text")]
    [InlineData("quote \" backslash \\ slash /")]
    [InlineData("line\nbreak\ttab")]
    [InlineData("\u00e9\u4e2d\u2028\u2029\ufeff\ue000\u0378")]
    public void Canonical_ReadsBackAsTheSameText(string text)
    {
        var message = Parse(Browser(text));

        Assert.Equal(text, TextOf(message.Canonical));
    }

    [Fact]
    public void Canonical_ReadsBackAsTheSameText_ForEmoji()
    {
        Assert.Equal(Grinning + Family, TextOf(Parse(Browser(Grinning + Family)).Canonical));
    }

    // The largest valid chunk as the browser sends it: 64 units of 3 UTF-8 bytes each, or 64 escaped line breaks.
    [Theory]
    [InlineData('\u4e2d')]
    [InlineData('\u2028')]
    [InlineData('\uffff')]
    [InlineData('\n')]
    [InlineData('"')]
    public void TheLargestValidChunk_FitsTheBrowserLimit_AndItsCanonicalFormIsAtMost211Bytes(char unit)
    {
        var json = Browser(new string(unit, ScreenControlMessages.MaxTextLength));

        var message = Parse(json);

        Assert.Equal(BrowserControlKind.Input, message.Kind);
        Assert.True(Encoding.UTF8.GetByteCount(json) <= ScreenProtocol.MaxBrowserMessageBytes);
        Assert.True(Encoding.UTF8.GetByteCount(message.Canonical) <= MaxCanonicalBytes);
    }

    [Fact]
    public void TheLargestEmojiChunk_FitsTheBrowserLimit_AndItsCanonicalFormIsAtMost211Bytes()
    {
        var json = Browser(string.Concat(Enumerable.Repeat(Grinning, ScreenControlMessages.MaxTextLength / 2)));

        var message = Parse(json);

        Assert.Equal(BrowserControlKind.Input, message.Kind);
        Assert.True(Encoding.UTF8.GetByteCount(json) <= ScreenProtocol.MaxBrowserMessageBytes);
        Assert.True(Encoding.UTF8.GetByteCount(message.Canonical) <= MaxCanonicalBytes);
    }
}
