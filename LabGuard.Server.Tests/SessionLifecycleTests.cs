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

    private static PcSession SessionOn(int pcId, DateTimeOffset startedAt, DateTimeOffset? expiresAt = null, DateTimeOffset? endedAt = null) => new()
    {
        PcId = pcId,
        PcName = $"PC-{pcId}",
        UserAccountId = 1,
        Username = "student",
        StartedAtUtc = startedAt,
        ExpiresAtUtc = expiresAt,
        EndedAtUtc = endedAt,
        EndReason = endedAt is null ? null : "logout",
    };
}
