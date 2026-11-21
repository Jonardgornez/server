using LabGuard.Server.Screen;

namespace LabGuard.Server.Tests;

// The stream's choice of source: the PC's own stream, or the secure-desktop helper while the PC is paused.
public class ScreenStreamSourceTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
    private static readonly BrowserControlMessage MoveInput = new(BrowserControlKind.Move, "{\"t\":\"move\",\"x\":1,\"y\":1}", InputRate.Move);
    private static readonly BrowserControlMessage SasInput = new(BrowserControlKind.Input, "{\"t\":\"sas\"}", InputRate.Sas);
    private static readonly BrowserControlMessage TextInput = new(BrowserControlKind.Input, "{\"t\":\"text\",\"s\":\"a\"}", InputRate.Text);

    private static ScreenStream NewStream() => SingleViewerStream.New();

    private static ScreenSocket NewSocket() => new(new FakeWebSocket(), TimeSpan.FromSeconds(1));

    private static (ScreenStream Stream, ScreenSocket Pc) LiveStream()
    {
        var stream = NewStream();
        var pc = NewSocket();
        stream.TryAttachPc(pc, stream.ViewId, out _);
        stream.TrySwitchSource(null);
        return (stream, pc);
    }

    private static void TurnControlOn(ScreenStream stream)
    {
        var request = stream.TryBeginControlRequest()!.Value;
        Assert.True(stream.TryConfirmControl(request.Pc, 5));
    }

    [Fact]
    public void TryTakeCursorSlot_OnlyForThePcThatCarriesControl_WhileOn()
    {
        var (stream, pc) = LiveStream();

        Assert.False(stream.TryTakeCursorSlot(pc, Now));
        stream.TryBeginControlRequest();
        Assert.False(stream.TryTakeCursorSlot(pc, Now));
        Assert.True(stream.TryConfirmControl(pc, 5));
        Assert.True(stream.TryTakeCursorSlot(pc, Now));
        Assert.False(stream.TryTakeCursorSlot(NewSocket(), Now));
    }

    [Fact]
    public void TryTakeCursorSlot_FromThePausedPc_WhileTheHelperCarriesControl_IsRefused()
    {
        var (stream, pc) = LiveStream();
        var helper = NewSocket();
        TurnControlOn(stream);
        stream.TrySetPcPaused(pc, isPaused: true);
        stream.TrySwitchSource(helper);

        Assert.False(stream.TryTakeCursorSlot(pc, Now));
        Assert.False(stream.TryTakeCursorSlot(helper, Now));
    }

    [Fact]
    public void TrySwitchSource_ToTheHelper_ResetsTheCursorOnlyOnceOneWasShown()
    {
        var (stream, pc) = LiveStream();
        var helper = NewSocket();
        TurnControlOn(stream);
        stream.TrySetPcPaused(pc, isPaused: true);
        Assert.False(stream.TrySwitchSource(helper)!.Value.ResetCursor);
        stream.TrySetPcPaused(pc, isPaused: false);
        stream.TrySwitchSource(helper);
        Assert.True(stream.TryTakeCursorSlot(pc, Now));
        stream.TrySetPcPaused(pc, isPaused: true);

        Assert.True(stream.TrySwitchSource(helper)!.Value.ResetCursor);
    }

    [Fact]
    public void TryEndControl_ResetsTheCursorOnlyOnceOneWasShown()
    {
        var (stream, pc) = LiveStream();
        TurnControlOn(stream);
        Assert.False(stream.TryEndControl()!.Value.ResetCursor);
        TurnControlOn(stream);
        stream.TryTakeCursorSlot(pc, Now);

        Assert.True(stream.TryEndControl()!.Value.ResetCursor);
    }

    [Fact]
    public void TrySwitchSource_AfterAttach_ReportsLiveOnce()
    {
        var stream = NewStream();
        stream.TryAttachPc(NewSocket(), stream.ViewId, out _);

        Assert.Equal(ScreenProtocol.LiveState, stream.TrySwitchSource(null)!.Value.State);
        Assert.Null(stream.TrySwitchSource(null));
    }

    [Fact]
    public void TrySwitchSource_IgnoresAConnectedHelper_WhileThePcIsNotPaused()
    {
        var (stream, _) = LiveStream();

        Assert.Null(stream.TrySwitchSource(NewSocket()));
    }

    [Fact]
    public void TrySwitchSource_PausedWithNoHelper_IsPaused_AndStartsNothing()
    {
        var (stream, pc) = LiveStream();
        stream.TrySetPcPaused(pc, isPaused: true);

        var change = stream.TrySwitchSource(null)!.Value;

        Assert.Equal(ScreenProtocol.PausedState, change.State);
        Assert.Null(change.StartedHelper);
    }

    [Fact]
    public void TrySwitchSource_PausedWithAHelper_StartsIt_AndIsSecure_ThenResumedStopsIt()
    {
        var (stream, pc) = LiveStream();
        var helper = NewSocket();
        stream.TrySetPcPaused(pc, isPaused: true);

        var started = stream.TrySwitchSource(helper)!.Value;
        stream.TrySetPcPaused(pc, isPaused: false);
        var stopped = stream.TrySwitchSource(helper)!.Value;

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
        var (stream, _) = LiveStream();

        Assert.False(stream.TrySetPcPaused(NewSocket(), isPaused: true));
        Assert.Null(stream.TrySwitchSource(NewSocket()));
    }

    [Fact]
    public void TrySwitchSource_WithControlOn_MovesControlToTheHelper_ReleasesThePc_AndBackOnResume()
    {
        var (stream, pc) = LiveStream();
        var helper = NewSocket();
        TurnControlOn(stream);
        stream.TrySetPcPaused(pc, isPaused: true);

        var onto = stream.TrySwitchSource(helper)!.Value;
        var inputWhileSecure = stream.TakeControlInputTarget(MoveInput, Now);
        stream.TrySetPcPaused(pc, isPaused: false);
        var back = stream.TrySwitchSource(helper)!.Value;

        Assert.Same(helper, onto.ControlSource);
        Assert.Same(pc, onto.ReleasedPc);
        Assert.Null(onto.EndedControl);
        Assert.Same(helper, inputWhileSecure);
        Assert.Same(pc, back.ControlSource);
        Assert.Null(back.ReleasedPc);
        Assert.Equal(ScreenControlState.On, stream.ControlState);
        Assert.Same(pc, stream.TakeControlInputTarget(MoveInput, Now));
    }

    [Fact]
    public void TrySwitchSource_TheHelperLeavingWhileThePcIsPaused_EndsControl_AndIsPaused()
    {
        var (stream, pc) = LiveStream();
        var helper = NewSocket();
        TurnControlOn(stream);
        stream.TrySetPcPaused(pc, isPaused: true);
        stream.TrySwitchSource(helper);

        var change = stream.TrySwitchSource(null)!.Value;

        Assert.Same(helper, change.StoppedHelper);
        Assert.Equal(5, change.EndedControl!.Value.AuditId);
        Assert.Same(pc, change.EndedControl!.Value.AttachedPc);
        Assert.Equal(ScreenProtocol.PausedState, change.State);
        Assert.Equal(ScreenControlState.Off, stream.ControlState);
    }

    [Fact]
    public void ControlRequestedWhileTheHelperStreams_IsAskedOfTheHelper_AndOnlyItCanConfirm()
    {
        var (stream, pc) = LiveStream();
        var helper = NewSocket();
        stream.TrySetPcPaused(pc, isPaused: true);
        stream.TrySwitchSource(helper);

        var request = stream.TryBeginControlRequest()!.Value;

        Assert.Same(helper, request.Pc);
        Assert.False(stream.IsAwaitingConfirmation(pc));
        Assert.False(stream.TryConfirmControl(pc, 1));
        Assert.True(stream.TryConfirmControl(helper, 1));
    }

    [Fact]
    public void TryEndControlFrom_OnlyTheSocketThatCarriesControl()
    {
        var (stream, pc) = LiveStream();
        var helper = NewSocket();
        TurnControlOn(stream);
        stream.TrySetPcPaused(pc, isPaused: true);
        stream.TrySwitchSource(helper);

        Assert.Null(stream.TryEndControlFrom(pc));
        var ended = stream.TryEndControlFrom(helper)!.Value;

        Assert.Same(pc, ended.AttachedPc);
        Assert.Same(helper, ended.Helper);
        Assert.Equal(ScreenControlState.Off, stream.ControlState);
    }

    [Fact]
    public void Sas_GoesToTheConnectedHelper_EvenWhileThePcIsTheSource()
    {
        var (stream, pc) = LiveStream();
        var helper = NewSocket();
        TurnControlOn(stream);

        Assert.Same(helper, stream.TakeControlInputTarget(SasInput, Now, connectedHelper: helper));
        Assert.Same(pc, stream.TakeControlInputTarget(SasInput, Now.AddSeconds(2)));
    }

    [Fact]
    public void Text_GoesToTheSourceThatCarriesControl_NotToAConnectedHelperWhileThePcIsTheSource()
    {
        var (stream, pc) = LiveStream();
        var helper = NewSocket();
        TurnControlOn(stream);

        Assert.Same(pc, stream.TakeControlInputTarget(TextInput, Now, connectedHelper: helper));

        stream.TrySetPcPaused(pc, isPaused: true);
        stream.TrySwitchSource(helper);

        Assert.Same(helper, stream.TakeControlInputTarget(TextInput, Now, connectedHelper: helper));
    }

    [Fact]
    public void FrameCredits_OnlyTheCurrentSourceMaySend()
    {
        var (stream, pc) = LiveStream();
        var helper = NewSocket();
        stream.TrySetPcPaused(pc, isPaused: true);
        stream.TrySwitchSource(helper);

        Assert.False(stream.TakesFrame(pc));
        Assert.True(stream.TakesFrame(helper));
    }

    [Fact]
    public void FrameCredits_EachSourceHasItsOwnWindow_AndAcksGoToTheSenderOfTheOldestFrame()
    {
        var (stream, pc) = LiveStream();
        var helper = NewSocket();
        Assert.True(stream.TakesFrame(pc));
        stream.TrySetPcPaused(pc, isPaused: true);
        stream.TrySwitchSource(helper);

        Assert.True(stream.TakesFrame(helper));
        Assert.True(stream.TakesFrame(helper));
        Assert.False(stream.TakesFrame(helper));
        Assert.True(stream.TryReturnFrameCredit(out var first));
        Assert.True(stream.TryReturnFrameCredit(out var second));

        Assert.Same(pc, first);
        Assert.Same(helper, second);
    }

    [Fact]
    public void FrameCredits_AnAckForAStoppedHelperRun_GoesNowhere_AndDoesNotCostTheNextRun()
    {
        var (stream, pc) = LiveStream();
        var helper = NewSocket();
        stream.TrySetPcPaused(pc, isPaused: true);
        stream.TrySwitchSource(helper);
        stream.TakesFrame(helper);
        stream.TakesFrame(helper);
        stream.TrySetPcPaused(pc, isPaused: false);
        stream.TrySwitchSource(helper);
        stream.TrySetPcPaused(pc, isPaused: true);
        stream.TrySwitchSource(helper);

        Assert.True(stream.TakesFrame(helper));
        Assert.True(stream.TryReturnFrameCredit(out var stale));
        Assert.Null(stale);
    }

    [Fact]
    public void FrameCredits_NeverMoreThanTheCapAcrossASwitch()
    {
        var (stream, pc) = LiveStream();
        var first = NewSocket();
        var second = NewSocket();
        stream.TakesFrame(pc);
        stream.TakesFrame(pc);
        stream.TrySetPcPaused(pc, isPaused: true);
        stream.TrySwitchSource(first);
        stream.TakesFrame(first);
        stream.TakesFrame(first);

        stream.TrySwitchSource(second);

        Assert.False(stream.TakesFrame(second));
    }

    [Fact]
    public void TheLastViewerLeaving_HandsBackTheStreamingHelper()
    {
        var (stream, pc) = LiveStream();
        var helper = NewSocket();
        stream.TrySetPcPaused(pc, isPaused: true);
        stream.TrySwitchSource(helper);

        var leave = stream.CloseOnlyViewer();

        Assert.True(leave.IsLast);
        Assert.Same(pc, leave.ClosedPc);
        Assert.Same(helper, leave.StreamingHelper);
        Assert.Null(stream.TrySwitchSource(helper));
    }
}
