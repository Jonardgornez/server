using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace LabGuard.Server.Screen;

// The remote control wire. The browser's input is parsed strictly, and only a message rebuilt in its canonical form reaches the PC.
// Every field is checked before the text is rebuilt, so nothing the browser sent goes to the PC unless it has exactly this shape.
public static class ScreenControlMessages
{
    public const int MaxCoordinate = 65535;
    public const int MaxWheelDelta = 1200;
    public const int MaxButton = 4;
    public const int MaxKeyCodeLength = 32;

    private static readonly JsonDocumentOptions ParseOptions = new() { MaxDepth = 4, CommentHandling = JsonCommentHandling.Disallow };
    private static readonly BrowserControlMessage Dropped = new(BrowserControlKind.Invalid);

    // Sent to the PC.
    public static readonly byte[] PcOnMessage = "{\"t\":\"control\",\"on\":true}"u8.ToArray();
    public static readonly byte[] PcOffMessage = "{\"t\":\"control\",\"on\":false}"u8.ToArray();

    // Sent to the browser.
    public static readonly byte[] RequestingMessage = "{\"control\":\"requesting\"}"u8.ToArray();
    public static readonly byte[] OnMessage = "{\"control\":\"on\"}"u8.ToArray();

    // The reason is always one of ControlReasons, so it never needs escaping.
    public static byte[] OffMessage(string reason) => Encoding.UTF8.GetBytes($"{{\"control\":\"off\",\"reason\":\"{reason}\"}}");

    // Anything that is not exactly one of the documented shapes is Invalid, and the caller drops it.
    public static BrowserControlMessage Parse(WebSocketMessageType type, ReadOnlyMemory<byte> payload)
    {
        if (type != WebSocketMessageType.Text)
        {
            return Dropped;
        }

        try
        {
            using var document = JsonDocument.Parse(payload, ParseOptions);
            return ParseObject(document.RootElement);
        }
        catch (JsonException)
        {
            return Dropped;
        }
        catch (InvalidOperationException)
        {
            // A lone surrogate escape is rejected when its text is read. That is a malformed message too.
            return Dropped;
        }
    }

    private static BrowserControlMessage ParseObject(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !TryReadFields(root, out var fields))
        {
            return Dropped;
        }

        if (!fields.TryGetValue("t", out var typeField) || typeField.ValueKind != JsonValueKind.String)
        {
            return Dropped;
        }

        return typeField.GetString() switch
        {
            "control" => ParseControl(fields),
            "move" => ParseMove(fields),
            "down" => ParseButton("down", fields),
            "up" => ParseButton("up", fields),
            "wheel" => ParseWheel(fields),
            "key" => ParseKey(fields),
            "release" => HasExactly(fields, "t")
                ? new BrowserControlMessage(BrowserControlKind.Input, "{\"t\":\"release\"}", InputRate.Exempt, HeldEffect.ReleaseAll)
                : Dropped,
            "sas" => HasExactly(fields, "t")
                ? new BrowserControlMessage(BrowserControlKind.Input, "{\"t\":\"sas\"}", InputRate.Sas)
                : Dropped,
            _ => Dropped,
        };
    }

    private static BrowserControlMessage ParseControl(Dictionary<string, JsonElement> fields)
    {
        if (!HasExactly(fields, "t", "on") || fields["on"].ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return Dropped;
        }

        return new BrowserControlMessage(fields["on"].ValueKind == JsonValueKind.True ? BrowserControlKind.ControlOn : BrowserControlKind.ControlOff);
    }

    private static BrowserControlMessage ParseMove(Dictionary<string, JsonElement> fields)
    {
        if (!HasExactly(fields, "t", "x", "y")
            || !TryReadInteger(fields["x"], 0, MaxCoordinate, out var x)
            || !TryReadInteger(fields["y"], 0, MaxCoordinate, out var y))
        {
            return Dropped;
        }

        return new BrowserControlMessage(BrowserControlKind.Move, FormattableString.Invariant($"{{\"t\":\"move\",\"x\":{x},\"y\":{y}}}"), InputRate.Move);
    }

    private static BrowserControlMessage ParseButton(string name, Dictionary<string, JsonElement> fields)
    {
        if (!HasExactly(fields, "t", "b", "x", "y")
            || !TryReadInteger(fields["b"], 0, MaxButton, out var button)
            || !TryReadInteger(fields["x"], 0, MaxCoordinate, out var x)
            || !TryReadInteger(fields["y"], 0, MaxCoordinate, out var y))
        {
            return Dropped;
        }

        // A press is capped like a scroll. A release is exempt from the caps, so a held button can always be let go.
        var isPress = name == "down";
        return new BrowserControlMessage(
            BrowserControlKind.Input,
            FormattableString.Invariant($"{{\"t\":\"{name}\",\"b\":{button},\"x\":{x},\"y\":{y}}}"),
            isPress ? InputRate.Other : InputRate.Exempt,
            isPress ? HeldEffect.Press : HeldEffect.Release,
            $"b{button}");
    }

    // A wheel amount beyond the range is clamped to it, as the protocol says, rather than dropped.
    private static BrowserControlMessage ParseWheel(Dictionary<string, JsonElement> fields)
    {
        if (!HasExactly(fields, "t", "dx", "dy")
            || !TryReadInteger(fields["dx"], out var deltaX)
            || !TryReadInteger(fields["dy"], out var deltaY))
        {
            return Dropped;
        }

        var clampedX = Math.Clamp(deltaX, -MaxWheelDelta, MaxWheelDelta);
        var clampedY = Math.Clamp(deltaY, -MaxWheelDelta, MaxWheelDelta);
        return new BrowserControlMessage(BrowserControlKind.Input, FormattableString.Invariant($"{{\"t\":\"wheel\",\"dx\":{clampedX},\"dy\":{clampedY}}}"), InputRate.Other);
    }

    private static BrowserControlMessage ParseKey(Dictionary<string, JsonElement> fields)
    {
        if (!HasExactly(fields, "t", "c", "d")
            || fields["c"].ValueKind != JsonValueKind.String
            || fields["d"].ValueKind is not (JsonValueKind.True or JsonValueKind.False)
            || fields["c"].GetString() is not { } code
            || !IsKeyCode(code))
        {
            return Dropped;
        }

        // Only a key going down is capped. A key coming up is exempt, so a key can always be let go.
        var isDown = fields["d"].ValueKind == JsonValueKind.True;
        return new BrowserControlMessage(
            BrowserControlKind.Input,
            FormattableString.Invariant($"{{\"t\":\"key\",\"c\":\"{code}\",\"d\":{(isDown ? "true" : "false")}}}"),
            isDown ? InputRate.Other : InputRate.Exempt,
            isDown ? HeldEffect.Press : HeldEffect.Release,
            $"k{code}");
    }

    // Letters and digits only, so the code can go into the canonical text without escaping. The client holds the allow-list.
    private static bool IsKeyCode(string code) =>
        code.Length is >= 1 and <= MaxKeyCodeLength && code.All(char.IsAsciiLetterOrDigit);

    // Each name appears once. A repeated name is refused, since a reader could take either value.
    private static bool TryReadFields(JsonElement root, out Dictionary<string, JsonElement> fields)
    {
        fields = [];
        foreach (var property in root.EnumerateObject())
        {
            if (!fields.TryAdd(property.Name, property.Value))
            {
                return false;
            }
        }

        return true;
    }

    // Exactly these names and no others.
    private static bool HasExactly(Dictionary<string, JsonElement> fields, params string[] names) =>
        fields.Count == names.Length && names.All(fields.ContainsKey);

    private static bool TryReadInteger(JsonElement value, long min, long max, out long number) =>
        TryReadInteger(value, out number) && number >= min && number <= max;

    // A whole number written with digits only. A decimal point, an exponent or a string is refused.
    private static bool TryReadInteger(JsonElement value, out long number)
    {
        number = 0;
        return value.ValueKind == JsonValueKind.Number
            && IsPlainInteger(value.GetRawText())
            && value.TryGetInt64(out number);
    }

    private static bool IsPlainInteger(string text)
    {
        var digits = text.StartsWith('-') ? text.AsSpan(1) : text.AsSpan();
        if (digits.Length == 0)
        {
            return false;
        }

        foreach (var digit in digits)
        {
            if (!char.IsAsciiDigit(digit))
            {
                return false;
            }
        }

        return true;
    }
}

// What a browser message means once parsed. Only Move and Input are relayed, and only as their canonical text.
public enum BrowserControlKind
{
    Invalid,
    ControlOn,
    ControlOff,
    Move,
    Input,
}

// How often the input may be relayed. Moves, presses and scrolls, and the secure attention sequence have their own caps. Exempt inputs have a backstop.
public enum InputRate
{
    Exempt,
    Move,
    Other,
    Sas,
}

// What an input does to the keys and buttons held on the PC. The server relays an up or release only for something it relayed as down.
public enum HeldEffect
{
    None,
    Press,
    Release,
    ReleaseAll,
}

public readonly record struct BrowserControlMessage(
    BrowserControlKind Kind,
    string Canonical = "",
    InputRate Rate = InputRate.Exempt,
    HeldEffect Held = HeldEffect.None,
    string HeldKey = "");

// Why a control request was refused or ended. These are the reasons the browser is told, and they are part of the wire contract.
public static class ControlReasons
{
    public const string Stopped = "stopped";
    public const string ServerPc = "server-pc";
    public const string Waiting = "waiting";
    public const string Unsupported = "unsupported";
    public const string Connection = "connection";
    public const string PcEnded = "pc-ended";
}
