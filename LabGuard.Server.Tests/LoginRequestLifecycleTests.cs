using LabGuard.Server.Data;
using LabGuard.Server.Requests;

namespace LabGuard.Server.Tests;

public class LoginRequestLifecycleTests
{
    private static readonly DateTimeOffset Requested = new(2026, 11, 9, 10, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(0, 30)]
    [InlineData(10, 20)]
    [InlineData(29.5, 1)]
    [InlineData(29.999, 1)]
    public void CooldownSecondsLeft_IsTheTimeLeftRoundedUp(double secondsElapsed, int expected)
    {
        var now = Requested.AddSeconds(secondsElapsed);

        Assert.Equal(expected, LoginRequestLifecycle.CooldownSecondsLeft(Requested, now));
    }

    [Theory]
    [InlineData(30)]
    [InlineData(45)]
    public void CooldownSecondsLeft_IsNullOnceTheCooldownHasPassed(double secondsElapsed)
    {
        Assert.Null(LoginRequestLifecycle.CooldownSecondsLeft(Requested, Requested.AddSeconds(secondsElapsed)));
    }

    [Fact]
    public void IsDecisionReported_IsTrueUntilTwoMinutesAfterTheDecision()
    {
        Assert.True(LoginRequestLifecycle.IsDecisionReported(Requested, Requested.AddSeconds(119)));
        Assert.False(LoginRequestLifecycle.IsDecisionReported(Requested, Requested.AddMinutes(2)));
        Assert.True(LoginRequestLifecycle.IsDecisionReported(Requested.AddSeconds(5), Requested));
    }

    [Theory]
    [InlineData(LoginRequestStatus.Pending, "pending")]
    [InlineData(LoginRequestStatus.Approved, "approved")]
    [InlineData(LoginRequestStatus.Denied, "denied")]
    [InlineData(LoginRequestStatus.Cancelled, "cancelled")]
    public void WireStatus_IsTheLowercaseStatusName(LoginRequestStatus status, string expected)
    {
        Assert.Equal(expected, LoginRequestLifecycle.WireStatus(status));
    }
}
