using LabGuard.Server.Data;
using LabGuard.Server.Sessions;

namespace LabGuard.Server.Logs;

// What the session log shows and exports: where a session came from, how long it lasted, why it ended,
// and the local day bounds and paging the filters use.
public static class SessionLogRules
{
    public const string AccountSource = "account";
    public const string AdminUnlockSource = "admin-unlock";
    public const string LoginRequestSource = "login-request";

    public const string RunningLabel = "Running";
    public const string OtherLabel = "Other";

    // The escape character used in the LIKE patterns of the PC and user filters.
    public const string LikeEscape = "\\";

    public static string SourceOf(PcSession session) =>
        session.IsAdminUnlock
            ? AdminUnlockSource
            : session.LoginRequestId is not null ? LoginRequestSource : AccountSource;

    // Whole minutes, rounded down, and never negative. An ended session counts to its end. A running session counts to now,
    // or to its pause while it is paused. Time spent paused is not taken out, because only the current pause is stored.
    public static int DurationMinutes(PcSession session, DateTimeOffset now)
    {
        var end = session.EndedAtUtc ?? session.PausedAtUtc ?? now;
        var span = end - session.StartedAtUtc;
        return span <= TimeSpan.Zero
            ? 0
            : (int)Math.Min(int.MaxValue, span.Ticks / TimeSpan.TicksPerMinute);
    }

    public static string EndedByLabel(PcSession session) =>
        session.EndedAtUtc is null ? RunningLabel : EndReasonLabel(session.EndReason);

    // A reason this version doesn't know, or none, reads as Other.
    public static string EndReasonLabel(string? reason) => reason switch
    {
        SessionLifecycle.Logout => "Logout",
        SessionLifecycle.TimeExpired => "Time expired",
        SessionLifecycle.AccountDisabled => "Account disabled",
        SessionLifecycle.AccountDeleted => "Account removed",
        SessionLifecycle.Replaced => "Replaced by a new login",
        SessionLifecycle.PcRemoved => "PC removed",
        SessionLifecycle.ConnectionLost => "Connection lost",
        SessionLifecycle.AdminShutdown => "Shutdown",
        SessionLifecycle.AdminReboot => "Reboot",
        _ => OtherLabel,
    };

    // The first instant of a local calendar day, as UTC. A zone's standard offset applies where the day's midnight is
    // skipped or repeated by a clock change. Clamped to the DateTimeOffset range, so the earliest date can't overflow.
    public static DateTimeOffset LocalDayStartUtc(DateOnly day, TimeZoneInfo zone)
    {
        var midnight = day.ToDateTime(TimeOnly.MinValue);
        var ticks = midnight.Ticks - zone.GetUtcOffset(midnight).Ticks;
        if (ticks < DateTime.MinValue.Ticks)
        {
            return DateTimeOffset.MinValue;
        }

        return ticks > DateTime.MaxValue.Ticks
            ? DateTimeOffset.MaxValue
            : new DateTimeOffset(ticks, TimeSpan.Zero);
    }

    // The first instant after a local calendar day, as UTC. The last representable day has no end.
    public static DateTimeOffset LocalDayEndUtc(DateOnly lastDay, TimeZoneInfo zone) =>
        lastDay == DateOnly.MaxValue
            ? DateTimeOffset.MaxValue
            : LocalDayStartUtc(lastDay.AddDays(1), zone);

    // Matches text anywhere in a value. The LIKE wildcards in the text are escaped, so "50%" finds "50%" and not "500".
    public static string LikePattern(string text)
    {
        var escaped = text
            .Replace(LikeEscape, LikeEscape + LikeEscape)
            .Replace("%", LikeEscape + "%")
            .Replace("_", LikeEscape + "_");
        return $"%{escaped}%";
    }

    // The page to show: the requested one, or the last page when it is beyond the end. A list with nothing in it has one page.
    public static int ClampPage(long requestedPage, int totalItems, int pageSize)
    {
        var lastPage = Math.Max(1L, (totalItems + (long)pageSize - 1) / pageSize);
        return (int)Math.Min(requestedPage, lastPage);
    }
}
