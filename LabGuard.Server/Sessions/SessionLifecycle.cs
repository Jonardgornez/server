using LabGuard.Server.Accounts;
using LabGuard.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace LabGuard.Server.Sessions;

public record PcSessionInfo(int SessionId, string Username, DefaultTimeResponse Time, int? SecondsRemaining, bool IsAdminUnlock);

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
    public const string AdminLock = "admin-lock";
    public const string ConnectionLost = "connection-lost";

    public const string AdminUnlockUsername = "Admin unlock";
    // Real account ids start at 1, so no account matches this and EndForAccountAsync never ends an admin unlock.
    public const int AdminUnlockAccountId = 0;

    public static PcSession NewAdminUnlock(Pc pc, DateTimeOffset now) => new()
    {
        PcId = pc.Id,
        PcName = pc.Name,
        UserAccountId = AdminUnlockAccountId,
        Username = AdminUnlockUsername,
        StartedAtUtc = now,
        ExpiresAtUtc = null,
        DefaultTimeMinutes = null,
        IsAdminUnlock = true,
    };

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

    // Closes the PC's session if its time has run out, recording the moment it ran out. A paused session is never closed.
    public static Task<int> EndExpiredForPcAsync(AppDbContext db, int pcId, DateTimeOffset now, CancellationToken cancellationToken) =>
        db.PcSessions
            .Where(s => s.PcId == pcId && s.EndedAtUtc == null && s.PausedAtUtc == null && s.ExpiresAtUtc != null && s.ExpiresAtUtc <= now)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(s => s.EndedAtUtc, s => s.ExpiresAtUtc)
                .SetProperty(s => s.EndReason, TimeExpired), cancellationToken);

    // Closes every PC's expired session, recording when each one ran out. Paused sessions are left alone.
    public static Task<int> EndExpiredAsync(AppDbContext db, DateTimeOffset now, CancellationToken cancellationToken) =>
        db.PcSessions
            .Where(s => s.EndedAtUtc == null && s.PausedAtUtc == null && s.ExpiresAtUtc != null && s.ExpiresAtUtc <= now)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(s => s.EndedAtUtc, s => s.ExpiresAtUtc)
                .SetProperty(s => s.EndReason, TimeExpired), cancellationToken);

    // Ends every active session on every PC in one statement.
    public static Task<int> EndAllAsync(AppDbContext db, string reason, DateTimeOffset now, CancellationToken cancellationToken) =>
        db.PcSessions
            .Where(s => s.EndedAtUtc == null)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(s => s.EndedAtUtc, now)
                .SetProperty(s => s.EndReason, reason), cancellationToken);

    // Ends one session by its id, so a racing login on the same PC can't be ended by mistake.
    public static Task<int> EndSessionAsync(AppDbContext db, int sessionId, string reason, DateTimeOffset now, CancellationToken cancellationToken) =>
        db.PcSessions
            .Where(s => s.Id == sessionId && s.EndedAtUtc == null)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(s => s.EndedAtUtc, now)
                .SetProperty(s => s.EndReason, reason), cancellationToken);

    // A paused session never expires: its time stays frozen until it is resumed.
    public static bool HasExpired(PcSession session, DateTimeOffset now) =>
        session.PausedAtUtc is null && session.ExpiresAtUtc is { } expiresAt && expiresAt <= now;

    public static DefaultTimeResponse ToTime(int? minutes) =>
        minutes is { } value
            ? new DefaultTimeResponse(AccountEndpoints.TimeLimitMode, value)
            : new DefaultTimeResponse(AccountEndpoints.OpenTimeMode, null);

    // Rounded up, so the client never shows 0 while there is still time; null for open time.
    // A paused session is measured at the moment it was paused, so its count stays the same.
    public static int? SecondsRemaining(PcSession session, DateTimeOffset now) =>
        session.ExpiresAtUtc is { } expiresAt
            ? (int)Math.Max(0, Math.Ceiling((expiresAt - (session.PausedAtUtc ?? now)).TotalSeconds))
            : null;

    public static PcSessionInfo ToInfo(PcSession session, DateTimeOffset now) =>
        new(session.Id, session.Username, ToTime(session.DefaultTimeMinutes), SecondsRemaining(session, now), session.IsAdminUnlock);
}
