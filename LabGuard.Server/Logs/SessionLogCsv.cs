using System.Globalization;
using System.Text;
using LabGuard.Server.Data;

namespace LabGuard.Server.Logs;

// Writes the session log as RFC 4180 CSV with a UTF-8 byte order mark, so Excel reads the text as UTF-8.
// PC names and usernames are typed by people, so a cell a spreadsheet could run as a formula gets a leading single quote.
public static class SessionLogCsv
{
    private static readonly string[] Header = ["PC", "User or source", "Start", "End", "Duration (minutes)", "Ended by"];
    private const string TimeFormat = "yyyy-MM-dd HH:mm";
    private const string FileNameTimeFormat = "yyyyMMdd-HHmm";
    private const string FormulaStarters = "=+-@\t\r";
    private static readonly char[] QuotedCharacters = [',', '"', '\r', '\n'];

    public static byte[] Write(IEnumerable<PcSession> sessions, DateTimeOffset now, TimeZoneInfo zone)
    {
        var text = new StringBuilder();
        AppendRow(text, Header);
        foreach (var session in sessions)
        {
            AppendRow(text,
            [
                session.PcName,
                session.Username,
                FormatLocal(session.StartedAtUtc, zone, TimeFormat),
                session.EndedAtUtc is { } endedAt ? FormatLocal(endedAt, zone, TimeFormat) : "",
                SessionLogRules.DurationMinutes(session, now).ToString(CultureInfo.InvariantCulture),
                SessionLogRules.EndedByLabel(session),
            ]);
        }

        return [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(text.ToString())];
    }

    public static string FileNameFor(DateTimeOffset now, TimeZoneInfo zone) =>
        $"session-logs-{FormatLocal(now, zone, FileNameTimeFormat)}.csv";

    // A value that starts like a formula is prefixed with a single quote first. Then a value with a comma, a quote
    // or a line break is quoted, with its inner quotes doubled.
    public static string Field(string value)
    {
        var safe = value.Length > 0 && FormulaStarters.Contains(value[0]) ? "'" + value : value;
        return safe.IndexOfAny(QuotedCharacters) < 0
            ? safe
            : "\"" + safe.Replace("\"", "\"\"") + "\"";
    }

    private static void AppendRow(StringBuilder text, IEnumerable<string> cells) =>
        text.AppendJoin(',', cells.Select(Field)).Append("\r\n");

    private static string FormatLocal(DateTimeOffset instant, TimeZoneInfo zone, string format) =>
        TimeZoneInfo.ConvertTime(instant, zone).ToString(format, CultureInfo.InvariantCulture);
}
