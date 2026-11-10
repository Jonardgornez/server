using System.Buffers.Binary;

namespace LabGuard.Server.Tests;

// Image bytes for the background tests. Only the headers are real: nothing is decoded, so the rest is zero padding.
internal static class BackgroundTestImages
{
    public const int FiveMegabytes = 5 * 1024 * 1024;

    public static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    // A PNG whose IHDR chunk gives the size, padded with zeros to totalLength.
    public static byte[] Png(int width = 1, int height = 1, int totalLength = 33)
    {
        var bytes = new byte[totalLength];
        PngSignature.CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(8), 13);
        "IHDR"u8.CopyTo(bytes.AsSpan(12));
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(16), (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(20), (uint)height);
        return bytes;
    }

    // A JPEG with a Huffman table ahead of its start-of-frame segment, the order real files use.
    public static byte[] Jpeg(int width = 1, int height = 1) =>
    [
        0xFF, 0xD8,
        0xFF, 0xC4, 0x00, 0x04, 0x00, 0x00,
        0xFF, 0xC0, 0x00, 0x0B, 0x08,
        (byte)(height >> 8), (byte)height,
        (byte)(width >> 8), (byte)width,
        0x01, 0x01, 0x11, 0x00,
        0xFF, 0xD9,
    ];

    // A JPEG that ends before any start-of-frame segment, so it has no readable size.
    public static byte[] JpegWithoutFrame() => [0xFF, 0xD8, 0xFF, 0xC4, 0x00, 0x04, 0x00, 0x00, 0xFF, 0xD9];
}
