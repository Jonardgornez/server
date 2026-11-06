namespace LabGuard.Server.Screen;

// Reads a JPEG's size from its frame header (SOF0, SOF1 or SOF2) without decoding anything.
// Hostile input is bounded: at most MaxSegments header segments are read, and every length is checked against the frame.
public static class JpegSize
{
    private const int MaxSegments = 32;
    private const byte StartOfFrameBaseline = 0xC0;
    private const byte StartOfFrameExtended = 0xC1;
    private const byte StartOfFrameProgressive = 0xC2;
    private const byte StartOfScan = 0xDA;
    private const byte EndOfImage = 0xD9;

    public static bool TryRead(ReadOnlySpan<byte> frame, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (frame.Length < 4 || frame[0] != 0xFF || frame[1] != 0xD8 || frame[2] != 0xFF)
        {
            return false;
        }

        var position = 2;
        for (var segment = 0; segment < MaxSegments && position < frame.Length; segment++)
        {
            if (frame[position] != 0xFF)
            {
                return false;
            }

            // Fill bytes may precede a marker.
            while (position < frame.Length && frame[position] == 0xFF)
            {
                position++;
            }

            if (position >= frame.Length)
            {
                return false;
            }

            var marker = frame[position++];
            if (marker == 0x00 || marker == EndOfImage || marker == StartOfScan)
            {
                // Stuffed data, the end of the image, or entropy-coded data before any frame header: nothing to read.
                return false;
            }

            if (marker == 0x01 || marker is >= 0xD0 and <= 0xD8)
            {
                // Standalone markers (TEM, RSTn, SOI) have no length.
                continue;
            }

            if (position + 2 > frame.Length)
            {
                return false;
            }

            var length = (frame[position] << 8) | frame[position + 1];
            if (length < 2)
            {
                return false;
            }

            if (marker is StartOfFrameBaseline or StartOfFrameExtended or StartOfFrameProgressive)
            {
                // The segment holds its length, the sample precision, then the height and the width.
                if (length < 8 || position + 7 > frame.Length)
                {
                    return false;
                }

                height = (frame[position + 3] << 8) | frame[position + 4];
                width = (frame[position + 5] << 8) | frame[position + 6];
                return width > 0 && height > 0;
            }

            // Other frame types (lossless, arithmetic-coded, hierarchical) can't be sized here, so they are refused.
            // DHT (C4), JPG (C8) and DAC (CC) are ordinary segments.
            if (marker is >= 0xC0 and <= 0xCF && marker is not (0xC4 or 0xC8 or 0xCC))
            {
                return false;
            }

            if (position + length > frame.Length)
            {
                return false;
            }

            position += length;
        }

        return false;
    }
}
