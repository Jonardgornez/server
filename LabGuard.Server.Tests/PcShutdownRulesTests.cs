using LabGuard.Server.Data;
using LabGuard.Server.Pcs;

namespace LabGuard.Server.Tests;

public class PcShutdownRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 6, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void StateOf_NoCommand_IsNullOnlineAndOffline()
    {
        var pc = new Pc { Name = "PC-01" };

        Assert.Null(PcShutdownRules.StateOf(pc, isOnline: true, Now));
        Assert.Null(PcShutdownRules.StateOf(pc, isOnline: false, Now));
    }

    [Fact]
    public void StateOf_RequestWithinTheWindow_IsShuttingDown_EvenWhenOffline()
    {
        var pc = new Pc { Name = "PC-01", ShutdownRequestedAtUtc = Now.AddSeconds(-59) };

        Assert.Equal(PcShutdownRules.ShuttingDownState, PcShutdownRules.StateOf(pc, isOnline: false, Now));
    }

    [Fact]
    public void StateOf_RequestAtTheWindowEdge_IsNotPending()
    {
        var pc = new Pc { Name = "PC-01", ShutdownRequestedAtUtc = Now.AddSeconds(-60) };

        Assert.Null(PcShutdownRules.StateOf(pc, isOnline: false, Now));
    }

    [Fact]
    public void StateOf_DeliveredWhileOnlineWithinTheWindow_IsShuttingDown()
    {
        var pc = new Pc { Name = "PC-01", ShutdownDeliveredAtUtc = Now.AddSeconds(-10) };

        Assert.Equal(PcShutdownRules.ShuttingDownState, PcShutdownRules.StateOf(pc, isOnline: true, Now));
    }

    [Fact]
    public void StateOf_DeliveredAndOffline_IsShutdown_WhateverTheAge()
    {
        var pc = new Pc { Name = "PC-01", ShutdownDeliveredAtUtc = Now.AddHours(-3) };

        Assert.Equal(PcShutdownRules.ShutdownState, PcShutdownRules.StateOf(pc, isOnline: false, Now));
    }

    [Fact]
    public void StateOf_DeliveredLongAgoWhileOnline_IsNull()
    {
        var pc = new Pc { Name = "PC-01", ShutdownDeliveredAtUtc = Now.AddMinutes(-5) };

        Assert.Null(PcShutdownRules.StateOf(pc, isOnline: true, Now));
    }

    [Fact]
    public void ActionOf_NoStoredAction_IsShutdown()
    {
        Assert.Equal(PcShutdownRules.ShutdownAction, PcShutdownRules.ActionOf(null));
        Assert.Equal(PcShutdownRules.RestartAction, PcShutdownRules.ActionOf(PcShutdownRules.RestartAction));
    }

    [Fact]
    public void StateOf_RestartRequestPending_IsRestarting_EvenWhenOffline()
    {
        var pc = new Pc { Name = "PC-01", ShutdownRequestedAtUtc = Now.AddSeconds(-59), PowerAction = PcShutdownRules.RestartAction };

        Assert.Equal(PcShutdownRules.RestartingState, PcShutdownRules.StateOf(pc, isOnline: false, Now));
        Assert.Equal(PcShutdownRules.RestartingState, PcShutdownRules.StateOf(pc, isOnline: true, Now));
    }

    [Fact]
    public void StateOf_RestartDeliveredWhileOnlineWithinTheWindow_IsRestarting()
    {
        var pc = new Pc { Name = "PC-01", ShutdownDeliveredAtUtc = Now.AddSeconds(-10), PowerAction = PcShutdownRules.RestartAction };

        Assert.Equal(PcShutdownRules.RestartingState, PcShutdownRules.StateOf(pc, isOnline: true, Now));
    }

    [Fact]
    public void StateOf_RestartDeliveredAndOfflineWithinFiveMinutes_IsRebooting()
    {
        var pc = new Pc { Name = "PC-01", ShutdownDeliveredAtUtc = Now.AddMinutes(-4).AddSeconds(-59), PowerAction = PcShutdownRules.RestartAction };

        Assert.Equal(PcShutdownRules.RebootingState, PcShutdownRules.StateOf(pc, isOnline: false, Now));
    }

    [Fact]
    public void StateOf_RestartDeliveredAndOfflineAtTheRebootingEdge_IsNull()
    {
        var pc = new Pc { Name = "PC-01", ShutdownDeliveredAtUtc = Now.AddMinutes(-5), PowerAction = PcShutdownRules.RestartAction };

        Assert.Null(PcShutdownRules.StateOf(pc, isOnline: false, Now));
    }

    [Fact]
    public void StateOf_RestartDeliveredLongAgoWhileOnline_IsNull()
    {
        var pc = new Pc { Name = "PC-01", ShutdownDeliveredAtUtc = Now.AddMinutes(-5), PowerAction = PcShutdownRules.RestartAction };

        Assert.Null(PcShutdownRules.StateOf(pc, isOnline: true, Now));
    }

    [Fact]
    public void StateOf_ShutdownDeliveredAndOffline_StaysShutdownPastTheRebootingWindow()
    {
        var pc = new Pc { Name = "PC-01", ShutdownDeliveredAtUtc = Now.AddMinutes(-30), PowerAction = PcShutdownRules.ShutdownAction };

        Assert.Equal(PcShutdownRules.ShutdownState, PcShutdownRules.StateOf(pc, isOnline: false, Now));
    }

    [Fact]
    public void StateOf_RowWithNoStoredAction_CountsAsShutdown()
    {
        var pc = new Pc { Name = "PC-01", ShutdownDeliveredAtUtc = Now.AddMinutes(-1) };

        Assert.Equal(PcShutdownRules.ShutdownState, PcShutdownRules.StateOf(pc, isOnline: false, Now));
    }

    // The windows follow the countdown stored with the delivery: 30 seconds gives the first version's windows, 600 a longer one.
    // A null countdown is a row delivered before it was stored, so it uses the default of 30.

    [Theory]
    [InlineData(30, 59, true)]
    [InlineData(30, 60, false)]
    [InlineData(600, 629, true)]
    [InlineData(600, 630, false)]
    [InlineData(null, 59, true)]
    [InlineData(null, 60, false)]
    public void StateOf_DeliveryWindowFollowsTheStoredCountdown(int? countdown, int secondsSinceDelivery, bool inWindow)
    {
        var pc = new Pc { Name = "PC-01", ShutdownDeliveredAtUtc = Now.AddSeconds(-secondsSinceDelivery), PowerCountdownSeconds = countdown };

        Assert.Equal(inWindow ? PcShutdownRules.ShuttingDownState : null, PcShutdownRules.StateOf(pc, isOnline: true, Now));
    }

    [Theory]
    [InlineData(30, 299, true)]
    [InlineData(30, 300, false)]
    [InlineData(600, 869, true)]
    [InlineData(600, 870, false)]
    [InlineData(null, 299, true)]
    [InlineData(null, 300, false)]
    public void StateOf_RebootingWindowFollowsTheStoredCountdown(int? countdown, int secondsSinceDelivery, bool inWindow)
    {
        var pc = new Pc
        {
            Name = "PC-01",
            ShutdownDeliveredAtUtc = Now.AddSeconds(-secondsSinceDelivery),
            PowerAction = PcShutdownRules.RestartAction,
            PowerCountdownSeconds = countdown,
        };

        Assert.Equal(inWindow ? PcShutdownRules.RebootingState : null, PcShutdownRules.StateOf(pc, isOnline: false, Now));
    }

    [Theory]
    [InlineData(null, 60)]
    [InlineData(30, 60)]
    [InlineData(600, 630)]
    public void DeliveryWindowOf_IsTheCountdownPlus30Seconds(int? countdown, int seconds) =>
        Assert.Equal(TimeSpan.FromSeconds(seconds), PcShutdownRules.DeliveryWindowOf(countdown));

    [Theory]
    [InlineData(null, 120)]
    [InlineData(30, 120)]
    [InlineData(600, 690)]
    public void LogoutWindowOf_IsTheCountdownPlus90Seconds(int? countdown, int seconds) =>
        Assert.Equal(TimeSpan.FromSeconds(seconds), PcShutdownRules.LogoutWindowOf(countdown));

    [Theory]
    [InlineData(null, 300)]
    [InlineData(30, 300)]
    [InlineData(600, 870)]
    public void RebootingWindowOf_IsTheCountdownPlus270Seconds(int? countdown, int seconds) =>
        Assert.Equal(TimeSpan.FromSeconds(seconds), PcShutdownRules.RebootingWindowOf(countdown));

    [Fact]
    public void IsDeliveryInWindow_NoDelivery_IsFalse()
    {
        Assert.False(PcShutdownRules.IsDeliveryInWindow(null, countdownSeconds: 30, Now));
    }

    [Fact]
    public void IsWithin_FutureTime_CountsAsWithin()
    {
        Assert.True(PcShutdownRules.IsWithin(Now.AddSeconds(30), Now, PcShutdownRules.RequestWindow));
    }

    [Fact]
    public void IsWithin_ExactlyTheWindow_IsNotWithin()
    {
        Assert.False(PcShutdownRules.IsWithin(Now.AddSeconds(-60), Now, PcShutdownRules.RequestWindow));
    }
}
