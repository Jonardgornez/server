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

    public const string WaitingState = "waiting";
    public const string LiveState = "live";
    public const string PausedState = "paused";

    // The PC may have this many frames unacknowledged. Anything beyond that is dropped, never queued.
    public const int MaxFramesInFlight = 2;

    // Per message type. A PC frame is at most 2 MiB, and the PC sends nothing as text but "paused" or "resumed".
    public const int MaxPcBinaryBytes = 2 * 1024 * 1024;
    public const int MaxPcTextBytes = 16;
    // The browser only sends "ack", so anything larger is not a legitimate message.
    public const int MaxBrowserMessageBytes = 256;
    public static readonly MessageLimits PcLimits = new(MaxPcTextBytes, MaxPcBinaryBytes);
    public static readonly MessageLimits BrowserLimits = new(MaxBrowserMessageBytes, MaxBrowserMessageBytes);

    // Frame size limits. Checked from the JPEG header before a frame is relayed.
    public const int MaxFrameSide = 8192;
    public const long MaxFramePixels = 16_000_000;

    public static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(2);
    // A message that has started must finish within this time. A partial message that goes silent is aborted.
    public static readonly TimeSpan MessageStallTimeout = TimeSpan.FromSeconds(5);
    // A PC may reconnect at most once per window. The PC client backs off at least this long.
    public static readonly TimeSpan AttachCooldown = TimeSpan.FromSeconds(1);
    // Keep-alive pings keep idle sockets alive through proxies. Idle is not a fault: a still screen sends nothing.
    // A PC that stops heartbeating is closed by the monitor, so liveness comes from the heartbeat.
    public static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(30);

    private static readonly byte[] WaitingMessage = "{\"state\":\"waiting\"}"u8.ToArray();
    private static readonly byte[] LiveMessage = "{\"state\":\"live\"}"u8.ToArray();
    private static readonly byte[] PausedMessage = "{\"state\":\"paused\"}"u8.ToArray();
    public static readonly byte[] AckMessage = "ack"u8.ToArray();

    // Null when the mode isn't one the view accepts. A missing mode means notified.
    public static string? ParseMode(string? text) => text switch
    {
        null => NotifiedMode,
        NotifiedMode => NotifiedMode,
        AnonymousMode => AnonymousMode,
        _ => null,
    };

    public static ReadOnlyMemory<byte> StateMessage(string state) => state switch
    {
        WaitingState => WaitingMessage,
        LiveState => LiveMessage,
        PausedState => PausedMessage,
        _ => throw new ArgumentOutOfRangeException(nameof(state), "Unknown view state."),
    };

    // A frame is relayed only when it starts with a JPEG marker and its header gives a size the browser can show.
    public static bool IsRelayableFrame(ReadOnlySpan<byte> frame) =>
        JpegSize.TryRead(frame, out var width, out var height)
        && width <= MaxFrameSide
        && height <= MaxFrameSide
        && (long)width * height <= MaxFramePixels;

    public static PcMessageKind ClassifyPcMessage(WebSocketMessageType type, ReadOnlySpan<byte> payload)
    {
        if (type == WebSocketMessageType.Binary)
        {
            return IsRelayableFrame(payload) ? PcMessageKind.Frame : PcMessageKind.Ignored;
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

public readonly record struct MessageLimits(int Text, int Binary);

public enum PcMessageKind
{
    Ignored,
    Frame,
    Paused,
    Resumed,
}
