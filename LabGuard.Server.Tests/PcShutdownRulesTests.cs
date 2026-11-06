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
