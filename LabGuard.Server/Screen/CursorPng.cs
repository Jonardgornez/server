using System.Buffers.Binary;

namespace LabGuard.Server.Screen;

// The PNG of a custom pointer shape, checked chunk by chunk before it is relayed to the browser. Only plain image chunks are allowed,
// each with a correct CRC, so nothing but pixels (and the colour hints an encoder such as WPF's adds) reaches the browser's decoder.
public static class CursorPng
{
    private const int SignatureLength = 8;
    // Length, type and CRC around each chunk's data.
    private const int ChunkOverhead = 12;
    private const int IhdrLength = 13;

    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly uint[] CrcTable = BuildCrcTable();

    // True when the PNG is well formed and only uses allowed chunks, with its size 1..maxSide on each side.
    public static bool TryReadSize(ReadOnlySpan<byte> png, int maxSide, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (png.Length < SignatureLength || !png[..SignatureLength].SequenceEqual(Signature))
        {
            return false;
        }

        var offset = SignatureLength;
        var isFirst = true;
        var hasIdat = false;
        while (TryReadChunk(png, offset, out var type, out var data))
        {
            offset += ChunkOverhead + data.Length;
            if (isFirst)
            {
                if (!type.SequenceEqual("IHDR"u8) || !TryReadHeader(data, maxSide, out width, out height))
                {
                    return false;
                }

                isFirst = false;
                continue;
            }

            if (type.SequenceEqual("IEND"u8))
            {
                // IEND carries nothing, and nothing may follow it.
                return data.Length == 0 && hasIdat && offset == png.Length;
            }

            if (type.SequenceEqual("IDAT"u8))
            {
                hasIdat = true;
            }
            else if (!IsAllowedAncillary(type, data.Length))
            {
                return false;
            }
        }

        return false;
    }

    // A chunk whose length fits in the buffer and whose CRC, over its type and data, is correct.
    private static bool TryReadChunk(ReadOnlySpan<byte> png, int offset, out ReadOnlySpan<byte> type, out ReadOnlySpan<byte> data)
    {
        type = default;
        data = default;
        if (png.Length - offset < ChunkOverhead)
        {
            return false;
        }

        var length = BinaryPrimitives.ReadUInt32BigEndian(png.Slice(offset, 4));
        if (length > (uint)(png.Length - offset - ChunkOverhead))
        {
            return false;
        }

        var typeAndData = png.Slice(offset + 4, 4 + (int)length);
        var crc = BinaryPrimitives.ReadUInt32BigEndian(png.Slice(offset + 8 + (int)length, 4));
        if (Crc32(typeAndData) != crc)
        {
            return false;
        }

        type = typeAndData[..4];
        data = typeAndData[4..];
        return true;
    }

    private static bool TryReadHeader(ReadOnlySpan<byte> header, int maxSide, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (header.Length != IhdrLength)
        {
            return false;
        }

        var rawWidth = BinaryPrimitives.ReadUInt32BigEndian(header[..4]);
        var rawHeight = BinaryPrimitives.ReadUInt32BigEndian(header.Slice(4, 4));
        if (rawWidth < 1 || rawWidth > maxSide || rawHeight < 1 || rawHeight > maxSide
            || !IsValidDepth(colourType: header[9], bitDepth: header[8])
            || header[10] != 0 || header[11] != 0 || header[12] != 0)
        {
            return false;
        }

        width = (int)rawWidth;
        height = (int)rawHeight;
        return true;
    }

    // The bit depths the PNG specification allows for each colour type.
    private static bool IsValidDepth(byte colourType, byte bitDepth) => colourType switch
    {
        0 => bitDepth is 1 or 2 or 4 or 8 or 16,
        3 => bitDepth is 1 or 2 or 4 or 8,
        2 or 4 or 6 => bitDepth is 8 or 16,
        _ => false,
    };

    // The palette and transparency chunks, and the fixed-size colour hints WPF's encoder writes. Nothing that carries text or metadata.
    private static bool IsAllowedAncillary(ReadOnlySpan<byte> type, int length) =>
        (type.SequenceEqual("PLTE"u8) && length is > 0 and <= 768 && length % 3 == 0)
        || (type.SequenceEqual("tRNS"u8) && length <= 256)
        || (type.SequenceEqual("sRGB"u8) && length == 1)
        || (type.SequenceEqual("gAMA"u8) && length == 4)
        || (type.SequenceEqual("pHYs"u8) && length == 9);

    public static uint Crc32(ReadOnlySpan<byte> bytes)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var value in bytes)
        {
            crc = CrcTable[(crc ^ value) & 0xFF] ^ (crc >> 8);
        }

        return crc ^ 0xFFFFFFFFu;
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (var n = 0u; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }
}
