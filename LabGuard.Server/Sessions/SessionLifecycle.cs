using LabGuard.Server.Accounts;
using LabGuard.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace LabGuard.Server.Sessions;

public record PcSessionInfo(int SessionId, string Username, DefaultTimeResponse Time, int? SecondsRemaining);

// Shared rules for ending sessions and describing them. Every method works on the active
// session (EndedAtUtc is null) and uses single statements, so callers can combine them in a transaction.
public static class SessionLifecycle
{
    public const string Logout = "logout";
    public const string TimeExpired = "time-expired";
    public const string AccountDisabled = "account-disabled";
    public const string AccountDeleted = "account-deleted";
    public const string Replaced = "replaced";
    public const string PcRemoved = "pc-removed";

    public static Task<int> EndForPcAsync(AppDbContext db, int pcId, string reason, DateTimeOffset now, CancellationToken cancellationToken) =>
        db.PcSessions
            .Where(s => s.PcId == pcId && s.EndedAtUtc == null)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(s => s.EndedAtUtc, now)
                .SetProperty(s => s.EndReason, reason), cancellationToken);

    public static Task<int> EndForAccountAsync(AppDbContext db, int accountId, string reason, DateTimeOffset now, CancellationToken cancellationToken) =>
        db.PcSessions
            .Where(s => s.UserAccountId == accountId && s.EndedAtUtc == null)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(s => s.EndedAtUtc, now)
                .SetProperty(s => s.EndReason, reason), cancellationToken);

    // Closes the PC's session if its time has run out, recording the moment it ran out.
    public static Task<int> EndExpiredForPcAsync(AppDbContext db, int pcId, DateTimeOffset now, CancellationToken cancellationToken) =>
        db.PcSessions
            .Where(s => s.PcId == pcId && s.EndedAtUtc == null && s.ExpiresAtUtc != null && s.ExpiresAtUtc <= now)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(s => s.EndedAtUtc, s => s.ExpiresAtUtc)
                .SetProperty(s => s.EndReason, TimeExpired), cancellationToken);

    public static bool HasExpired(PcSession session, DateTimeOffset now) =>
        session.ExpiresAtUtc is { } expiresAt && expiresAt <= now;

    public static DefaultTimeResponse ToTime(int? minutes) =>
        minutes is { } value
            ? new DefaultTimeResponse(AccountEndpoints.TimeLimitMode, value)
            : new DefaultTimeResponse(AccountEndpoints.OpenTimeMode, null);

    // Rounded up, so the client never shows 0 while there is still time; null for open time.
    public static int? SecondsRemaining(PcSession session, DateTimeOffset now) =>
        session.ExpiresAtUtc is { } expiresAt
            ? (int)Math.Max(0, Math.Ceiling((expiresAt - now).TotalSeconds))
            : null;

    public static PcSessionInfo ToInfo(PcSession session, DateTimeOffset now) =>
        new(session.Id, session.Username, ToTime(session.DefaultTimeMinutes), SecondsRemaining(session, now));
}
