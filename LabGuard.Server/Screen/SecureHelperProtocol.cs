using System.Net.WebSockets;

namespace LabGuard.Server.Screen;

// The wire rules of the secure-desktop helper: a SYSTEM process the PC's service runs in the console session, which can capture
// and control the protected screens (sign-in, lock, UAC, Ctrl+Alt+Del) that the PC's own stream can't. It connects to the server
// on the PC channel with the PC's token, and idles until a viewer needs it. It is told to start while the PC's stream is paused.
public static class SecureHelperProtocol
{
    // A helper that sends anything else is closed with this code: a binary message that is not a relayable frame, or unknown text.
    public const WebSocketCloseStatus ProtocolErrorCloseStatus = WebSocketCloseStatus.PolicyViolation;

    // Pings go out this often, and a helper that doesn't answer one within the timeout is cut off, so an idle helper whose
    // connection died silently doesn't stay registered.
    public static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan KeepAliveTimeout = TimeSpan.FromSeconds(15);

    public static readonly byte[] StopMessage = "{\"t\":\"stop\"}"u8.ToArray();
    private static readonly byte[] StartNotifiedMessage = "{\"t\":\"start\",\"mode\":\"notified\"}"u8.ToArray();
    private static readonly byte[] StartAnonymousMessage = "{\"t\":\"start\",\"mode\":\"anonymous\"}"u8.ToArray();

    public static ReadOnlyMemory<byte> StartMessage(string mode) => mode switch
    {
        ScreenProtocol.NotifiedMode => StartNotifiedMessage,
        ScreenProtocol.AnonymousMode => StartAnonymousMessage,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), "Unknown view mode."),
    };

    // Strict: the helper's own code sends only these, so anything else is a fault and closes it. Paused and resumed are known,
    // so a helper may reuse the PC's streaming code, but they change nothing.
    public static HelperMessageKind Classify(WebSocketMessageType type, ReadOnlySpan<byte> payload)
    {
        if (type == WebSocketMessageType.Binary)
        {
            return ScreenProtocol.IsRelayableFrame(payload) ? HelperMessageKind.Frame : HelperMessageKind.Invalid;
        }

        return ScreenProtocol.ClassifyPcMessage(type, payload) switch
        {
            PcMessageKind.ControlOn => HelperMessageKind.ControlOn,
            PcMessageKind.ControlOff => HelperMessageKind.ControlOff,
            PcMessageKind.Paused or PcMessageKind.Resumed => HelperMessageKind.Ignored,
            _ => HelperMessageKind.Invalid,
        };
    }
}

public enum HelperMessageKind
{
    Invalid,
    Ignored,
    Frame,
    ControlOn,
    ControlOff,
}

// One helper connection: the PC it serves, the hash of the token it signed in with, so a replaced token closes it, and its socket.
public sealed class SecureHelperConnection(int pcId, string tokenHash, ScreenSocket socket)
{
    public int PcId { get; } = pcId;
    public string TokenHash { get; } = tokenHash;
    public ScreenSocket Socket { get; } = socket;
}
