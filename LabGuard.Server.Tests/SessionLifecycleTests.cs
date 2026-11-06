using LabGuard.Server.Data;
using LabGuard.Server.Sessions;
using Microsoft.EntityFrameworkCore;

namespace LabGuard.Server.Tests;

public class SessionLifecycleTests
{
    [Fact]
    public void NewAdminUnlock_HasNoAccountNameExpiryOrTime()
    {
        var pc = new Pc { Id = 7, Name = "PC-07" };
        var now = new DateTimeOffset(2026, 11, 5, 10, 0, 0, TimeSpan.Zero);

        var session = SessionLifecycle.NewAdminUnlock(pc, now);

        Assert.Equal(7, session.PcId);
        Assert.Equal("PC-07", session.PcName);
        Assert.Equal(0, session.UserAccountId);
        Assert.Equal("Admin unlock", session.Username);
        Assert.Equal(now, session.StartedAtUtc);
        Assert.Null(session.ExpiresAtUtc);
        Assert.Null(session.DefaultTimeMinutes);
        Assert.Null(session.EndedAtUtc);
        Assert.Null(session.EndReason);
        Assert.True(session.IsAdminUnlock);
    }

    [Fact]
    public void ToInfo_CarriesTheAdminUnlockFlag()
    {
        var now = new DateTimeOffset(2026, 11, 5, 10, 0, 0, TimeSpan.Zero);
        var unlock = SessionLifecycle.NewAdminUnlock(new Pc { Id = 1, Name = "PC-01" }, now);
        var login = new PcSession { Id = 2, Username = "student", StartedAtUtc = now };

        Assert.True(SessionLifecycle.ToInfo(unlock, now).IsAdminUnlock);
        Assert.False(SessionLifecycle.ToInfo(login, now).IsAdminUnlock);
    }

    [Fact]
    public async Task EndExpiredAsync_ClosesOnlyExpiredSessions_AsTimeExpired()
    {
        using var app = new TestApp();
        var now = app.Time.GetUtcNow();
        var expiredAt = now - TimeSpan.FromMinutes(1);
        await app.WithDbAsync(async db =>
        {
            db.PcSessions.AddRange(
                SessionOn(1, now.AddMinutes(-5), expiresAt: expiredAt),
                SessionOn(2, now.AddMinutes(-5), expiresAt: now.AddMinutes(1)),
                SessionOn(3, now.AddMinutes(-5)),
                SessionOn(4, now.AddMinutes(-5), expiresAt: expiredAt, endedAt: now.AddMinutes(-2)));
            await db.SaveChangesAsync();
            return 0;
        });

        var affected = await app.WithDbAsync(db => SessionLifecycle.EndExpiredAsync(db, now, CancellationToken.None));

        Assert.Equal(1, affected);
        var rows = await app.WithDbAsync(db => db.PcSessions.AsNoTracking().OrderBy(s => s.PcId).ToListAsync());
        Assert.Equal(SessionLifecycle.TimeExpired, rows[0].EndReason);
        Assert.Equal(expiredAt.ToUnixTimeMilliseconds(), rows[0].EndedAtUtc!.Value.ToUnixTimeMilliseconds());
        Assert.Null(rows[1].EndedAtUtc);
        Assert.Null(rows[2].EndedAtUtc);
        Assert.Equal("logout", rows[3].EndReason);
    }

    [Fact]
    public async Task EndAllAsync_EndsEveryActiveSession_AndCountsThem()
    {
        using var app = new TestApp();
        var now = app.Time.GetUtcNow();
        await app.WithDbAsync(async db =>
        {
            db.PcSessions.AddRange(
                SessionOn(1, now.AddMinutes(-5)),
                SessionOn(2, now.AddMinutes(-5), expiresAt: now.AddMinutes(1)),
                SessionOn(3, now.AddMinutes(-5), endedAt: now.AddMinutes(-2)));
            await db.SaveChangesAsync();
            return 0;
        });

        var affected = await app.WithDbAsync(db => SessionLifecycle.EndAllAsync(db, SessionLifecycle.AdminLock, now, CancellationToken.None));

        Assert.Equal(2, affected);
        var rows = await app.WithDbAsync(db => db.PcSessions.AsNoTracking().OrderBy(s => s.PcId).ToListAsync());
        Assert.Equal(SessionLifecycle.AdminLock, rows[0].EndReason);
        Assert.Equal(now.ToUnixTimeMilliseconds(), rows[0].EndedAtUtc!.Value.ToUnixTimeMilliseconds());
        Assert.Equal(SessionLifecycle.AdminLock, rows[1].EndReason);
        Assert.Equal("logout", rows[2].EndReason);
    }

    [Fact]
    public async Task EndSessionAsync_EndsOnlyTheNamedSession()
    {
        using var app = new TestApp();
        var now = app.Time.GetUtcNow();
        var (first, second) = await app.WithDbAsync(async db =>
        {
            var a = SessionOn(1, now.AddMinutes(-5));
            var b = SessionOn(2, now.AddMinutes(-5));
            db.PcSessions.AddRange(a, b);
            await db.SaveChangesAsync();
            return (a.Id, b.Id);
        });

        var affected = await app.WithDbAsync(db => SessionLifecycle.EndSessionAsync(db, first, SessionLifecycle.ConnectionLost, now, CancellationToken.None));

        Assert.Equal(1, affected);
        var rows = await app.WithDbAsync(db => db.PcSessions.AsNoTracking().OrderBy(s => s.Id).ToListAsync());
        Assert.Equal(SessionLifecycle.ConnectionLost, rows[0].EndReason);
        Assert.Equal(now.ToUnixTimeMilliseconds(), rows[0].EndedAtUtc!.Value.ToUnixTimeMilliseconds());
        Assert.Equal(second, rows[1].Id);
        Assert.Null(rows[1].EndedAtUtc);
    }

    [Fact]
    public void HasExpired_IsFalseForAPausedSession_EvenPastItsExpiry()
    {
        var now = new DateTimeOffset(2026, 11, 5, 10, 0, 0, TimeSpan.Zero);
        var expired = new PcSession { ExpiresAtUtc = now.AddMinutes(-1) };
        var paused = new PcSession { ExpiresAtUtc = now.AddMinutes(-1), PausedAtUtc = now.AddMinutes(-5) };

        Assert.True(SessionLifecycle.HasExpired(expired, now));
        Assert.False(SessionLifecycle.HasExpired(paused, now));
    }

    [Fact]
    public void SecondsRemaining_WhilePaused_IsMeasuredFromThePauseMoment()
    {
        var now = new DateTimeOffset(2026, 11, 5, 10, 0, 0, TimeSpan.Zero);
        var session = new PcSession { ExpiresAtUtc = now.AddSeconds(90.5), PausedAtUtc = now };

        Assert.Equal(91, SessionLifecycle.SecondsRemaining(session, now));
        Assert.Equal(91, SessionLifecycle.SecondsRemaining(session, now.AddMinutes(10)));
    }

    [Fact]
    public void SecondsRemaining_WhilePausedPastExpiry_IsZeroAndOpenTimeIsNull()
    {
        var now = new DateTimeOffset(2026, 11, 5, 10, 0, 0, TimeSpan.Zero);
        var pastExpiry = new PcSession { ExpiresAtUtc = now, PausedAtUtc = now.AddSeconds(5) };
        var openTime = new PcSession { PausedAtUtc = now };

        Assert.Equal(0, SessionLifecycle.SecondsRemaining(pastExpiry, now));
        Assert.Null(SessionLifecycle.SecondsRemaining(openTime, now));
    }

    [Fact]
    public async Task EndExpiredAsync_LeavesPausedSessionsRunning()
    {
        using var app = new TestApp();
        var now = app.Time.GetUtcNow();
        await app.WithDbAsync(async db =>
        {
            db.PcSessions.Add(SessionOn(1, now.AddMinutes(-5), expiresAt: now.AddMinutes(-1), pausedAt: now.AddMinutes(-2)));
            await db.SaveChangesAsync();
            return 0;
        });

        var affected = await app.WithDbAsync(db => SessionLifecycle.EndExpiredAsync(db, now, CancellationToken.None));

        Assert.Equal(0, affected);
        Assert.Null((await app.WithDbAsync(db => db.PcSessions.AsNoTracking().SingleAsync())).EndedAtUtc);
    }

    [Fact]
    public async Task EndExpiredForPcAsync_LeavesAPausedSessionRunning()
    {
        using var app = new TestApp();
        var now = app.Time.GetUtcNow();
        await app.WithDbAsync(async db =>
        {
            db.PcSessions.Add(SessionOn(1, now.AddMinutes(-5), expiresAt: now.AddMinutes(-1), pausedAt: now.AddMinutes(-2)));
            await db.SaveChangesAsync();
            return 0;
        });

        var affected = await app.WithDbAsync(db => SessionLifecycle.EndExpiredForPcAsync(db, 1, now, CancellationToken.None));

        Assert.Equal(0, affected);
        Assert.Null((await app.WithDbAsync(db => db.PcSessions.AsNoTracking().SingleAsync())).EndedAtUtc);
    }

    private static PcSession SessionOn(int pcId, DateTimeOffset startedAt, DateTimeOffset? expiresAt = null, DateTimeOffset? endedAt = null, DateTimeOffset? pausedAt = null) => new()
    {
        PcId = pcId,
        PcName = $"PC-{pcId}",
        UserAccountId = 1,
        Username = "student",
        StartedAtUtc = startedAt,
        ExpiresAtUtc = expiresAt,
        EndedAtUtc = endedAt,
        PausedAtUtc = pausedAt,
        EndReason = endedAt is null ? null : "logout",
    };
}
