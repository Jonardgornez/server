using System.Buffers.Binary;
using System.Text;
using LabGuard.Server.Screen;

namespace LabGuard.Server.Tests;

/// <summary>Builds PNGs chunk by chunk, with correct CRCs unless a test breaks one on purpose.</summary>
internal static class TestPng
{
    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public static byte[] Header(int width = 32, int height = 32, byte bitDepth = 8, byte colourType = 6, byte compression = 0, byte filter = 0, byte interlace = 0)
    {
        var header = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0, 4), (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4, 4), (uint)height);
        header[8] = bitDepth;
        header[9] = colourType;
        header[10] = compression;
        header[11] = filter;
        header[12] = interlace;
        return header;
    }

    public static byte[] Chunk(string type, byte[] data)
    {
        var chunk = new byte[12 + data.Length];
        BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(0, 4), (uint)data.Length);
        Encoding.ASCII.GetBytes(type).CopyTo(chunk, 4);
        data.CopyTo(chunk, 8);
        BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(8 + data.Length, 4), CursorPng.Crc32(chunk.AsSpan(4, 4 + data.Length)));
        return chunk;
    }

    public static byte[] FromChunks(params byte[][] chunks) => [.. Signature, .. chunks.SelectMany(chunk => chunk)];

    // IHDR, one IDAT padded so the whole PNG is the given length, then IEND.
    public static byte[] Build(int width = 32, int height = 32, int length = 64) =>
        FromChunks(Chunk("IHDR", Header(width, height)), Chunk("IDAT", new byte[Math.Max(1, length - 57)]), Chunk("IEND", []));
}

// The pointer PNG's chunk rules: only plain image chunks, in order, each with a correct CRC.
public class CursorPngTests
{
    // Made by WPF's PngBitmapEncoder from a 64 by 64 BGRA32 bitmap, the way the PC client encodes cursors. WPF adds sRGB, gAMA and pHYs.
    private const string WpfPng =
        "iVBORw0KGgoAAAANSUhEUgAAAEAAAABACAYAAACqaXHeAAAAAXNSR0IArs4c6QAAAARnQU1BAACxjwv8YQUAAAAJcEhZcwAADsMAAA7DAcdvqGQAAACuSURB"
        + "VHhe7dOxDQMxAMNA+eHBMlo2/xSpOQGp6gDWOt/Ptt13u9vusfnZ9u6/Y/RDweKHgsVdgILFXYCCxV2AgsVdgILFXYCCxV2AgsVdgILFXYCCxV2AgsVdgILF"
        + "XYCCxV2AgsVdgILFXYCCxV2AgsVdgILFXYCCxV2AgsVdgILFXYCCxV2AgsVdgILFXYCCxV2AgsVdgILFXYCCxV2AgsVdgILFXYCCxV2AgsU/eQRXh6Dgaf4A"
        + "AAAASUVORK5CYII=";

    private static readonly byte[] Idat = TestPng.Chunk("IDAT", [0x78, 0x9C]);
    private static readonly byte[] Iend = TestPng.Chunk("IEND", []);

    private static byte[] Ihdr(byte bitDepth = 8, byte colourType = 6, byte compression = 0, byte filter = 0, byte interlace = 0) =>
        TestPng.Chunk("IHDR", TestPng.Header(32, 32, bitDepth, colourType, compression, filter, interlace));

    private static bool IsAccepted(byte[] png) => CursorPng.TryReadSize(png, 64, out _, out _);

    [Fact]
    public void Crc32_MatchesTheKnownValue()
    {
        Assert.Equal(0xCBF43926u, CursorPng.Crc32("123456789"u8));
    }

    [Fact]
    public void TryReadSize_AWpfEncodedBgra32Cursor_IsAccepted()
    {
        var png = Convert.FromBase64String(WpfPng);

        Assert.True(CursorPng.TryReadSize(png, 64, out var width, out var height));
        Assert.Equal((64, 64), (width, height));
    }

    [Fact]
    public void TryReadSize_AMinimalPng_IsAccepted()
    {
        Assert.True(IsAccepted(TestPng.FromChunks(Ihdr(), Idat, Iend)));
    }

    [Fact]
    public void TryReadSize_EveryAllowedChunk_IsAccepted()
    {
        var png = TestPng.FromChunks(
            Ihdr(bitDepth: 8, colourType: 3),
            TestPng.Chunk("sRGB", [0]),
            TestPng.Chunk("gAMA", new byte[4]),
            TestPng.Chunk("pHYs", new byte[9]),
            TestPng.Chunk("PLTE", new byte[6]),
            TestPng.Chunk("tRNS", new byte[2]),
            Idat,
            Idat,
            Iend);

        Assert.True(IsAccepted(png));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(0, 2)]
    [InlineData(0, 4)]
    [InlineData(0, 8)]
    [InlineData(0, 16)]
    [InlineData(2, 8)]
    [InlineData(2, 16)]
    [InlineData(3, 1)]
    [InlineData(3, 2)]
    [InlineData(3, 4)]
    [InlineData(3, 8)]
    [InlineData(4, 8)]
    [InlineData(4, 16)]
    [InlineData(6, 8)]
    [InlineData(6, 16)]
    public void TryReadSize_AValidColourTypeAndDepth_IsAccepted(byte colourType, byte bitDepth)
    {
        Assert.True(IsAccepted(TestPng.FromChunks(Ihdr(bitDepth, colourType), Idat, Iend)));
    }

    [Theory]
    [InlineData(0, 3)]
    [InlineData(0, 32)]
    [InlineData(2, 4)]
    [InlineData(3, 16)]
    [InlineData(4, 1)]
    [InlineData(6, 4)]
    [InlineData(1, 8)]
    [InlineData(5, 8)]
    [InlineData(7, 8)]
    public void TryReadSize_AnInvalidColourTypeAndDepth_IsRefused(byte colourType, byte bitDepth)
    {
        Assert.False(IsAccepted(TestPng.FromChunks(Ihdr(bitDepth, colourType), Idat, Iend)));
    }

    [Fact]
    public void TryReadSize_ANonZeroCompression_IsRefused()
    {
        Assert.False(IsAccepted(TestPng.FromChunks(Ihdr(compression: 1), Idat, Iend)));
    }

    [Fact]
    public void TryReadSize_ANonZeroFilter_IsRefused()
    {
        Assert.False(IsAccepted(TestPng.FromChunks(Ihdr(filter: 1), Idat, Iend)));
    }

    [Fact]
    public void TryReadSize_Interlaced_IsRefused()
    {
        Assert.False(IsAccepted(TestPng.FromChunks(Ihdr(interlace: 1), Idat, Iend)));
    }

    [Fact]
    public void TryReadSize_AnIhdrNot13Bytes_IsRefused()
    {
        var longHeader = TestPng.Chunk("IHDR", [.. TestPng.Header(), 0]);

        Assert.False(IsAccepted(TestPng.FromChunks(longHeader, Idat, Iend)));
    }

    [Fact]
    public void TryReadSize_IhdrNotFirst_IsRefused()
    {
        Assert.False(IsAccepted(TestPng.FromChunks(Idat, Ihdr(), Iend)));
    }

    [Fact]
    public void TryReadSize_ASecondIhdr_IsRefused()
    {
        Assert.False(IsAccepted(TestPng.FromChunks(Ihdr(), Ihdr(), Idat, Iend)));
    }

    [Fact]
    public void TryReadSize_NoIdat_IsRefused()
    {
        Assert.False(IsAccepted(TestPng.FromChunks(Ihdr(), Iend)));
    }

    [Fact]
    public void TryReadSize_NoIend_IsRefused()
    {
        Assert.False(IsAccepted(TestPng.FromChunks(Ihdr(), Idat)));
    }

    [Fact]
    public void TryReadSize_BytesAfterIend_AreRefused()
    {
        Assert.False(IsAccepted([.. TestPng.FromChunks(Ihdr(), Idat, Iend), 0]));
    }

    [Fact]
    public void TryReadSize_AChunkAfterIend_IsRefused()
    {
        Assert.False(IsAccepted(TestPng.FromChunks(Ihdr(), Idat, Iend, Idat)));
    }

    [Fact]
    public void TryReadSize_AnIendWithData_IsRefused()
    {
        Assert.False(IsAccepted(TestPng.FromChunks(Ihdr(), Idat, TestPng.Chunk("IEND", [0]))));
    }

    [Theory]
    [InlineData("tEXt")]
    [InlineData("zTXt")]
    [InlineData("iTXt")]
    [InlineData("eXIf")]
    [InlineData("iCCP")]
    [InlineData("acTL")]
    [InlineData("fdAT")]
    [InlineData("abcd")]
    public void TryReadSize_AChunkOutsideTheAllowlist_IsRefused(string type)
    {
        Assert.False(IsAccepted(TestPng.FromChunks(Ihdr(), TestPng.Chunk(type, new byte[4]), Idat, Iend)));
    }

    [Theory]
    [InlineData("sRGB", 2)]
    [InlineData("gAMA", 3)]
    [InlineData("pHYs", 10)]
    [InlineData("PLTE", 4)]
    [InlineData("PLTE", 0)]
    [InlineData("PLTE", 771)]
    [InlineData("tRNS", 257)]
    public void TryReadSize_AnAllowedChunkOfTheWrongSize_IsRefused(string type, int length)
    {
        Assert.False(IsAccepted(TestPng.FromChunks(Ihdr(), TestPng.Chunk(type, new byte[length]), Idat, Iend)));
    }

    [Fact]
    public void TryReadSize_ABadCrc_IsRefused()
    {
        var idat = TestPng.Chunk("IDAT", [0x78, 0x9C]);
        idat[^1] ^= 0xFF;

        Assert.False(IsAccepted(TestPng.FromChunks(Ihdr(), idat, Iend)));
    }

    [Fact]
    public void TryReadSize_ChangedIhdrData_FailsItsCrc()
    {
        var png = TestPng.FromChunks(Ihdr(), Idat, Iend);
        png[19] = 16;

        Assert.False(IsAccepted(png));
    }

    [Theory]
    [InlineData(0x7FFFFFFFu)]
    [InlineData(0xFFFFFFFFu)]
    [InlineData(100u)]
    public void TryReadSize_AChunkLengthPastTheBuffer_IsRefused(uint length)
    {
        var png = TestPng.FromChunks(Ihdr(), Idat, Iend);
        BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(33, 4), length);

        Assert.False(IsAccepted(png));
    }

    [Fact]
    public void TryReadSize_ATruncatedChunk_IsRefused()
    {
        var png = TestPng.FromChunks(Ihdr(), Idat, Iend);

        Assert.False(IsAccepted(png[..^4]));
    }

    [Fact]
    public void TryReadSize_NoSignature_IsRefused()
    {
        var png = TestPng.FromChunks(Ihdr(), Idat, Iend);
        png[1] = (byte)'X';

        Assert.False(IsAccepted(png));
    }
}
