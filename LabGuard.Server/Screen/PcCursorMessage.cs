using System.Globalization;
using System.Text;
using System.Text.Json;

namespace LabGuard.Server.Screen;

// The PC's pointer shape during remote control, so the administrator's page can show it over the screen view. The PC sends
// {"t":"cursor","k":"<name>"} for a standard shape, or {"t":"cursor","k":"image","x":n,"y":n,"png":"<base64>"} for any other.
// Read strictly, and relayed to the browser only in the canonical form written here, never as the PC sent it.
public static class PcCursorMessage
{
    // The hard cap on a PC text message. Anything over the short-text limit must be a cursor message.
    public const int MaxMessageBytes = 24_576;
    public const int MaxPngBytes = 16 * 1024;
    public const int MaxImageSide = 64;

    public const string DefaultName = "default";
    private const string ImageName = "image";

    public static readonly IReadOnlySet<string> Names = new HashSet<string>(StringComparer.Ordinal)
    {
        DefaultName, "text", "wait", "progress", "crosshair", "pointer", "help", "not-allowed", "move",
        "ns-resize", "ew-resize", "nwse-resize", "nesw-resize", "col-resize", "row-resize", "none",
    };

    public static readonly byte[] BrowserDefaultMessage = "{\"cursor\":\"default\"}"u8.ToArray();

    private static readonly string[] StandardFields = ["k", "t"];
    private static readonly string[] ImageFields = ["k", "png", "t", "x", "y"];

    // NotCursor when the text is not a JSON object whose "t" is "cursor". Invalid when it is one but breaks a rule. Otherwise the
    // message the browser gets.
    public static PcCursorParse Parse(ReadOnlySpan<byte> payload)
    {
        if (payload.Length > MaxMessageBytes)
        {
            return PcCursorParse.NotCursor;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(payload.ToArray(), new JsonDocumentOptions { MaxDepth = 2 });
        }
        catch (JsonException)
        {
            return PcCursorParse.NotCursor;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("t", out var type)
                || type.ValueKind != JsonValueKind.String
                || type.GetString() != "cursor")
            {
                return PcCursorParse.NotCursor;
            }

            return ReadCursor(root) is { } browser ? new PcCursorParse(PcCursorStatus.Valid, browser) : PcCursorParse.Invalid;
        }
    }

    private static byte[]? ReadCursor(JsonElement root)
    {
        if (!TryReadString(root, "k", out var name))
        {
            return null;
        }

        if (Names.Contains(name))
        {
            return HasExactFields(root, StandardFields) ? Encoding.UTF8.GetBytes($"{{\"cursor\":\"{name}\"}}") : null;
        }

        return name == ImageName && HasExactFields(root, ImageFields) ? ReadImage(root) : null;
    }

    private static byte[]? ReadImage(JsonElement root)
    {
        if (!TryReadInt(root, "x", out var x)
            || !TryReadInt(root, "y", out var y)
            || !TryReadString(root, "png", out var base64)
            || DecodePng(base64) is not { } png
            || !CursorPng.TryReadSize(png, MaxImageSide, out var width, out var height)
            || x < 0 || x >= width
            || y < 0 || y >= height)
        {
            return null;
        }

        var json = string.Create(
            CultureInfo.InvariantCulture,
            $"{{\"cursor\":\"image\",\"x\":{x},\"y\":{y},\"png\":\"{Convert.ToBase64String(png)}\"}}");
        return Encoding.UTF8.GetBytes(json);
    }

    // Standard alphabet and padding only. Convert.TryFromBase64String alone would also skip whitespace.
    private static byte[]? DecodePng(string base64)
    {
        if (base64.Length == 0 || base64.Length % 4 != 0 || !base64.All(IsBase64Char))
        {
            return null;
        }

        var buffer = new byte[MaxPngBytes];
        return Convert.TryFromBase64String(base64, buffer, out var written) ? buffer[..written] : null;
    }

    private static bool IsBase64Char(char c) => char.IsAsciiLetterOrDigit(c) || c is '+' or '/' or '=';

    // Exactly these fields, each once.
    private static bool HasExactFields(JsonElement root, string[] expected)
    {
        var names = root.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).ToArray();
        return names.SequenceEqual(expected, StringComparer.Ordinal);
    }

    private static bool TryReadString(JsonElement root, string field, out string value)
    {
        value = "";
        if (!root.TryGetProperty(field, out var element) || element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = element.GetString()!;
        return true;
    }

    private static bool TryReadInt(JsonElement root, string field, out int value)
    {
        value = 0;
        return root.TryGetProperty(field, out var element)
            && element.ValueKind == JsonValueKind.Number
            && element.TryGetInt32(out value);
    }
}

public enum PcCursorStatus
{
    NotCursor,
    Invalid,
    Valid,
}

// The outcome of reading a PC text as a cursor message. BrowserMessage is set only when it is valid.
public readonly record struct PcCursorParse(PcCursorStatus Status, byte[]? BrowserMessage = null)
{
    public static readonly PcCursorParse NotCursor = new(PcCursorStatus.NotCursor);
    public static readonly PcCursorParse Invalid = new(PcCursorStatus.Invalid);
}
