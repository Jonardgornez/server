using LabGuard.Server.Data;
using LabGuard.Server.Logs;
using LabGuard.Server.Sessions;

namespace LabGuard.Server.Tests;

public class SessionLogRulesTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 9, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeZoneInfo PlusTen = TimeZoneInfo.CreateCustomTimeZone("UTC+10", TimeSpan.FromHours(10), "UTC+10", "UTC+10");
    private static readonly TimeZoneInfo MinusFive = TimeZoneInfo.CreateCustomTimeZone("UTC-5", TimeSpan.FromHours(-5), "UTC-5", "UTC-5");

    // Source

    [Fact]
    public void SourceOf_AccountSession_IsAccount()
    {
        Assert.Equal(SessionLogRules.AccountSource, SessionLogRules.SourceOf(new PcSession { Username = "student" }));
    }

    [Fact]
    public void SourceOf_AdminUnlock_IsAdminUnlock()
    {
        Assert.Equal(SessionLogRules.AdminUnlockSource, SessionLogRules.SourceOf(new PcSession { IsAdminUnlock = true }));
    }

    [Fact]
    public void SourceOf_LoginRequestSession_IsLoginRequest()
    {
        Assert.Equal(SessionLogRules.LoginRequestSource, SessionLogRules.SourceOf(new PcSession { LoginRequestId = 7 }));
    }

    // Duration

    [Fact]
    public void DurationMinutes_EndedSession_CountsToTheEnd()
    {
        var session = new PcSession { StartedAtUtc = Start, EndedAtUtc = Start.AddMinutes(90).AddSeconds(30) };

        Assert.Equal(90, SessionLogRules.DurationMinutes(session, Now));
    }

    [Fact]
    public void DurationMinutes_EndedSession_IgnoresTheClock()
    {
        var session = new PcSession { StartedAtUtc = Start, EndedAtUtc = Start.AddMinutes(5) };

        Assert.Equal(5, SessionLogRules.DurationMinutes(session, Start.AddDays(3)));
    }

    [Fact]
    public void DurationMinutes_RunningSession_CountsToNow()
    {
        var session = new PcSession { StartedAtUtc = Start };

        Assert.Equal(120, SessionLogRules.DurationMinutes(session, Now));
    }

    [Fact]
    public void DurationMinutes_RunningSession_RoundsDownToWholeMinutes()
    {
        var session = new PcSession { StartedAtUtc = Start };

        Assert.Equal(0, SessionLogRules.DurationMinutes(session, Start.AddSeconds(59)));
        Assert.Equal(1, SessionLogRules.DurationMinutes(session, Start.AddSeconds(60)));
        Assert.Equal(5, SessionLogRules.DurationMinutes(session, Start.AddMinutes(5).AddSeconds(59)));
    }

    [Fact]
    public void DurationMinutes_RunningPausedSession_CountsToThePause()
    {
        var session = new PcSession { StartedAtUtc = Start, PausedAtUtc = Start.AddMinutes(30) };

        Assert.Equal(30, SessionLogRules.DurationMinutes(session, Now));
    }

    [Fact]
    public void DurationMinutes_ClockEarlierThanStart_IsZero()
    {
        var session = new PcSession { StartedAtUtc = Start };

        Assert.Equal(0, SessionLogRules.DurationMinutes(session, Start.AddMinutes(-10)));
    }

    [Fact]
    public void DurationMinutes_PausedBeforeStart_IsZero()
    {
        var session = new PcSession { StartedAtUtc = Start, PausedAtUtc = Start.AddMinutes(-10) };

        Assert.Equal(0, SessionLogRules.DurationMinutes(session, Now));
    }

    [Fact]
    public void DurationMinutes_EndedBeforeStart_IsZero()
    {
        var session = new PcSession { StartedAtUtc = Start, EndedAtUtc = Start.AddMinutes(-1) };

        Assert.Equal(0, SessionLogRules.DurationMinutes(session, Now));
    }

    // End reason labels

    [Theory]
    [InlineData(SessionLifecycle.Logout, "Logout")]
    [InlineData(SessionLifecycle.TimeExpired, "Time expired")]
    [InlineData(SessionLifecycle.AccountDisabled, "Account disabled")]
    [InlineData(SessionLifecycle.AccountDeleted, "Account removed")]
    [InlineData(SessionLifecycle.Replaced, "Replaced by a new login")]
    [InlineData(SessionLifecycle.PcRemoved, "PC removed")]
    [InlineData(SessionLifecycle.ConnectionLost, "Connection lost")]
    [InlineData(SessionLifecycle.AdminShutdown, "Shutdown")]
    [InlineData(SessionLifecycle.AdminReboot, "Reboot")]
    public void EndReasonLabel_KnownReason_IsReadable(string reason, string expected)
    {
        Assert.Equal(expected, SessionLogRules.EndReasonLabel(reason));
    }

    [Theory]
    [InlineData("something-new")]
    [InlineData("")]
    [InlineData(null)]
    public void EndReasonLabel_UnknownOrMissingReason_IsOther(string? reason)
    {
        Assert.Equal(SessionLogRules.OtherLabel, SessionLogRules.EndReasonLabel(reason));
    }

    [Fact]
    public void EndedByLabel_RunningSession_IsRunning()
    {
        Assert.Equal(SessionLogRules.RunningLabel, SessionLogRules.EndedByLabel(new PcSession { StartedAtUtc = Start }));
    }

    [Fact]
    public void EndedByLabel_EndedSession_ReadsItsReason()
    {
        var session = new PcSession { StartedAtUtc = Start, EndedAtUtc = Start.AddMinutes(5), EndReason = SessionLifecycle.TimeExpired };

        Assert.Equal("Time expired", SessionLogRules.EndedByLabel(session));
    }

    [Fact]
    public void EndedByLabel_EndedSessionWithoutReason_IsOther()
    {
        var session = new PcSession { StartedAtUtc = Start, EndedAtUtc = Start.AddMinutes(5), EndReason = null };

        Assert.Equal(SessionLogRules.OtherLabel, SessionLogRules.EndedByLabel(session));
    }

    // Local day bounds

    [Fact]
    public void LocalDayStartUtc_ZoneAheadOfUtc_IsTheEarlierUtcInstant()
    {
        var start = SessionLogRules.LocalDayStartUtc(new DateOnly(2026, 11, 9), PlusTen);

        Assert.Equal(new DateTimeOffset(2026, 11, 8, 14, 0, 0, TimeSpan.Zero), start);
    }

    [Fact]
    public void LocalDayStartUtc_ZoneBehindUtc_IsTheLaterUtcInstant()
    {
        var start = SessionLogRules.LocalDayStartUtc(new DateOnly(2026, 11, 9), MinusFive);

        Assert.Equal(new DateTimeOffset(2026, 11, 9, 5, 0, 0, TimeSpan.Zero), start);
    }

    [Fact]
    public void LocalDayStartUtc_UtcZone_IsUtcMidnight()
    {
        var start = SessionLogRules.LocalDayStartUtc(new DateOnly(2026, 11, 9), TimeZoneInfo.Utc);

        Assert.Equal(new DateTimeOffset(2026, 11, 9, 0, 0, 0, TimeSpan.Zero), start);
    }

    [Fact]
    public void LocalDayStartUtc_EarliestDate_ClampsToTheMinimum()
    {
        var start = SessionLogRules.LocalDayStartUtc(DateOnly.MinValue, PlusTen);

        Assert.Equal(DateTimeOffset.MinValue, start);
    }

    [Fact]
    public void LocalDayEndUtc_IsTheStartOfTheNextLocalDay()
    {
        var end = SessionLogRules.LocalDayEndUtc(new DateOnly(2026, 11, 9), PlusTen);

        Assert.Equal(new DateTimeOffset(2026, 11, 9, 14, 0, 0, TimeSpan.Zero), end);
    }

    [Fact]
    public void LocalDayEndUtc_LastRepresentableDate_HasNoEnd()
    {
        Assert.Equal(DateTimeOffset.MaxValue, SessionLogRules.LocalDayEndUtc(DateOnly.MaxValue, PlusTen));
    }

    // LIKE pattern

    [Fact]
    public void LikePattern_PlainText_IsWrappedInWildcards()
    {
        Assert.Equal("%lab%", SessionLogRules.LikePattern("lab"));
    }

    [Fact]
    public void LikePattern_WildcardCharacters_AreEscaped()
    {
        Assert.Equal("%50\\%\\_%", SessionLogRules.LikePattern("50%_"));
    }

    [Fact]
    public void LikePattern_EscapeCharacter_IsEscaped()
    {
        Assert.Equal("%a\\\\b%", SessionLogRules.LikePattern("a\\b"));
    }

    // Paging

    [Theory]
    [InlineData(1, 0, 10, 1)]
    [InlineData(5, 0, 10, 1)]
    [InlineData(1, 10, 10, 1)]
    [InlineData(2, 10, 10, 1)]
    [InlineData(2, 11, 10, 2)]
    [InlineData(3, 25, 10, 3)]
    [InlineData(4, 25, 10, 3)]
    [InlineData(long.MaxValue, 25, 10, 3)]
    public void ClampPage_ReturnsTheRequestedPageOrTheLastOne(long requested, int totalItems, int pageSize, int expected)
    {
        Assert.Equal(expected, SessionLogRules.ClampPage(requested, totalItems, pageSize));
    }
}
