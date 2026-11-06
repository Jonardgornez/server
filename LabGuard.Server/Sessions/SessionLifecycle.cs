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

    // Ends one session by its id, so a racing login on the same PC can't be ended by mistake.
    public static Task<int> EndSessionAsync(AppDbContext db, int sessionId, string reason, DateTimeOffset now, CancellationToken cancellationToken) =>
        db.PcSessions
            .Where(s => s.Id == sessionId && s.EndedAtUtc == null)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(s => s.EndedAtUtc, now)
                .SetProperty(s => s.EndReason, reason), cancellationToken);

    // Pauses the running session of each listed PC that isn't paused yet, so a second lock keeps the first pause time.
    public static Task<int> PauseForPcsAsync(AppDbContext db, int[] pcIds, DateTimeOffset now, CancellationToken cancellationToken) =>
        db.PcSessions
            .Where(s => pcIds.Contains(s.PcId) && s.EndedAtUtc == null && s.PausedAtUtc == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(s => s.PausedAtUtc, now), cancellationToken);

    // Ends a pause. The expiry moves forward by the time spent paused, so the time left is what it was at the pause.
    public static Task<int> ResumeAsync(AppDbContext db, PcSession session, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var expiresAt = ExpiryAfterResume(session, now);
        return db.PcSessions
            .Where(s => s.Id == session.Id && s.EndedAtUtc == null)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(s => s.ExpiresAtUtc, expiresAt)
                .SetProperty(s => s.PausedAtUtc, (DateTimeOffset?)null), cancellationToken);
    }

    // The expiry once the pause ends: exactly the time that was left when it paused, whatever the clock did meanwhile.
    public static DateTimeOffset? ExpiryAfterResume(PcSession session, DateTimeOffset now)
    {
        if (session.ExpiresAtUtc is not { } expiresAt)
        {
            return null;
        }

        return session.PausedAtUtc is { } pausedAt ? now + (expiresAt - pausedAt) : expiresAt;
    }

    // A paused session never expires: its time stays frozen until it is resumed.
    public static bool HasExpired(PcSession session, DateTimeOffset now) =>
        session.PausedAtUtc is null && session.ExpiresAtUtc is { } expiresAt && expiresAt <= now;

    // The most time a session may have left. The client treats more than this as invalid and ends the session.
    public static readonly TimeSpan MaxTimeLeft = TimeSpan.FromDays(1);

    // Open time gets a limit of exactly the minutes, counted from now or, while paused, from the pause.
    public static DateTimeOffset ExpiryAfterAddingTime(PcSession session, int minutes, DateTimeOffset now) =>
        session.ExpiresAtUtc is { } expiresAt
            ? expiresAt.AddMinutes(minutes)
            : (session.PausedAtUtc ?? now).AddMinutes(minutes);

    // Measured the way SecondsRemaining measures it: from the pause while paused.
    public static bool FitsMaxTimeLeft(PcSession session, int minutes, DateTimeOffset now) =>
        ExpiryAfterAddingTime(session, minutes, now) - (session.PausedAtUtc ?? now) <= MaxTimeLeft;

    // Open time becomes a time limit with the minutes as its default time. A timed session keeps its default time.
    // Any pause is kept, so a paused session shows the added time at once and resumes with it.
    public static Task<int> AddTimeAsync(AppDbContext db, PcSession session, int minutes, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var expiresAt = ExpiryAfterAddingTime(session, minutes, now);
        var defaultTimeMinutes = session.ExpiresAtUtc is null ? minutes : session.DefaultTimeMinutes;
        return db.PcSessions
            .Where(s => s.Id == session.Id && s.EndedAtUtc == null)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(s => s.ExpiresAtUtc, expiresAt)
                .SetProperty(s => s.DefaultTimeMinutes, defaultTimeMinutes), cancellationToken);
    }

    // Makes the session open time: no expiry and no default time. Any pause is kept.
    public static Task<int> SetOpenTimeAsync(AppDbContext db, int sessionId, CancellationToken cancellationToken) =>
        db.PcSessions
            .Where(s => s.Id == sessionId && s.EndedAtUtc == null)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(s => s.ExpiresAtUtc, (DateTimeOffset?)null)
                .SetProperty(s => s.DefaultTimeMinutes, (int?)null), cancellationToken);

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
