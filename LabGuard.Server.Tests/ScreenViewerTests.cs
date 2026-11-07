using LabGuard.Server.Screen;

namespace LabGuard.Server.Tests;

public class ScreenViewerTests
{
    private const string ViewId = "0123456789abcdef0123456789abcdef";

    private static ScreenViewer NewViewer(string viewId = ViewId) =>
        new(viewId, pcId: 7, mode: ScreenProtocol.NotifiedMode, auditId: 1, DateTimeOffset.UtcNow.AddMinutes(30),
            new ScreenSocket(new FakeWebSocket(), TimeSpan.FromSeconds(1)), owner: new ScreenViewOwner(1, 1, "tester"), pcName: "PC-01");

    private static ScreenSocket NewSocket() => new(new FakeWebSocket(), TimeSpan.FromSeconds(1));

    [Fact]
    public void ViewIdMatches_OnlyTheExactId()
    {
        var viewer = NewViewer();

        Assert.True(viewer.ViewIdMatches(ViewId));
        Assert.False(viewer.ViewIdMatches(ViewId[..31]));
        Assert.False(viewer.ViewIdMatches(ViewId + "0"));
        Assert.False(viewer.ViewIdMatches(ViewId.ToUpperInvariant()));
        Assert.False(viewer.ViewIdMatches(""));
        Assert.False(viewer.ViewIdMatches(null));
    }

    [Fact]
    public void TryTakeFrameCredit_OnlyForTheAttachedPc_AndOnlyTwoAtATime()
    {
        var viewer = NewViewer();
        var attached = NewSocket();
        var stranger = NewSocket();
        viewer.TryAttachPc(attached, out _);

        Assert.False(viewer.TryTakeFrameCredit(stranger));
        Assert.True(viewer.TryTakeFrameCredit(attached));
        Assert.True(viewer.TryTakeFrameCredit(attached));
        Assert.False(viewer.TryTakeFrameCredit(attached));
    }

    [Fact]
    public void TryReturnFrameCredit_OnlyWhenOneIsOutstanding()
    {
        var viewer = NewViewer();
        var pc = NewSocket();
        viewer.TryAttachPc(pc, out _);

        Assert.False(viewer.TryReturnFrameCredit());
        viewer.TryTakeFrameCredit(pc);
        Assert.True(viewer.TryReturnFrameCredit());
        Assert.False(viewer.TryReturnFrameCredit());
    }

    [Fact]
    public void TryAttachPc_StartsTheCreditWindowAgain()
    {
        var viewer = NewViewer();
        var first = NewSocket();
        var second = NewSocket();
        viewer.TryAttachPc(first, out _);
        viewer.TryTakeFrameCredit(first);
        viewer.TryTakeFrameCredit(first);

        viewer.TryAttachPc(second, out _);

        Assert.True(viewer.TryTakeFrameCredit(second));
        Assert.True(viewer.TryTakeFrameCredit(second));
    }

    [Fact]
    public void TryAttachPc_ReturnsTheSocketItReplaces()
    {
        var viewer = NewViewer();
        var first = NewSocket();
        var second = NewSocket();

        Assert.True(viewer.TryAttachPc(first, out var replacedByFirst));
        Assert.Null(replacedByFirst);
        Assert.True(viewer.TryAttachPc(second, out var replacedBySecond));
        Assert.Same(first, replacedBySecond);
        Assert.True(viewer.IsCurrentPc(second));
        Assert.False(viewer.IsCurrentPc(first));
    }

    [Fact]
    public void ReplacedSocket_CannotTakeCreditsOrChangeState()
    {
        var viewer = NewViewer();
        var replaced = NewSocket();
        viewer.TryAttachPc(replaced, out _);
        viewer.TryAttachPc(NewSocket(), out _);

        Assert.False(viewer.TryTakeFrameCredit(replaced));
        Assert.False(viewer.TrySetState(ScreenProtocol.PausedState, replaced));
    }

    [Fact]
    public void TryDetachPc_OnlyTheAttachedSocketCanLeave()
    {
        var viewer = NewViewer();
        var attached = NewSocket();
        var other = NewSocket();
        viewer.TryAttachPc(attached, out _);

        Assert.False(viewer.TryDetachPc(other));
        Assert.True(viewer.TryDetachPc(attached));
        Assert.Null(viewer.CurrentPc);
        Assert.False(viewer.TryDetachPc(attached));
    }

    [Fact]
    public void TrySetState_OnlyWhenTheStateChanges()
    {
        var viewer = NewViewer();

        Assert.True(viewer.TrySetState(ScreenProtocol.WaitingState));
        Assert.False(viewer.TrySetState(ScreenProtocol.WaitingState));
        Assert.True(viewer.TrySetState(ScreenProtocol.LiveState));
        Assert.True(viewer.TrySetState(ScreenProtocol.PausedState));
        Assert.False(viewer.TrySetState(ScreenProtocol.PausedState));
    }

    [Fact]
    public void TrySetState_NamedPcMustBeTheAttachedOne()
    {
        var viewer = NewViewer();
        var pc = NewSocket();

        Assert.False(viewer.TrySetState(ScreenProtocol.PausedState, pc));
        viewer.TryAttachPc(pc, out _);
        Assert.True(viewer.TrySetState(ScreenProtocol.PausedState, pc));
    }

    [Fact]
    public void TryClose_IsOnlyTrueOnce_AndHandsBackThePcSocket()
    {
        var viewer = NewViewer();
        var pc = NewSocket();
        viewer.TryAttachPc(pc, out _);

        Assert.True(viewer.TryClose(out var handedBack));
        Assert.Same(pc, handedBack);
        Assert.False(viewer.TryClose(out var second));
        Assert.Null(second);
    }

    [Fact]
    public void AfterClose_NothingCanAttachTakeCreditOrChangeState()
    {
        var viewer = NewViewer();
        viewer.TryClose(out _);

        Assert.False(viewer.TryAttachPc(NewSocket(), out _));
        Assert.False(viewer.TrySetState(ScreenProtocol.LiveState));
        Assert.Null(viewer.CurrentPc);
    }

    [Fact]
    public void CancelLifetime_CancelsEverySourceLinkedToIt()
    {
        var viewer = NewViewer();
        using var linked = viewer.LinkLifetime(CancellationToken.None);
        Assert.NotNull(linked);
        Assert.False(linked.IsCancellationRequested);

        viewer.CancelLifetime();

        Assert.True(linked.IsCancellationRequested);
    }

    [Fact]
    public void Dispose_EndsTheLifetime_AndNoLinkIsMadeAfterwards()
    {
        var viewer = NewViewer();

        viewer.Dispose();

        Assert.Null(viewer.LinkLifetime(CancellationToken.None));
        viewer.CancelLifetime();
    }

    // Remote control

    [Fact]
    public void ControlState_StartsOff()
    {
        Assert.Equal(ScreenControlState.Off, NewViewer().ControlState);
    }

    [Fact]
    public void TryBeginControlRequest_NeedsAnAttachedPc_AndOnlyOneAtATime()
    {
        var viewer = NewViewer();
        var pc = NewSocket();

        Assert.Null(viewer.TryBeginControlRequest());
        viewer.TryAttachPc(pc, out _);
        var request = viewer.TryBeginControlRequest();

        Assert.NotNull(request);
        Assert.Same(pc, request!.Value.Pc);
        Assert.Equal(ScreenControlState.Requesting, viewer.ControlState);
        Assert.Null(viewer.TryBeginControlRequest());
    }

    [Fact]
    public void TryConfirmControl_OnlyFromTheAskedPc_AndOnlyWhileRequesting()
    {
        var viewer = NewViewer();
        var asked = NewSocket();
        var stranger = NewSocket();
        viewer.TryAttachPc(asked, out _);

        Assert.False(viewer.TryConfirmControl(asked, 5));
        viewer.TryBeginControlRequest();
        Assert.True(viewer.IsAwaitingConfirmation(asked));
        Assert.False(viewer.IsAwaitingConfirmation(stranger));
        Assert.False(viewer.TryConfirmControl(stranger, 5));
        Assert.True(viewer.TryConfirmControl(asked, 5));
        Assert.Equal(ScreenControlState.On, viewer.ControlState);
        Assert.False(viewer.TryConfirmControl(asked, 6));
    }

    [Fact]
    public void TryConfirmControl_FromAReplacedPc_IsRefused()
    {
        var viewer = NewViewer();
        var replaced = NewSocket();
        viewer.TryAttachPc(replaced, out _);
        viewer.TryBeginControlRequest();
        viewer.TryAttachPc(NewSocket(), out _);

        Assert.False(viewer.TryConfirmControl(replaced, 5));
        Assert.Equal(ScreenControlState.Requesting, viewer.ControlState);
    }

    [Fact]
    public void TryExpireRequest_OnlyTheRequestItWasMadeFor()
    {
        var viewer = NewViewer();
        viewer.TryAttachPc(NewSocket(), out _);
        var first = viewer.TryBeginControlRequest()!.Value;
        viewer.TryEndControl();
        var second = viewer.TryBeginControlRequest()!.Value;

        Assert.Null(viewer.TryExpireRequest(first.Id));
        Assert.NotNull(viewer.TryExpireRequest(second.Id));
        Assert.Equal(ScreenControlState.Off, viewer.ControlState);
    }

    [Fact]
    public void TryExpireRequest_DoesNothingOnceControlIsOn()
    {
        var viewer = NewViewer();
        var pc = NewSocket();
        viewer.TryAttachPc(pc, out _);
        var request = viewer.TryBeginControlRequest()!.Value;
        viewer.TryConfirmControl(pc, 9);

        Assert.Null(viewer.TryExpireRequest(request.Id));
        Assert.Equal(ScreenControlState.On, viewer.ControlState);
    }

    [Fact]
    public void TryEndControl_HandsBackTheAuditRowAndThePc_WhileThePcIsAttached()
    {
        var viewer = NewViewer();
        var pc = NewSocket();
        viewer.TryAttachPc(pc, out _);
        viewer.TryBeginControlRequest();
        viewer.TryConfirmControl(pc, 9);

        var ended = viewer.TryEndControl();

        Assert.Equal(9, ended!.Value.AuditId);
        Assert.Same(pc, ended.Value.AttachedPc);
        Assert.Equal(ScreenControlState.Off, viewer.ControlState);
        Assert.Null(viewer.TryEndControl());
    }

    [Fact]
    public void TryEndControl_AfterThePcWasReplaced_DoesNotHandBackTheOldPc()
    {
        var viewer = NewViewer();
        var first = NewSocket();
        viewer.TryAttachPc(first, out _);
        viewer.TryBeginControlRequest();
        viewer.TryConfirmControl(first, 9);
        viewer.TryAttachPc(NewSocket(), out _);

        var ended = viewer.TryEndControl();

        Assert.Null(ended!.Value.AttachedPc);
        Assert.Equal(9, ended.Value.AuditId);
    }

    [Fact]
    public void TryEndControl_AfterThePcDetached_DoesNotHandBackThePc()
    {
        var viewer = NewViewer();
        var pc = NewSocket();
        viewer.TryAttachPc(pc, out _);
        viewer.TryBeginControlRequest();
        viewer.TryDetachPc(pc);

        var ended = viewer.TryEndControl();

        Assert.NotNull(ended);
        Assert.Null(ended!.Value.AttachedPc);
        Assert.Null(ended.Value.AuditId);
    }

    private static readonly BrowserControlMessage MoveInput = new(BrowserControlKind.Move, "{}", InputRate.Move);
    private static readonly BrowserControlMessage WheelInput = new(BrowserControlKind.Input, "{}", InputRate.Other);
    private static readonly BrowserControlMessage ReleaseAllInput = new(BrowserControlKind.Input, "{}", InputRate.Exempt, HeldEffect.ReleaseAll);

    private static BrowserControlMessage PressInput(string key) => new(BrowserControlKind.Input, "{}", InputRate.Other, HeldEffect.Press, key);
    private static BrowserControlMessage ReleaseInput(string key) => new(BrowserControlKind.Input, "{}", InputRate.Exempt, HeldEffect.Release, key);

    // A viewer with control on, held by the returned PC.
    private static ScreenViewer ConfirmedViewer(out ScreenSocket pc)
    {
        var viewer = NewViewer();
        pc = NewSocket();
        viewer.TryAttachPc(pc, out _);
        viewer.TryBeginControlRequest();
        viewer.TryConfirmControl(pc, 9);
        return viewer;
    }

    [Fact]
    public void TakeControlInputTarget_OnlyWhileOn_ForTheAttachedPc()
    {
        var viewer = NewViewer();
        var pc = NewSocket();
        var now = DateTimeOffset.UtcNow;
        viewer.TryAttachPc(pc, out _);
        Assert.Null(viewer.TakeControlInputTarget(MoveInput, now));

        viewer.TryBeginControlRequest();
        Assert.Null(viewer.TakeControlInputTarget(MoveInput, now));

        viewer.TryConfirmControl(pc, 9);
        Assert.Same(pc, viewer.TakeControlInputTarget(MoveInput, now));

        viewer.TryDetachPc(pc);
        Assert.Null(viewer.TakeControlInputTarget(MoveInput, now));
    }

    [Fact]
    public void TakeControlInputTarget_CapsMovesAndOtherInputSeparately()
    {
        var viewer = ConfirmedViewer(out _);
        var now = DateTimeOffset.UtcNow;

        var moves = Enumerable.Range(0, ScreenProtocol.MaxMovesPerSecond + 10)
            .Count(_ => viewer.TakeControlInputTarget(MoveInput, now) is not null);
        var wheels = Enumerable.Range(0, ScreenProtocol.MaxOtherInputPerSecond + 10)
            .Count(_ => viewer.TakeControlInputTarget(WheelInput, now) is not null);

        Assert.Equal(ScreenProtocol.MaxMovesPerSecond, moves);
        Assert.Equal(ScreenProtocol.MaxOtherInputPerSecond, wheels);
    }

    [Fact]
    public void TakeControlInputTarget_AnUpOrReleaseIsRelayedOnlyForSomethingHeld()
    {
        var viewer = ConfirmedViewer(out _);
        var now = DateTimeOffset.UtcNow;

        Assert.Null(viewer.TakeControlInputTarget(ReleaseInput("kKeyA"), now));
        Assert.Null(viewer.TakeControlInputTarget(ReleaseAllInput, now));
        Assert.NotNull(viewer.TakeControlInputTarget(PressInput("kKeyA"), now));
        Assert.NotNull(viewer.TakeControlInputTarget(ReleaseInput("kKeyA"), now));
        Assert.Null(viewer.TakeControlInputTarget(ReleaseInput("kKeyA"), now));
    }

    [Fact]
    public void TakeControlInputTarget_ReleaseAll_IsRelayedOnlyWhileSomethingIsHeld_AndClearsIt()
    {
        var viewer = ConfirmedViewer(out _);
        var now = DateTimeOffset.UtcNow;
        viewer.TakeControlInputTarget(PressInput("kKeyA"), now);
        viewer.TakeControlInputTarget(PressInput("b0"), now);

        Assert.NotNull(viewer.TakeControlInputTarget(ReleaseAllInput, now));
        Assert.Null(viewer.TakeControlInputTarget(ReleaseAllInput, now));
        Assert.Null(viewer.TakeControlInputTarget(ReleaseInput("kKeyA"), now));
    }

    [Fact]
    public void TakeControlInputTarget_ExemptInputIsNotCappedByTheMoveOrOtherCaps()
    {
        var viewer = ConfirmedViewer(out _);
        var now = DateTimeOffset.UtcNow;
        viewer.TakeControlInputTarget(PressInput("kKeyA"), now);
        for (var attempt = 0; attempt < ScreenProtocol.MaxOtherInputPerSecond; attempt++)
        {
            viewer.TakeControlInputTarget(WheelInput, now);
        }

        Assert.Null(viewer.TakeControlInputTarget(WheelInput, now));
        Assert.NotNull(viewer.TakeControlInputTarget(ReleaseInput("kKeyA"), now));
    }

    [Fact]
    public void TakeControlInputTarget_HeldKeysAreBounded_AndAPressBeyondTheBoundIsDropped()
    {
        var viewer = ConfirmedViewer(out _);
        var now = DateTimeOffset.UtcNow;

        // Presses are capped at 200 a second, so the bound is reached over two seconds.
        for (var index = 0; index < ScreenProtocol.MaxHeldInputs; index++)
        {
            Assert.NotNull(viewer.TakeControlInputTarget(PressInput($"k{index}"), now.AddSeconds(index / 200)));
        }

        var later = now.AddSeconds(2);
        Assert.Null(viewer.TakeControlInputTarget(PressInput("kOverflow"), later));
        Assert.NotNull(viewer.TakeControlInputTarget(PressInput("k0"), later));
    }

    [Fact]
    public void EndingControl_ClearsWhatWasHeld()
    {
        var viewer = ConfirmedViewer(out _);
        var now = DateTimeOffset.UtcNow;
        viewer.TakeControlInputTarget(PressInput("kKeyA"), now);
        viewer.TryEndControl();

        var pc = NewSocket();
        viewer.TryAttachPc(pc, out _);
        viewer.TryBeginControlRequest();
        viewer.TryConfirmControl(pc, 10);

        Assert.Null(viewer.TakeControlInputTarget(ReleaseInput("kKeyA"), now));
    }

    [Fact]
    public void TryTakeControlRequestSlot_AllowsOneRequestPerSecond()
    {
        var viewer = NewViewer();
        var now = DateTimeOffset.UtcNow;

        Assert.True(viewer.TryTakeControlRequestSlot(now));
        Assert.False(viewer.TryTakeControlRequestSlot(now.AddMilliseconds(999)));
        Assert.True(viewer.TryTakeControlRequestSlot(now.AddSeconds(1)));
    }

    [Fact]
    public void TryEndRequestFrom_OnlyEndsARequestForThatPc()
    {
        var viewer = NewViewer();
        var asked = NewSocket();
        var stranger = NewSocket();
        viewer.TryAttachPc(asked, out _);
        viewer.TryBeginControlRequest();

        Assert.Null(viewer.TryEndRequestFrom(stranger));
        Assert.NotNull(viewer.TryEndRequestFrom(asked));
        Assert.Equal(ScreenControlState.Off, viewer.ControlState);
    }

    [Fact]
    public void EndControlOnClose_HandsBackTheAuditRow_AndNothingCanStartAfterwards()
    {
        var viewer = NewViewer();
        var pc = NewSocket();
        viewer.TryAttachPc(pc, out _);
        viewer.TryBeginControlRequest();
        viewer.TryConfirmControl(pc, 9);
        viewer.TryClose(out _);

        Assert.Equal(9, viewer.EndControlOnClose());
        Assert.Equal(ScreenControlState.Off, viewer.ControlState);
        Assert.Null(viewer.EndControlOnClose());
        Assert.Null(viewer.TryBeginControlRequest());
    }

    [Fact]
    public void TryConfirmControl_AfterClose_IsRefused()
    {
        var viewer = NewViewer();
        var pc = NewSocket();
        viewer.TryAttachPc(pc, out _);
        viewer.TryBeginControlRequest();
        viewer.TryClose(out _);

        Assert.False(viewer.IsAwaitingConfirmation(pc));
        Assert.False(viewer.TryConfirmControl(pc, 9));
    }
}
