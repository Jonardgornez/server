using LabGuard.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace LabGuard.Server.Logs;

// Sent by the administrator to delete old session logs.
public sealed record SessionLogPurgeRequest(int? OlderThanDays);

public sealed record SessionLogPurgePreviewResponse(int Count);

public sealed record SessionLogPurgeResponse(int Deleted);

// The log category of "Purge old logs", so each purge leaves a line naming who ran it.
public sealed class SessionLogPurgeLog;

// "Purge old logs": deletes ended sessions that started more than the chosen number of days ago. A running session is never deleted.
public static class SessionLogPurge
{
    public static readonly int[] AllowedDays = [30, 90, 180, 365];

    public const string DaysMessage = "Choose 30, 90, 180 or 365 days.";

    public static bool IsAllowed(int? days) => days is { } value && AllowedDays.Contains(value);

    // A session that started exactly the chosen number of days ago is kept.
    public static IQueryable<PcSession> Matching(IQueryable<PcSession> sessions, int olderThanDays, DateTimeOffset now)
    {
        var cutoff = now - TimeSpan.FromDays(olderThanDays);
        return sessions.Where(s => s.EndedAtUtc != null && s.StartedAtUtc < cutoff);
    }
}
