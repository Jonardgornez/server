using LabGuard.Server.Screen;

namespace LabGuard.Server.Tests;

public class JpegSizeTests
{
    // A segment as bytes: marker, then its two-byte length, which counts itself and the payload.
    private static byte[] Segment(byte marker, params byte[] payload)
    {
        var length = payload.Length + 2;
        return [0xFF, marker, (byte)(length >> 8), (byte)length, .. payload];
    }

    private static byte[] Sof(byte marker, int width, int height) =>
        Segment(marker, 0x08, (byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width, 0x01, 0x01, 0x11, 0x00);

    private static byte[] Soi() => [0xFF, 0xD8];

    private static bool Read(byte[] bytes, out int width, out int height) => JpegSize.TryRead(bytes, out width, out height);

    [Theory]
    [InlineData(0xC0)]
    [InlineData(0xC1)]
    [InlineData(0xC2)]
    public void TryRead_ReadsTheSizeFromBaselineExtendedAndProgressiveFrames(byte marker)
    {
        var bytes = Soi().Concat(Sof(marker, 1920, 1080)).ToArray();

        Assert.True(Read(bytes, out var width, out var height));
        Assert.Equal(1920, width);
        Assert.Equal(1080, height);
    }

    [Fact]
    public void TryRead_SkipsOrdinarySegmentsBeforeTheFrameHeader()
    {
        var app0 = Segment(0xE0, new byte[14]);
        var quantisation = Segment(0xDB, new byte[65]);
        var bytes = Soi().Concat(app0).Concat(quantisation).Concat(Sof(0xC0, 320, 200)).ToArray();

        Assert.True(Read(bytes, out var width, out var height));
        Assert.Equal((320, 200), (width, height));
    }

    [Fact]
    public void TryRead_SkipsFillBytesBeforeAMarker()
    {
        var bytes = Soi().Concat(new byte[] { 0xFF, 0xFF, 0xFF }).Concat(Sof(0xC0, 64, 48).Skip(1)).ToArray();

        Assert.True(Read(bytes, out var width, out var height));
        Assert.Equal((64, 48), (width, height));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(0xDA)]
    [InlineData(0xD9)]
    public void TryRead_NoFrameHeaderBeforeTheScan_IsNotReadable(byte marker)
    {
        var bytes = Soi().Concat(Segment(marker, new byte[4])).ToArray();

        Assert.False(Read(bytes, out _, out _));
    }

    [Theory]
    [InlineData(0xC3)]
    [InlineData(0xC9)]
    [InlineData(0xCF)]
    public void TryRead_UnsupportedFrameTypes_AreRefused(byte marker)
    {
        var bytes = Soi().Concat(Sof(marker, 100, 100)).ToArray();

        Assert.False(Read(bytes, out _, out _));
    }

    [Fact]
    public void TryRead_AZeroSize_IsRefused()
    {
        Assert.False(Read(Soi().Concat(Sof(0xC0, 0, 480)).ToArray(), out _, out _));
        Assert.False(Read(Soi().Concat(Sof(0xC0, 640, 0)).ToArray(), out _, out _));
    }

    [Theory]
    [InlineData(new byte[] { 0xFF, 0xD8 })]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF })]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47 })]
    [InlineData(new byte[] { 0xFF, 0xD8, 0x00, 0xC0 })]
    public void TryRead_TooShortOrNotAJpeg_IsRefused(byte[] bytes)
    {
        Assert.False(Read(bytes, out _, out _));
    }

    [Fact]
    public void TryRead_TruncatedFrameHeader_IsRefused()
    {
        var full = Soi().Concat(Sof(0xC0, 640, 480)).ToArray();

        // Eleven bytes hold the whole size (the width ends at index 10), so only shorter prefixes are truncated.
        for (var length = 4; length < 11; length++)
        {
            Assert.False(Read(full[..length], out _, out _));
        }
    }

    [Fact]
    public void TryRead_ASegmentLengthOfZeroOrOne_IsRefused()
    {
        Assert.False(Read([0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x00, 0xFF, 0xC0, 0x00, 0x0B], out _, out _));
        Assert.False(Read([0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x01, 0xFF, 0xC0, 0x00, 0x0B], out _, out _));
    }

    [Fact]
    public void TryRead_ASegmentLongerThanTheFrame_IsRefused()
    {
        Assert.False(Read([0xFF, 0xD8, 0xFF, 0xE0, 0xFF, 0xFF, 0x00], out _, out _));
    }

    [Fact]
    public void TryRead_EndlessOrdinarySegments_StopAtTheScanLimit()
    {
        var segments = Enumerable.Range(0, 1000).SelectMany(_ => Segment(0xE0, new byte[2])).ToArray();
        var bytes = Soi().Concat(segments).Concat(Sof(0xC0, 10, 10)).ToArray();

        Assert.False(Read(bytes, out _, out _));
    }

    [Fact]
    public void TryRead_MarkersWithoutLengths_DoNotLoop()
    {
        var bytes = Soi().Concat(Enumerable.Repeat(new byte[] { 0xFF, 0xD0 }, 500).SelectMany(x => x)).ToArray();

        Assert.False(Read(bytes, out _, out _));
    }

    [Fact]
    public void TryRead_NeverThrows_OnRandomBytes()
    {
        var random = new Random(20261106);
        for (var trial = 0; trial < 5000; trial++)
        {
            var bytes = new byte[random.Next(0, 300)];
            random.NextBytes(bytes);
            if (bytes.Length >= 3 && trial % 2 == 0)
            {
                bytes[0] = 0xFF;
                bytes[1] = 0xD8;
                bytes[2] = 0xFF;
            }

            var readable = JpegSize.TryRead(bytes, out var width, out var height);

            Assert.True(!readable || (width > 0 && height > 0));
        }
    }
}
