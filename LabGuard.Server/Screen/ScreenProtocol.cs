using System.Net.WebSockets;

namespace LabGuard.Server.Screen;

// The wire rules of the live screen view, shared by the administrator route and the PC route.
// The server relays JPEG frames from the PC to the browser. Nothing here stores or logs a frame.
public static class ScreenProtocol
{
    public const string NotifiedMode = "notified";
    public const string AnonymousMode = "anonymous";

    // Custom close codes. 4001: a newer viewer or PC connection took this one's place. 4002: the PC went offline or was removed.
    // 4003: the administrator's session ended.
    public const WebSocketCloseStatus ReplacedCloseStatus = (WebSocketCloseStatus)4001;
    public const WebSocketCloseStatus PcOfflineCloseStatus = (WebSocketCloseStatus)4002;
    public const WebSocketCloseStatus SessionEndedCloseStatus = (WebSocketCloseStatus)4003;
    public const string SessionEndedReason = "The administrator session ended.";

    // The PC may have this many frames unacknowledged. Anything beyond that is dropped, never queued.
    public const int MaxFramesInFlight = 2;
    public const int MaxPcMessageBytes = 4 * 1024 * 1024;
    // The browser only sends "ack", so anything larger is not a legitimate message.
    public const int MaxBrowserMessageBytes = 256;
    public static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(2);

    private static readonly string[] LoopbackHosts = ["localhost", "127.0.0.1", "[::1]"];

    public static readonly byte[] WaitingMessage = "{\"state\":\"waiting\"}"u8.ToArray();
    public static readonly byte[] LiveMessage = "{\"state\":\"live\"}"u8.ToArray();
    public static readonly byte[] PausedMessage = "{\"state\":\"paused\"}"u8.ToArray();
    public static readonly byte[] AckMessage = "ack"u8.ToArray();

    // Null when the mode isn't one the view accepts. A missing mode means notified.
    public static string? ParseMode(string? text) => text switch
    {
        null => NotifiedMode,
        NotifiedMode => NotifiedMode,
        AnonymousMode => AnonymousMode,
        _ => null,
    };

    // The host decides, not the port, so the Vite dev server (localhost:5173) works. Origin is required.
    public static bool IsLoopbackOrigin(string? origin)
    {
        if (string.IsNullOrEmpty(origin) || !Uri.TryCreate(origin, UriKind.Absolute, out var uri))
        {
            return false;
        }

        return uri.Scheme is "http" or "https"
            && LoopbackHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase);
    }

    public static bool IsJpeg(ReadOnlySpan<byte> frame) =>
        frame.Length >= 3 && frame[0] == 0xFF && frame[1] == 0xD8 && frame[2] == 0xFF;

    public static PcMessageKind ClassifyPcMessage(WebSocketMessageType type, ReadOnlySpan<byte> payload)
    {
        if (type == WebSocketMessageType.Binary)
        {
            return IsJpeg(payload) ? PcMessageKind.Frame : PcMessageKind.Ignored;
        }

        if (type == WebSocketMessageType.Text)
        {
            if (payload.SequenceEqual("paused"u8))
            {
                return PcMessageKind.Paused;
            }

            if (payload.SequenceEqual("resumed"u8))
            {
                return PcMessageKind.Resumed;
            }
        }

        return PcMessageKind.Ignored;
    }

    public static bool IsBrowserAck(WebSocketMessageType type, ReadOnlySpan<byte> payload) =>
        type == WebSocketMessageType.Text && payload.SequenceEqual("ack"u8);
}

public enum PcMessageKind
{
    Ignored,
    Frame,
    Paused,
    Resumed,
}
