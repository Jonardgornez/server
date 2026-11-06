using LabGuard.Server.Data;

namespace LabGuard.Server.Pcs;

// Shutdown rules shared by the admin routes, the PC list and the heartbeat. The server only stores and reports a
// request; the PC does the shutting down itself.
public static class PcShutdownRules
{
    public const int DeliveredShutdownSeconds = 30;
    public const string ShuttingDownState = "shutting-down";
    public const string ShutdownState = "shutdown";

    // A request the PC hasn't picked up within this time expires.
    public static readonly TimeSpan RequestWindow = TimeSpan.FromSeconds(60);
    // A delivered shutdown counts as shutting down for this long, and forgotten by a heartbeat after it.
    public static readonly TimeSpan DeliveryWindow = TimeSpan.FromSeconds(60);
    // A logout this soon after a delivery ends the session as an admin shutdown.
    public static readonly TimeSpan LogoutWindow = TimeSpan.FromMinutes(2);

    // Less than the window old. A time in the future counts as recent, so a clock moved back never drops a request.
    public static bool IsWithin(DateTimeOffset at, DateTimeOffset now, TimeSpan window) => now - at < window;

    // Null for a PC with no shutdown in progress or done. Shutting down: a request not yet picked up, or a delivery
    // while the PC is online. Shut down: a delivery and the PC has since gone offline. A PC that just went offline
    // without a command stays null.
    public static string? StateOf(Pc pc, bool isOnline, DateTimeOffset now)
    {
        var isRequestPending = pc.ShutdownRequestedAtUtc is { } requestedAt && IsWithin(requestedAt, now, RequestWindow);
        var isDeliveredRecently = pc.ShutdownDeliveredAtUtc is { } deliveredAt && IsWithin(deliveredAt, now, DeliveryWindow);
        if (isRequestPending || (isOnline && isDeliveredRecently))
        {
            return ShuttingDownState;
        }

        return !isOnline && pc.ShutdownDeliveredAtUtc is not null ? ShutdownState : null;
    }
}
