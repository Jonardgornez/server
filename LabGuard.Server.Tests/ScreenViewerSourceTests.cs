using LabGuard.Server.Screen;

namespace LabGuard.Server.Tests;

// The viewer's choice of source: the PC's own stream, or the secure-desktop helper while the PC is paused.
public class ScreenViewerSourceTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
    private static readonly BrowserControlMessage MoveInput = new(BrowserControlKind.Move, "{\"t\":\"move\",\"x\":1,\"y\":1}", InputRate.Move);
    private static readonly BrowserControlMessage SasInput = new(BrowserControlKind.Input, "{\"t\":\"sas\"}", InputRate.Sas);
    private static readonly BrowserControlMessage TextInput = new(BrowserControlKind.Input, "{\"t\":\"text\",\"s\":\"a\"}", InputRate.Text);

    private static ScreenViewer NewViewer() =>
        new("0123456789abcdef0123456789abcdef", pcId: 7, mode: ScreenProtocol.NotifiedMode, auditId: 1, DateTimeOffset.UtcNow.AddMinutes(30),
            new ScreenSocket(new FakeWebSocket(), TimeSpan.FromSeconds(1)), owner: new ScreenViewOwner(1, 1, "tester"), pcName: "PC-01");

    private static ScreenSocket NewSocket() => new(new FakeWebSocket(), TimeSpan.FromSeconds(1));

    private static (ScreenViewer Viewer, ScreenSocket Pc) LiveViewer()
    {
        var viewer = NewViewer();
        var pc = NewSocket();
        viewer.TryAttachPc(pc, out _);
        viewer.TrySwitchSource(null);
        return (viewer, pc);
    }

    private static void TurnControlOn(ScreenViewer viewer)
    {
        var request = viewer.TryBeginControlRequest()!.Value;
        Assert.True(viewer.TryConfirmControl(request.Pc, 5));
    }

    [Fact]
    public void TrySwitchSource_AfterAttach_ReportsLiveOnce()
    {
        var viewer = NewViewer();
        viewer.TryAttachPc(NewSocket(), out _);

        Assert.Equal(ScreenProtocol.LiveState, viewer.TrySwitchSource(null)!.Value.State);
        Assert.Null(viewer.TrySwitchSource(null));
    }

    [Fact]
    public void TrySwitchSource_IgnoresAConnectedHelper_WhileThePcIsNotPaused()
    {
        var (viewer, _) = LiveViewer();

        Assert.Null(viewer.TrySwitchSource(NewSocket()));
    }

    [Fact]
    public void TrySwitchSource_PausedWithNoHelper_IsPaused_AndStartsNothing()
    {
        var (viewer, pc) = LiveViewer();
        viewer.TrySetPcPaused(pc, isPaused: true);

        var change = viewer.TrySwitchSource(null)!.Value;

        Assert.Equal(ScreenProtocol.PausedState, change.State);
        Assert.Null(change.StartedHelper);
    }

    [Fact]
    public void TrySwitchSource_PausedWithAHelper_StartsIt_AndIsSecure_ThenResumedStopsIt()
    {
        var (viewer, pc) = LiveViewer();
        var helper = NewSocket();
        viewer.TrySetPcPaused(pc, isPaused: true);

        var started = viewer.TrySwitchSource(helper)!.Value;
        viewer.TrySetPcPaused(pc, isPaused: false);
        var stopped = viewer.TrySwitchSource(helper)!.Value;

        Assert.Same(helper, started.StartedHelper);
        Assert.Equal(ScreenProtocol.SecureState, started.State);
        Assert.Null(started.ControlSource);
        Assert.Same(helper, stopped.StoppedHelper);
        Assert.Null(stopped.StartedHelper);
        Assert.Equal(ScreenProtocol.LiveState, stopped.State);
    }

    [Fact]
    public void TrySetPcPaused_OnlyForTheAttachedPc()
    {
        var (viewer, _) = LiveViewer();

        Assert.False(viewer.TrySetPcPaused(NewSocket(), isPaused: true));
        Assert.Null(viewer.TrySwitchSource(NewSocket()));
    }

    [Fact]
    public void TrySwitchSource_WithControlOn_MovesControlToTheHelper_ReleasesThePc_AndBackOnResume()
    {
        var (viewer, pc) = LiveViewer();
        var helper = NewSocket();
        TurnControlOn(viewer);
        viewer.TrySetPcPaused(pc, isPaused: true);

        var onto = viewer.TrySwitchSource(helper)!.Value;
        var inputWhileSecure = viewer.TakeControlInputTarget(MoveInput, Now);
        viewer.TrySetPcPaused(pc, isPaused: false);
        var back = viewer.TrySwitchSource(helper)!.Value;

        Assert.Same(helper, onto.ControlSource);
        Assert.Same(pc, onto.ReleasedPc);
        Assert.Null(onto.EndedControl);
        Assert.Same(helper, inputWhileSecure);
        Assert.Same(pc, back.ControlSource);
        Assert.Null(back.ReleasedPc);
        Assert.Equal(ScreenControlState.On, viewer.ControlState);
        Assert.Same(pc, viewer.TakeControlInputTarget(MoveInput, Now));
    }

    [Fact]
    public void TrySwitchSource_TheHelperLeavingWhileThePcIsPaused_EndsControl_AndIsPaused()
    {
        var (viewer, pc) = LiveViewer();
        var helper = NewSocket();
        TurnControlOn(viewer);
        viewer.TrySetPcPaused(pc, isPaused: true);
        viewer.TrySwitchSource(helper);

        var change = viewer.TrySwitchSource(null)!.Value;

        Assert.Same(helper, change.StoppedHelper);
        Assert.Equal(5, change.EndedControl!.Value.AuditId);
        Assert.Same(pc, change.EndedControl!.Value.AttachedPc);
        Assert.Equal(ScreenProtocol.PausedState, change.State);
        Assert.Equal(ScreenControlState.Off, viewer.ControlState);
    }

    [Fact]
    public void ControlRequestedWhileTheHelperStreams_IsAskedOfTheHelper_AndOnlyItCanConfirm()
    {
        var (viewer, pc) = LiveViewer();
        var helper = NewSocket();
        viewer.TrySetPcPaused(pc, isPaused: true);
        viewer.TrySwitchSource(helper);

        var request = viewer.TryBeginControlRequest()!.Value;

        Assert.Same(helper, request.Pc);
        Assert.False(viewer.IsAwaitingConfirmation(pc));
        Assert.False(viewer.TryConfirmControl(pc, 1));
        Assert.True(viewer.TryConfirmControl(helper, 1));
    }

    [Fact]
    public void TryEndControlFrom_OnlyTheSocketThatCarriesControl()
    {
        var (viewer, pc) = LiveViewer();
        var helper = NewSocket();
        TurnControlOn(viewer);
        viewer.TrySetPcPaused(pc, isPaused: true);
        viewer.TrySwitchSource(helper);

        Assert.Null(viewer.TryEndControlFrom(pc));
        var ended = viewer.TryEndControlFrom(helper)!.Value;

        Assert.Same(pc, ended.AttachedPc);
        Assert.Same(helper, ended.Helper);
        Assert.Equal(ScreenControlState.Off, viewer.ControlState);
    }

    [Fact]
    public void Sas_GoesToTheConnectedHelper_EvenWhileThePcIsTheSource()
    {
        var (viewer, pc) = LiveViewer();
        var helper = NewSocket();
        TurnControlOn(viewer);

        Assert.Same(helper, viewer.TakeControlInputTarget(SasInput, Now, connectedHelper: helper));
        Assert.Same(pc, viewer.TakeControlInputTarget(SasInput, Now.AddSeconds(2)));
    }

    [Fact]
    public void Text_GoesToTheSourceThatCarriesControl_NotToAConnectedHelperWhileThePcIsTheSource()
    {
        var (viewer, pc) = LiveViewer();
        var helper = NewSocket();
        TurnControlOn(viewer);

        Assert.Same(pc, viewer.TakeControlInputTarget(TextInput, Now, connectedHelper: helper));

        viewer.TrySetPcPaused(pc, isPaused: true);
        viewer.TrySwitchSource(helper);

        Assert.Same(helper, viewer.TakeControlInputTarget(TextInput, Now, connectedHelper: helper));
    }

    [Fact]
    public void FrameCredits_OnlyTheCurrentSourceMaySend()
    {
        var (viewer, pc) = LiveViewer();
        var helper = NewSocket();
        viewer.TrySetPcPaused(pc, isPaused: true);
        viewer.TrySwitchSource(helper);

        Assert.False(viewer.TryTakeFrameCredit(pc));
        Assert.True(viewer.TryTakeFrameCredit(helper));
    }

    [Fact]
    public void FrameCredits_EachSourceHasItsOwnWindow_AndAcksGoToTheSenderOfTheOldestFrame()
    {
        var (viewer, pc) = LiveViewer();
        var helper = NewSocket();
        Assert.True(viewer.TryTakeFrameCredit(pc));
        viewer.TrySetPcPaused(pc, isPaused: true);
        viewer.TrySwitchSource(helper);

        Assert.True(viewer.TryTakeFrameCredit(helper));
        Assert.True(viewer.TryTakeFrameCredit(helper));
        Assert.False(viewer.TryTakeFrameCredit(helper));
        Assert.True(viewer.TryReturnFrameCredit(out var first));
        Assert.True(viewer.TryReturnFrameCredit(out var second));

        Assert.Same(pc, first);
        Assert.Same(helper, second);
    }

    [Fact]
    public void FrameCredits_AnAckForAStoppedHelperRun_GoesNowhere_AndDoesNotCostTheNextRun()
    {
        var (viewer, pc) = LiveViewer();
        var helper = NewSocket();
        viewer.TrySetPcPaused(pc, isPaused: true);
        viewer.TrySwitchSource(helper);
        viewer.TryTakeFrameCredit(helper);
        viewer.TryTakeFrameCredit(helper);
        viewer.TrySetPcPaused(pc, isPaused: false);
        viewer.TrySwitchSource(helper);
        viewer.TrySetPcPaused(pc, isPaused: true);
        viewer.TrySwitchSource(helper);

        Assert.True(viewer.TryTakeFrameCredit(helper));
        Assert.True(viewer.TryReturnFrameCredit(out var stale));
        Assert.Null(stale);
    }

    [Fact]
    public void FrameCredits_NeverMoreThanTheCapAcrossASwitch()
    {
        var (viewer, pc) = LiveViewer();
        var first = NewSocket();
        var second = NewSocket();
        viewer.TryTakeFrameCredit(pc);
        viewer.TryTakeFrameCredit(pc);
        viewer.TrySetPcPaused(pc, isPaused: true);
        viewer.TrySwitchSource(first);
        viewer.TryTakeFrameCredit(first);
        viewer.TryTakeFrameCredit(first);

        viewer.TrySwitchSource(second);

        Assert.False(viewer.TryTakeFrameCredit(second));
    }

    [Fact]
    public void TryClose_HandsBackTheStreamingHelper()
    {
        var (viewer, pc) = LiveViewer();
        var helper = NewSocket();
        viewer.TrySetPcPaused(pc, isPaused: true);
        viewer.TrySwitchSource(helper);

        Assert.True(viewer.TryClose(out var closedPc, out var closedHelper));

        Assert.Same(pc, closedPc);
        Assert.Same(helper, closedHelper);
        Assert.Null(viewer.TrySwitchSource(helper));
    }
}
