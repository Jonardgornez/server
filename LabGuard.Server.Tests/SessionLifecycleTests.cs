using LabGuard.Server.Data;
using LabGuard.Server.Sessions;

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
}
