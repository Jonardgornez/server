using LabGuard.Server.Screen;

namespace LabGuard.Server.Tests;

public class ScreenInputRateLimiterTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TryTake_AllowsTheCapInASecond_AndRefusesTheNext()
    {
        var limiter = new ScreenInputRateLimiter(3);

        Assert.True(limiter.TryTake(Start));
        Assert.True(limiter.TryTake(Start));
        Assert.True(limiter.TryTake(Start));
        Assert.False(limiter.TryTake(Start));
        Assert.False(limiter.TryTake(Start.AddMilliseconds(999)));
    }

    [Fact]
    public void TryTake_AllowsAgainOnceASecondHasPassed()
    {
        var limiter = new ScreenInputRateLimiter(2);
        limiter.TryTake(Start);
        limiter.TryTake(Start);

        Assert.True(limiter.TryTake(Start.AddSeconds(1)));
        Assert.True(limiter.TryTake(Start.AddSeconds(1)));
        Assert.False(limiter.TryTake(Start.AddSeconds(1)));
    }

    [Fact]
    public void TryTake_SlidesTheWindow_SoTheCapHoldsOverAnySecond()
    {
        var limiter = new ScreenInputRateLimiter(2);
        Assert.True(limiter.TryTake(Start));
        Assert.True(limiter.TryTake(Start.AddMilliseconds(500)));
        Assert.False(limiter.TryTake(Start.AddMilliseconds(900)));

        // The first message has left the window, but the second has not.
        Assert.True(limiter.TryTake(Start.AddMilliseconds(1000)));
        Assert.False(limiter.TryTake(Start.AddMilliseconds(1400)));
        Assert.True(limiter.TryTake(Start.AddMilliseconds(1500)));
    }

    [Fact]
    public void TryTake_RefusedMessages_AreNotKept()
    {
        var limiter = new ScreenInputRateLimiter(1);
        Assert.True(limiter.TryTake(Start));

        for (var attempt = 1; attempt <= 50; attempt++)
        {
            Assert.False(limiter.TryTake(Start.AddMilliseconds(attempt)));
        }

        Assert.True(limiter.TryTake(Start.AddSeconds(1)));
    }

    [Fact]
    public void TryTake_WithAZeroCap_RefusesEverything()
    {
        var limiter = new ScreenInputRateLimiter(0);

        Assert.False(limiter.TryTake(Start));
        Assert.False(limiter.TryTake(Start.AddHours(1)));
    }
}
