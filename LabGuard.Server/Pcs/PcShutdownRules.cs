using LabGuard.Server.Data;

namespace LabGuard.Server.Pcs;

// Shutdown and restart rules shared by the admin routes, the PC list and the heartbeat. The server only stores and
// reports a request; the PC does the shutting down or restarting itself.
public static class PcShutdownRules
{
    public const string ShutdownAction = "shutdown";
    public const string RestartAction = "restart";
    public const string ShuttingDownState = "shutting-down";
    public const string RestartingState = "restarting";
    public const string ShutdownState = "shutdown";
    public const string RebootingState = "rebooting";

    // A request the PC hasn't picked up within this time expires.
    public static readonly TimeSpan RequestWindow = TimeSpan.FromSeconds(60);

    // Each window of a delivery is the countdown it was given plus an allowance, so a long countdown keeps them open as long.
    // The allowances are 30, 90 and 270 seconds: at the default countdown of 30 they are the windows of the first version.
    private const int DeliveryAllowanceSeconds = 30;
    private const int LogoutAllowanceSeconds = 90;
    private const int RebootingAllowanceSeconds = 270;

    // A delivered shutdown or restart counts as in progress for this long, and a heartbeat forgets it after this.
    public static TimeSpan DeliveryWindowOf(int? countdownSeconds) => TimeSpan.FromSeconds(CountdownOf(countdownSeconds) + DeliveryAllowanceSeconds);

    // A logout this soon after a delivery ends the session as an admin shutdown or reboot.
    public static TimeSpan LogoutWindowOf(int? countdownSeconds) => TimeSpan.FromSeconds(CountdownOf(countdownSeconds) + LogoutAllowanceSeconds);

    // A delivered restart the PC hasn't come back from within this time is shown as plain offline.
    public static TimeSpan RebootingWindowOf(int? countdownSeconds) => TimeSpan.FromSeconds(CountdownOf(countdownSeconds) + RebootingAllowanceSeconds);

    // Rows delivered before the countdown was stored have none, and they were given the default.
    private static int CountdownOf(int? storedSeconds) => storedSeconds ?? ServerSettings.DefaultPowerCountdownSeconds;

    // The action a stored request or delivery carries. Rows written before restarts existed have none, and they are shutdowns.
    public static string ActionOf(string? storedAction) => storedAction ?? ShutdownAction;

    // Less than the window old. A time in the future counts as recent, so a clock moved back never drops a request.
    public static bool IsWithin(DateTimeOffset at, DateTimeOffset now, TimeSpan window) => now - at < window;

    // True while a delivery is still held, that is, delivered less than its window ago. Null is never held.
    public static bool IsDeliveryInWindow(DateTimeOffset? deliveredAt, int? countdownSeconds, DateTimeOffset now) =>
        deliveredAt is { } delivered && IsWithin(delivered, now, DeliveryWindowOf(countdownSeconds));

    // Null for a PC with no shutdown or restart in progress or done. In progress: a request not yet picked up, or a
    // delivery while the PC is online. Done: a delivered shutdown once the PC has gone offline, or a delivered restart
    // while it is offline for less than the rebooting window. A PC that just went offline without a command stays null.
    public static string? StateOf(Pc pc, bool isOnline, DateTimeOffset now)
    {
        var isRestart = ActionOf(pc.PowerAction) == RestartAction;
        var isRequestPending = pc.ShutdownRequestedAtUtc is { } requestedAt && IsWithin(requestedAt, now, RequestWindow);
        var isDeliveredRecently = IsDeliveryInWindow(pc.ShutdownDeliveredAtUtc, pc.PowerCountdownSeconds, now);
        if (isRequestPending || (isOnline && isDeliveredRecently))
        {
            return isRestart ? RestartingState : ShuttingDownState;
        }

        if (isOnline || pc.ShutdownDeliveredAtUtc is not { } offlineDeliveredAt)
        {
            return null;
        }

        if (!isRestart)
        {
            return ShutdownState;
        }

        return IsWithin(offlineDeliveredAt, now, RebootingWindowOf(pc.PowerCountdownSeconds)) ? RebootingState : null;
    }
}
