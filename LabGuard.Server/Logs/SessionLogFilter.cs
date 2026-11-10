using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using LabGuard.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace LabGuard.Server.Logs;

// The query string of the session log list and its export, as sent. A missing or blank value is null.
public sealed record SessionLogListQuery(string? From, string? To, string? Pc, string? User, string? Page, string? PageSize);

// The checked filters and paging. From and To are local calendar days, both inclusive; null means no limit.
public sealed record SessionLogFilter(DateOnly? From, DateOnly? To, string? PcName, string? UserName, long Page, int PageSize)
{
    public const int DefaultPageSize = 10;
    public const int MaxPageSize = 100;
    public const int MaxTextLength = 64;

    private const string DateFormat = "yyyy-MM-dd";

    public static bool TryParse(
        SessionLogListQuery raw,
        [NotNullWhen(true)] out SessionLogFilter? filter,
        [NotNullWhen(false)] out string? error)
    {
        filter = null;
        error = null;

        if (!TryReadDay(raw.From, out var from))
        {
            error = $"From must be a date in the form {DateFormat}.";
            return false;
        }

        if (!TryReadDay(raw.To, out var to))
        {
            error = $"To must be a date in the form {DateFormat}.";
            return false;
        }

        if (from is { } firstDay && to is { } lastDay && firstDay > lastDay)
        {
            error = "The From date can't be after the To date.";
            return false;
        }

        var pcName = Trimmed(raw.Pc);
        if (pcName?.Length > MaxTextLength)
        {
            error = $"The PC filter can be at most {MaxTextLength} characters.";
            return false;
        }

        var userName = Trimmed(raw.User);
        if (userName?.Length > MaxTextLength)
        {
            error = $"The user filter can be at most {MaxTextLength} characters.";
            return false;
        }

        if (!TryReadNumber(raw.Page, 1, 1, long.MaxValue, out var page))
        {
            error = "Page must be a whole number, 1 or more.";
            return false;
        }

        if (!TryReadNumber(raw.PageSize, DefaultPageSize, 1, MaxPageSize, out var pageSize))
        {
            error = $"Page size must be a whole number from 1 to {MaxPageSize}.";
            return false;
        }

        filter = new SessionLogFilter(from, to, pcName, userName, page, (int)pageSize);
        return true;
    }

    // Keeps the sessions that started within the filter's local days, and that match the PC and user filters.
    public IQueryable<PcSession> ApplyTo(IQueryable<PcSession> sessions, TimeZoneInfo zone)
    {
        if (From is { } firstDay)
        {
            var startUtc = SessionLogRules.LocalDayStartUtc(firstDay, zone);
            sessions = sessions.Where(s => s.StartedAtUtc >= startUtc);
        }

        if (To is { } lastDay)
        {
            var endUtc = SessionLogRules.LocalDayEndUtc(lastDay, zone);
            sessions = sessions.Where(s => s.StartedAtUtc < endUtc);
        }

        if (PcName is { } pcName)
        {
            var pattern = SessionLogRules.LikePattern(pcName);
            sessions = sessions.Where(s => EF.Functions.Like(s.PcName, pattern, SessionLogRules.LikeEscape));
        }

        if (UserName is { } userName)
        {
            var pattern = SessionLogRules.LikePattern(userName);
            sessions = sessions.Where(s => EF.Functions.Like(s.Username, pattern, SessionLogRules.LikeEscape));
        }

        return sessions;
    }

    // Running sessions first, then the newest start, then the newest id so the order is stable.
    public static IOrderedQueryable<PcSession> NewestFirst(IQueryable<PcSession> sessions) =>
        sessions
            .OrderBy(s => s.EndedAtUtc != null)
            .ThenByDescending(s => s.StartedAtUtc)
            .ThenByDescending(s => s.Id);

    private static string? Trimmed(string? text) =>
        string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    // A blank day is no limit. Anything else must be a real calendar day in the yyyy-MM-dd form.
    private static bool TryReadDay(string? text, out DateOnly? day)
    {
        day = null;
        if (Trimmed(text) is not { } trimmed)
        {
            return true;
        }

        if (!DateOnly.TryParseExact(trimmed, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            return false;
        }

        day = parsed;
        return true;
    }

    // A blank number is the default. Anything else must be a whole number in range; a sign or a decimal point is refused.
    private static bool TryReadNumber(string? text, long defaultValue, long min, long max, out long value)
    {
        if (Trimmed(text) is not { } trimmed)
        {
            value = defaultValue;
            return true;
        }

        return long.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out value)
            && value >= min
            && value <= max;
    }
}
