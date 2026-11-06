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

    [Fact]
    public void ExpiryAfterResume_MovesTheExpiryByTheTimeSpentPaused()
    {
        var now = new DateTimeOffset(2026, 11, 5, 10, 0, 0, TimeSpan.Zero);
        var session = new PcSession { ExpiresAtUtc = now.AddMinutes(30), PausedAtUtc = now.AddMinutes(-20) };

        Assert.Equal(now.AddMinutes(50), SessionLifecycle.ExpiryAfterResume(session, now));
    }

    [Fact]
    public void ExpiryAfterResume_GivesExactlyTheTimeLeft_WhenTheClockMovedBack()
    {
        var now = new DateTimeOffset(2026, 11, 5, 10, 0, 0, TimeSpan.Zero);
        var session = new PcSession { ExpiresAtUtc = now.AddMinutes(30), PausedAtUtc = now.AddMinutes(5) };

        Assert.Equal(now.AddMinutes(25), SessionLifecycle.ExpiryAfterResume(session, now));
    }

    [Fact]
    public void ExpiryAfterResume_KeepsOpenTime_AndAnUnpausedExpiry()
    {
        var now = new DateTimeOffset(2026, 11, 5, 10, 0, 0, TimeSpan.Zero);

        Assert.Null(SessionLifecycle.ExpiryAfterResume(new PcSession { PausedAtUtc = now.AddMinutes(-1) }, now));
        Assert.Equal(now.AddMinutes(30), SessionLifecycle.ExpiryAfterResume(new PcSession { ExpiresAtUtc = now.AddMinutes(30) }, now));
    }

    [Fact]
    public async Task PauseForPcsAsync_PausesOnlyRunningSessionsOfTheListedPcs_AndKeepsAnExistingPause()
    {
        using var app = new TestApp();
        var now = app.Time.GetUtcNow();
        var earlierPause = now.AddMinutes(-1);
        await app.WithDbAsync(async db =>
        {
            db.PcSessions.AddRange(
                SessionOn(1, now.AddMinutes(-5)),
                SessionOn(2, now.AddMinutes(-5), pausedAt: earlierPause),
                SessionOn(3, now.AddMinutes(-5)),
                SessionOn(4, now.AddMinutes(-5), endedAt: now.AddMinutes(-2)));
            await db.SaveChangesAsync();
            return 0;
        });

        var affected = await app.WithDbAsync(db => SessionLifecycle.PauseForPcsAsync(db, [1, 2, 4], now, CancellationToken.None));

        Assert.Equal(1, affected);
        var rows = await app.WithDbAsync(db => db.PcSessions.AsNoTracking().OrderBy(s => s.PcId).ToListAsync());
        Assert.Equal(now.ToUnixTimeMilliseconds(), rows[0].PausedAtUtc!.Value.ToUnixTimeMilliseconds());
        Assert.Equal(earlierPause.ToUnixTimeMilliseconds(), rows[1].PausedAtUtc!.Value.ToUnixTimeMilliseconds());
        Assert.Null(rows[2].PausedAtUtc);
        Assert.Null(rows[3].PausedAtUtc);
    }

    [Fact]
    public async Task ResumeAsync_MovesTheExpiryForward_AndClearsThePause()
    {
        using var app = new TestApp();
        var now = app.Time.GetUtcNow();
        await app.WithDbAsync(async db =>
        {
            db.PcSessions.Add(SessionOn(1, now.AddMinutes(-10), expiresAt: now.AddMinutes(50), pausedAt: now.AddMinutes(-20)));
            await db.SaveChangesAsync();
            return 0;
        });
        var session = await app.WithDbAsync(db => db.PcSessions.AsNoTracking().SingleAsync());

        var affected = await app.WithDbAsync(db => SessionLifecycle.ResumeAsync(db, session, now, CancellationToken.None));

        Assert.Equal(1, affected);
        var stored = await app.WithDbAsync(db => db.PcSessions.AsNoTracking().SingleAsync());
        Assert.Null(stored.PausedAtUtc);
        Assert.Equal(now.AddMinutes(70).ToUnixTimeMilliseconds(), stored.ExpiresAtUtc!.Value.ToUnixTimeMilliseconds());
    }

    [Fact]
    public void ExpiryAfterAddingTime_MovesATimedExpiryLater()
    {
        var now = new DateTimeOffset(2026, 11, 5, 10, 0, 0, TimeSpan.Zero);
        var session = new PcSession { ExpiresAtUtc = now.AddMinutes(30) };

        Assert.Equal(now.AddMinutes(45), SessionLifecycle.ExpiryAfterAddingTime(session, 15, now));
    }

    [Fact]
    public void ExpiryAfterAddingTime_GivesOpenTimeThatManyMinutesFromNow()
    {
        var now = new DateTimeOffset(2026, 11, 5, 10, 0, 0, TimeSpan.Zero);

        Assert.Equal(now.AddMinutes(15), SessionLifecycle.ExpiryAfterAddingTime(new PcSession(), 15, now));
    }

    [Fact]
    public void ExpiryAfterAddingTime_GivesPausedOpenTimeThatManyMinutesFromThePause()
    {
        var now = new DateTimeOffset(2026, 11, 5, 10, 0, 0, TimeSpan.Zero);
        var session = new PcSession { PausedAtUtc = now.AddMinutes(-20) };

        var expiresAt = SessionLifecycle.ExpiryAfterAddingTime(session, 15, now);

        Assert.Equal(now.AddMinutes(-5), expiresAt);
        Assert.Equal(TimeSpan.FromMinutes(15), expiresAt - session.PausedAtUtc);
    }

    [Fact]
    public void FitsMaxTimeLeft_AllowsExactly1440MinutesLeft_AndRejectsOneSecondMore()
    {
        var now = new DateTimeOffset(2026, 11, 5, 10, 0, 0, TimeSpan.Zero);
        var atLimit = new PcSession { ExpiresAtUtc = now.AddMinutes(1439) };
        var oneSecondOver = new PcSession { ExpiresAtUtc = now.AddMinutes(1439).AddSeconds(1) };

        Assert.True(SessionLifecycle.FitsMaxTimeLeft(atLimit, 1, now));
        Assert.False(SessionLifecycle.FitsMaxTimeLeft(oneSecondOver, 1, now));
    }

    [Fact]
    public void FitsMaxTimeLeft_WhilePaused_MeasuresFromThePauseMoment()
    {
        var now = new DateTimeOffset(2026, 11, 5, 10, 0, 0, TimeSpan.Zero);
        var paused = new PcSession { ExpiresAtUtc = now.AddMinutes(1439), PausedAtUtc = now };

        Assert.True(SessionLifecycle.FitsMaxTimeLeft(paused, 1, now.AddHours(5)));
        Assert.False(SessionLifecycle.FitsMaxTimeLeft(paused, 2, now.AddHours(5)));
    }

    [Fact]
    public void FitsMaxTimeLeft_OpenTimeWithTheLargestAllowedMinutes_Fits()
    {
        var now = new DateTimeOffset(2026, 11, 5, 10, 0, 0, TimeSpan.Zero);

        Assert.True(SessionLifecycle.FitsMaxTimeLeft(new PcSession(), 1440, now));
    }

    [Fact]
    public async Task AddTimeAsync_ExtendsATimedSession_AndKeepsItsDefaultAndPause()
    {
        using var app = new TestApp();
        var now = app.Time.GetUtcNow();
        var timed = SessionOn(1, now.AddMinutes(-5), expiresAt: now.AddMinutes(10), pausedAt: now.AddMinutes(-1));
        timed.DefaultTimeMinutes = 15;
        await app.WithDbAsync(async db =>
        {
            db.PcSessions.Add(timed);
            await db.SaveChangesAsync();
            return 0;
        });
        var session = await app.WithDbAsync(db => db.PcSessions.AsNoTracking().SingleAsync());

        var affected = await app.WithDbAsync(db => SessionLifecycle.AddTimeAsync(db, session, 20, now, CancellationToken.None));

        Assert.Equal(1, affected);
        var stored = await app.WithDbAsync(db => db.PcSessions.AsNoTracking().SingleAsync());
        Assert.Equal(now.AddMinutes(30).ToUnixTimeMilliseconds(), stored.ExpiresAtUtc!.Value.ToUnixTimeMilliseconds());
        Assert.Equal(15, stored.DefaultTimeMinutes);
        Assert.Equal(now.AddMinutes(-1).ToUnixTimeMilliseconds(), stored.PausedAtUtc!.Value.ToUnixTimeMilliseconds());
    }

    [Fact]
    public async Task AddTimeAsync_TurnsOpenTimeIntoATimeLimit_CountedFromNow()
    {
        using var app = new TestApp();
        var now = app.Time.GetUtcNow();
        await app.WithDbAsync(async db =>
        {
            db.PcSessions.Add(SessionOn(1, now.AddMinutes(-5)));
            await db.SaveChangesAsync();
            return 0;
        });
        var session = await app.WithDbAsync(db => db.PcSessions.AsNoTracking().SingleAsync());

        await app.WithDbAsync(db => SessionLifecycle.AddTimeAsync(db, session, 20, now, CancellationToken.None));

        var stored = await app.WithDbAsync(db => db.PcSessions.AsNoTracking().SingleAsync());
        Assert.Equal(now.AddMinutes(20).ToUnixTimeMilliseconds(), stored.ExpiresAtUtc!.Value.ToUnixTimeMilliseconds());
        Assert.Equal(20, stored.DefaultTimeMinutes);
    }

    [Fact]
    public async Task SetOpenTimeAsync_ClearsTheExpiryAndDefault_KeepsThePause_AndSkipsEndedSessions()
    {
        using var app = new TestApp();
        var now = app.Time.GetUtcNow();
        var (activeId, endedId) = await app.WithDbAsync(async db =>
        {
            var active = SessionOn(1, now.AddMinutes(-5), expiresAt: now.AddMinutes(10), pausedAt: now.AddMinutes(-1));
            active.DefaultTimeMinutes = 15;
            var ended = SessionOn(2, now.AddMinutes(-5), expiresAt: now.AddMinutes(10), endedAt: now.AddMinutes(-2));
            ended.DefaultTimeMinutes = 15;
            db.PcSessions.AddRange(active, ended);
            await db.SaveChangesAsync();
            return (active.Id, ended.Id);
        });

        var affected = await app.WithDbAsync(db => SessionLifecycle.SetOpenTimeAsync(db, activeId, CancellationToken.None));
        var skipped = await app.WithDbAsync(db => SessionLifecycle.SetOpenTimeAsync(db, endedId, CancellationToken.None));

        Assert.Equal(1, affected);
        Assert.Equal(0, skipped);
        var active2 = await app.WithDbAsync(db => db.PcSessions.AsNoTracking().SingleAsync(s => s.Id == activeId));
        Assert.Null(active2.ExpiresAtUtc);
        Assert.Null(active2.DefaultTimeMinutes);
        Assert.Equal(now.AddMinutes(-1).ToUnixTimeMilliseconds(), active2.PausedAtUtc!.Value.ToUnixTimeMilliseconds());
        var ended2 = await app.WithDbAsync(db => db.PcSessions.AsNoTracking().SingleAsync(s => s.Id == endedId));
        Assert.Equal(15, ended2.DefaultTimeMinutes);
        Assert.NotNull(ended2.ExpiresAtUtc);
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
