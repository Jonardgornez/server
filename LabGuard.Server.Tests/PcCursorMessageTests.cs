using System.Text;
using LabGuard.Server.Screen;

namespace LabGuard.Server.Tests;

// The PC's pointer shape messages: read strictly, and relayed to the browser only in the canonical form.
public class PcCursorMessageTests
{
    private static PcCursorParse Parse(string json) => PcCursorMessage.Parse(Encoding.UTF8.GetBytes(json));

    private static string BrowserText(PcCursorParse parse) => Encoding.UTF8.GetString(parse.BrowserMessage!);

    internal static byte[] Png(int width = 32, int height = 32, int length = 64) => TestPng.Build(width, height, length);

    internal static string ImageJson(int x, int y, string base64) =>
        $"{{\"t\":\"cursor\",\"k\":\"image\",\"x\":{x},\"y\":{y},\"png\":\"{base64}\"}}";

    [Theory]
    [InlineData("default")]
    [InlineData("text")]
    [InlineData("wait")]
    [InlineData("progress")]
    [InlineData("crosshair")]
    [InlineData("pointer")]
    [InlineData("help")]
    [InlineData("not-allowed")]
    [InlineData("move")]
    [InlineData("ns-resize")]
    [InlineData("ew-resize")]
    [InlineData("nwse-resize")]
    [InlineData("nesw-resize")]
    [InlineData("col-resize")]
    [InlineData("row-resize")]
    [InlineData("none")]
    public void Parse_EveryStandardName_IsRelayedAsTheBrowserMessage(string name)
    {
        var parse = Parse($"{{\"t\":\"cursor\",\"k\":\"{name}\"}}");

        Assert.Equal(PcCursorStatus.Valid, parse.Status);
        Assert.Equal($"{{\"cursor\":\"{name}\"}}", BrowserText(parse));
    }

    [Fact]
    public void Parse_AStandardName_InAnyFieldOrderAndSpacing_IsCanonical()
    {
        var parse = Parse("{ \"k\": \"text\" , \"t\": \"cursor\" }");

        Assert.Equal("{\"cursor\":\"text\"}", BrowserText(parse));
    }

    [Fact]
    public void Parse_AnImage_IsRelayedWithItsHotspotAndPng()
    {
        var base64 = Convert.ToBase64String(Png(32, 32));

        var parse = Parse(ImageJson(3, 4, base64));

        Assert.Equal(PcCursorStatus.Valid, parse.Status);
        Assert.Equal($"{{\"cursor\":\"image\",\"x\":3,\"y\":4,\"png\":\"{base64}\"}}", BrowserText(parse));
    }

    [Fact]
    public void Parse_AnImage_InAnotherFieldOrder_IsCanonical()
    {
        var base64 = Convert.ToBase64String(Png(16, 16));

        var parse = Parse($"{{\"png\":\"{base64}\",\"y\":15,\"x\":0,\"k\":\"image\",\"t\":\"cursor\"}}");

        Assert.Equal($"{{\"cursor\":\"image\",\"x\":0,\"y\":15,\"png\":\"{base64}\"}}", BrowserText(parse));
    }

    [Theory]
    [InlineData(1, 1, 0, 0)]
    [InlineData(64, 64, 63, 63)]
    [InlineData(64, 1, 63, 0)]
    public void Parse_AnImageAtTheSizeAndHotspotBounds_IsValid(int width, int height, int x, int y)
    {
        var parse = Parse(ImageJson(x, y, Convert.ToBase64String(Png(width, height))));

        Assert.Equal(PcCursorStatus.Valid, parse.Status);
    }

    [Fact]
    public void Parse_APngOf16KiB_IsValid()
    {
        var parse = Parse(ImageJson(0, 0, Convert.ToBase64String(Png(length: PcCursorMessage.MaxPngBytes))));

        Assert.Equal(PcCursorStatus.Valid, parse.Status);
    }

    [Fact]
    public void Parse_APngOver16KiB_IsInvalid()
    {
        var parse = Parse(ImageJson(0, 0, Convert.ToBase64String(Png(length: PcCursorMessage.MaxPngBytes + 1))));

        Assert.Equal(PcCursorStatus.Invalid, parse.Status);
    }

    [Theory]
    [InlineData(0, 32)]
    [InlineData(32, 0)]
    [InlineData(65, 32)]
    [InlineData(32, 65)]
    public void Parse_AnImageSideOutside1To64_IsInvalid(int width, int height)
    {
        var parse = Parse(ImageJson(0, 0, Convert.ToBase64String(Png(width, height))));

        Assert.Equal(PcCursorStatus.Invalid, parse.Status);
    }

    [Theory]
    [InlineData(32, 0)]
    [InlineData(0, 32)]
    [InlineData(-1, 0)]
    [InlineData(0, -1)]
    [InlineData(64, 0)]
    public void Parse_AHotspotOutsideTheImage_IsInvalid(int x, int y)
    {
        var parse = Parse(ImageJson(x, y, Convert.ToBase64String(Png(32, 32))));

        Assert.Equal(PcCursorStatus.Invalid, parse.Status);
    }

    [Fact]
    public void Parse_ANonPng_IsInvalid()
    {
        var gif = Png();
        "GIF89a"u8.CopyTo(gif);

        Assert.Equal(PcCursorStatus.Invalid, Parse(ImageJson(0, 0, Convert.ToBase64String(gif))).Status);
    }

    [Fact]
    public void Parse_APngShorterThanItsHeader_IsInvalid()
    {
        Assert.Equal(PcCursorStatus.Invalid, Parse(ImageJson(0, 0, Convert.ToBase64String(Png()[..23]))).Status);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not base64!")]
    [InlineData("iVBORw0KGgo")]
    [InlineData("iVBO Rw0K")]
    [InlineData("iVBO\\nRw0K")]
    [InlineData("iVBORw0K-_==")]
    [InlineData("iVBORw0KGgo=====")]
    public void Parse_BadBase64_IsInvalid(string base64)
    {
        Assert.Equal(PcCursorStatus.Invalid, Parse(ImageJson(0, 0, base64)).Status);
    }

    [Theory]
    [InlineData("{\"t\":\"cursor\",\"k\":\"grab\"}")]
    [InlineData("{\"t\":\"cursor\",\"k\":\"Default\"}")]
    [InlineData("{\"t\":\"cursor\",\"k\":\"url(x)\"}")]
    [InlineData("{\"t\":\"cursor\",\"k\":\"\"}")]
    public void Parse_AnUnknownName_IsInvalid(string json)
    {
        Assert.Equal(PcCursorStatus.Invalid, Parse(json).Status);
    }

    [Theory]
    [InlineData("{\"t\":\"cursor\"}")]
    [InlineData("{\"t\":\"cursor\",\"k\":\"text\",\"x\":0}")]
    [InlineData("{\"t\":\"cursor\",\"k\":\"text\",\"k\":\"move\"}")]
    [InlineData("{\"t\":\"cursor\",\"k\":5}")]
    [InlineData("{\"t\":\"cursor\",\"k\":null}")]
    [InlineData("{\"t\":\"cursor\",\"k\":\"text\",\"t\":\"cursor\"}")]
    public void Parse_AStandardNameWithoutExactlyItsFields_IsInvalid(string json)
    {
        Assert.Equal(PcCursorStatus.Invalid, Parse(json).Status);
    }

    [Theory]
    [InlineData("{{\"t\":\"cursor\",\"k\":\"image\",\"x\":0,\"y\":0}}")]
    [InlineData("{{\"t\":\"cursor\",\"k\":\"image\",\"y\":0,\"png\":\"{0}\"}}")]
    [InlineData("{{\"t\":\"cursor\",\"k\":\"image\",\"x\":0,\"png\":\"{0}\"}}")]
    [InlineData("{{\"t\":\"cursor\",\"k\":\"image\",\"x\":0,\"y\":0,\"png\":\"{0}\",\"w\":1}}")]
    [InlineData("{{\"t\":\"cursor\",\"k\":\"image\",\"x\":0,\"x\":0,\"y\":0,\"png\":\"{0}\"}}")]
    [InlineData("{{\"t\":\"cursor\",\"k\":\"image\",\"x\":1.5,\"y\":0,\"png\":\"{0}\"}}")]
    [InlineData("{{\"t\":\"cursor\",\"k\":\"image\",\"x\":1.0,\"y\":0,\"png\":\"{0}\"}}")]
    [InlineData("{{\"t\":\"cursor\",\"k\":\"image\",\"x\":\"1\",\"y\":0,\"png\":\"{0}\"}}")]
    [InlineData("{{\"t\":\"cursor\",\"k\":\"image\",\"x\":0,\"y\":1e1,\"png\":\"{0}\"}}")]
    [InlineData("{{\"t\":\"cursor\",\"k\":\"image\",\"x\":0,\"y\":0,\"png\":5}}")]
    [InlineData("{{\"t\":\"cursor\",\"k\":\"text\",\"x\":0,\"y\":0,\"png\":\"{0}\"}}")]
    public void Parse_AnImageWithoutExactlyItsFields_IsInvalid(string format)
    {
        var json = string.Format(System.Globalization.CultureInfo.InvariantCulture, format, Convert.ToBase64String(Png()));

        Assert.Equal(PcCursorStatus.Invalid, Parse(json).Status);
    }

    [Theory]
    [InlineData("xxxxxxxxxxxxxxxxx")]
    [InlineData("\"cursor\"")]
    [InlineData("[\"cursor\",\"text\"]")]
    [InlineData("{\"t\":\"control\",\"on\":true}")]
    [InlineData("{\"k\":\"text\"}")]
    [InlineData("{\"t\":\"Cursor\",\"k\":\"text\"}")]
    [InlineData("{\"t\":\"cursor\",\"k\":\"text\"")]
    public void Parse_TextThatIsNotACursorMessage_IsNotCursor(string text)
    {
        Assert.Equal(PcCursorStatus.NotCursor, Parse(text).Status);
    }

    [Fact]
    public void Parse_AMessageOverTheCap_IsNotCursor()
    {
        var oversized = $"{{\"t\":\"cursor\",\"k\":\"text\"{new string(' ', PcCursorMessage.MaxMessageBytes)}}}";

        Assert.Equal(PcCursorStatus.NotCursor, Parse(oversized).Status);
    }
}
