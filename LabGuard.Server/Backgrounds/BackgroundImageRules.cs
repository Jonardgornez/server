using System.Buffers.Binary;
using System.Security.Cryptography;

namespace LabGuard.Server.Backgrounds;

// What an uploaded background image must pass before it is stored: its size, its declared type, the signature of that type
// and its pixel dimensions. The dimensions are read from the header only, so an oversized image is refused without being decoded.
public static class BackgroundImageRules
{
    public const long MaxBytes = 5 * 1024 * 1024;
    public const int MaxPixelsPerSide = 8192;
    public const long MaxTotalPixels = 7680L * 4320;
    public const string PngContentType = "image/png";
    public const string JpegContentType = "image/jpeg";

    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly byte[] JpegSignature = [0xFF, 0xD8, 0xFF];
    private static readonly byte[] PngHeaderChunkType = "IHDR"u8.ToArray();

    private const int PngIhdrTypeOffset = 12;
    private const int PngWidthOffset = 16;
    private const int PngHeightOffset = 20;
    private const int PngMinimumLength = 24;
    private const byte JpegSoiCode = 0xD8;
    private const byte JpegEoiCode = 0xD9;
    private const byte JpegSosCode = 0xDA;
    private const byte JpegDhtCode = 0xC4;
    private const byte JpegJpgCode = 0xC8;
    private const byte JpegDacCode = 0xCC;

    public static bool IsWithinSizeLimit(long byteCount) => byteCount <= MaxBytes;

    // Each side is capped, and so is the total, so a long thin image can't carry more pixels than an 8K frame.
    public static bool IsWithinPixelLimit(int width, int height) =>
        width <= MaxPixelsPerSide && height <= MaxPixelsPerSide && (long)width * height <= MaxTotalPixels;

    public static bool IsSupportedMediaType(string mediaType) => mediaType is PngContentType or JpegContentType;

    // The media type without parameters such as a charset, lowercased. Empty when there is no content type.
    public static string MediaTypeOf(string? contentType) =>
        contentType?.Split(';')[0].Trim().ToLowerInvariant() ?? "";

    // True only when the declared type is PNG or JPEG and the bytes begin with that type's signature.
    public static bool IsValidImage(string mediaType, ReadOnlySpan<byte> data) => mediaType switch
    {
        PngContentType => data.StartsWith(PngSignature.AsSpan()),
        JpegContentType => data.StartsWith(JpegSignature.AsSpan()),
        _ => false,
    };

    // Reads the width and height from the image header. False when the header can't be read for the declared type.
    public static bool TryReadPixelSize(string mediaType, ReadOnlySpan<byte> data, out int width, out int height) => mediaType switch
    {
        PngContentType => TryReadPngSize(data, out width, out height),
        JpegContentType => TryReadJpegSize(data, out width, out height),
        _ => Unreadable(out width, out height),
    };

    public static string Sha256Of(ReadOnlySpan<byte> data) => Convert.ToHexStringLower(SHA256.HashData(data));

    // The IHDR chunk is always first, and holds the big-endian width and height.
    private static bool TryReadPngSize(ReadOnlySpan<byte> data, out int width, out int height)
    {
        if (data.Length < PngMinimumLength || !data.Slice(PngIhdrTypeOffset, 4).SequenceEqual(PngHeaderChunkType))
        {
            return Unreadable(out width, out height);
        }

        var declaredWidth = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(PngWidthOffset, 4));
        var declaredHeight = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(PngHeightOffset, 4));
        // A value past int.MaxValue can't be held as an int, so it is refused rather than wrapped to a small or negative size.
        if (declaredWidth is 0 or > int.MaxValue || declaredHeight is 0 or > int.MaxValue)
        {
            return Unreadable(out width, out height);
        }

        width = (int)declaredWidth;
        height = (int)declaredHeight;
        return true;
    }

    // Walks the marker segments up to the first start-of-frame marker, which holds the height and width.
    private static bool TryReadJpegSize(ReadOnlySpan<byte> data, out int width, out int height)
    {
        // Starts just after the two-byte SOI marker.
        var position = 2;
        while (position < data.Length)
        {
            if (data[position] != 0xFF)
            {
                return Unreadable(out width, out height);
            }

            // Any run of 0xFF fill bytes may come before the marker code.
            var codeAt = position + 1;
            while (codeAt < data.Length && data[codeAt] == 0xFF)
            {
                codeAt++;
            }

            if (codeAt >= data.Length)
            {
                return Unreadable(out width, out height);
            }

            var marker = data[codeAt];
            position = codeAt + 1;
            if (marker == JpegSoiCode || marker == 0x01 || marker is >= 0xD0 and <= 0xD7)
            {
                continue;
            }

            if (marker is JpegEoiCode or JpegSosCode || position + 2 > data.Length)
            {
                return Unreadable(out width, out height);
            }

            // The segment length counts its own two bytes, so it is at least 2 and never runs past the data.
            var segmentLength = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(position, 2));
            if (segmentLength < 2 || position + segmentLength > data.Length)
            {
                return Unreadable(out width, out height);
            }

            if (IsStartOfFrame(marker))
            {
                // Segment layout: length (2), sample precision (1), height (2), width (2).
                if (segmentLength < 7)
                {
                    return Unreadable(out width, out height);
                }

                height = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(position + 3, 2));
                width = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(position + 5, 2));
                return width > 0 && height > 0;
            }

            position += segmentLength;
        }

        return Unreadable(out width, out height);
    }

    // The start-of-frame markers are C0 to CF, except the three that define other tables (DHT, JPG and DAC).
    private static bool IsStartOfFrame(byte marker) =>
        marker is >= 0xC0 and <= 0xCF && marker is not (JpegDhtCode or JpegJpgCode or JpegDacCode);

    private static bool Unreadable(out int width, out int height)
    {
        width = 0;
        height = 0;
        return false;
    }
}
