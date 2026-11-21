using LabGuard.Server.Screen;

namespace LabGuard.Server.Tests;

// The rules of one PC's stream, seen through a single viewer. ScreenStreamViewersTests covers several viewers sharing it.
public class ScreenStreamTests
{
    private static ScreenStream NewStream() => SingleViewerStream.New();

    private static ScreenSocket NewSocket() => new(new FakeWebSocket(), TimeSpan.FromSeconds(1));

    [Fact]
    public void ViewIdMatches_OnlyTheExactId()
    {
        var stream = NewStream();
        var viewId = stream.ViewId;

        Assert.Equal(32, viewId.Length);
        Assert.True(stream.ViewIdMatches(viewId));
        Assert.False(stream.ViewIdMatches(viewId[..31]));
        Assert.False(stream.ViewIdMatches(viewId + "0"));
        Assert.False(stream.ViewIdMatches("x" + viewId[1..]));
        Assert.False(stream.ViewIdMatches(""));
        Assert.False(stream.ViewIdMatches(null));
    }

    [Fact]
    public void TryTakeFrameCredit_OnlyForTheAttachedPc_AndOnlyTwoAtATime()
    {
        var stream = NewStream();
        var attached = NewSocket();
        var stranger = NewSocket();
        stream.TryAttachPc(attached, stream.ViewId, out _);

        Assert.False(stream.TakesFrame(stranger));
        Assert.True(stream.TakesFrame(attached));
        Assert.True(stream.TakesFrame(attached));
        Assert.False(stream.TakesFrame(attached));
    }

    [Fact]
    public void TryReturnFrameCredit_OnlyWhenOneIsOutstanding()
    {
        var stream = NewStream();
        var pc = NewSocket();
        stream.TryAttachPc(pc, stream.ViewId, out _);

        Assert.False(stream.TryReturnFrameCredit(out _));
        stream.TakesFrame(pc);
        Assert.True(stream.TryReturnFrameCredit(out _));
        Assert.False(stream.TryReturnFrameCredit(out _));
    }

    [Fact]
    public void TryAttachPc_StartsTheCreditWindowAgain()
    {
        var stream = NewStream();
        var first = NewSocket();
        var second = NewSocket();
        stream.TryAttachPc(first, stream.ViewId, out _);
        stream.TakesFrame(first);
        stream.TakesFrame(first);

        stream.TryAttachPc(second, stream.ViewId, out _);

        Assert.True(stream.TakesFrame(second));
        Assert.True(stream.TakesFrame(second));
    }

    [Fact]
    public void TryAttachPc_ReturnsTheSocketItReplaces()
    {
        var stream = NewStream();
        var first = NewSocket();
        var second = NewSocket();

        Assert.True(stream.TryAttachPc(first, stream.ViewId, out var replacedByFirst));
        Assert.Null(replacedByFirst);
        Assert.True(stream.TryAttachPc(second, stream.ViewId, out var replacedBySecond));
        Assert.Same(first, replacedBySecond);
        Assert.True(stream.IsCurrentPc(second));
        Assert.False(stream.IsCurrentPc(first));
    }

    [Fact]
    public void ReplacedSocket_CannotTakeCreditsOrChangeState()
    {
        var stream = NewStream();
        var replaced = NewSocket();
        stream.TryAttachPc(replaced, stream.ViewId, out _);
        stream.TryAttachPc(NewSocket(), stream.ViewId, out _);

        Assert.False(stream.TakesFrame(replaced));
        Assert.False(stream.TrySetPcPaused(replaced, isPaused: true));
    }

    [Fact]
    public void TryDetachPc_OnlyTheAttachedSocketCanLeave()
    {
        var stream = NewStream();
        var attached = NewSocket();
        var other = NewSocket();
        stream.TryAttachPc(attached, stream.ViewId, out _);

        Assert.False(stream.TryDetachPc(other));
        Assert.True(stream.TryDetachPc(attached));
        Assert.Null(stream.CurrentPc);
        Assert.False(stream.TryDetachPc(attached));
    }

    [Fact]
    public void RemoveViewer_OfTheLastViewer_ClosesTheStream_AndHandsBackThePcSocketOnce()
    {
        var stream = NewStream();
        var pc = NewSocket();
        stream.TryAttachPc(pc, stream.ViewId, out _);
        var onlyViewer = stream.OnlyViewer();

        var leave = stream.CloseOnlyViewer();
        var again = stream.RemoveViewer(onlyViewer);

        Assert.True(leave.IsLast);
        Assert.Same(pc, leave.ClosedPc);
        Assert.True(stream.IsClosed);
        Assert.False(again.IsLast);
        Assert.Null(again.ClosedPc);
    }

    [Fact]
    public void AfterClose_NothingCanAttachJoinOrSwitch()
    {
        var stream = NewStream();
        stream.CloseOnlyViewer();

        Assert.False(stream.TryAttachPc(NewSocket(), stream.ViewId, out _));
        Assert.Null(stream.TryJoin(SingleViewerStream.NewViewer(stream)));
        Assert.Null(stream.TrySwitchSource(null));
        Assert.Null(stream.CurrentPc);
        Assert.Null(stream.Describe());
    }

    [Fact]
    public void CancelLifetime_CancelsEverySourceLinkedToIt()
    {
        var stream = NewStream();
        using var linked = stream.LinkLifetime(CancellationToken.None);
        Assert.NotNull(linked);
        Assert.False(linked.IsCancellationRequested);

        stream.CancelLifetime();

        Assert.True(linked.IsCancellationRequested);
    }

    [Fact]
    public void Dispose_EndsTheLifetime_AndNoLinkIsMadeAfterwards()
    {
        var stream = NewStream();

        stream.Dispose();

        Assert.Null(stream.LinkLifetime(CancellationToken.None));
        stream.CancelLifetime();
    }

    // Remote control

    [Fact]
    public void ControlState_StartsOff()
    {
        Assert.Equal(ScreenControlState.Off, NewStream().ControlState);
    }

    [Fact]
    public void TryBeginControlRequest_NeedsAnAttachedPc_AndOnlyOneAtATime()
    {
        var stream = NewStream();
        var pc = NewSocket();

        Assert.Null(stream.TryBeginControlRequest());
        stream.TryAttachPc(pc, stream.ViewId, out _);
        var request = stream.TryBeginControlRequest();

        Assert.NotNull(request);
        Assert.Same(pc, request!.Value.Pc);
        Assert.Equal(ScreenControlState.Requesting, stream.ControlState);
        Assert.Null(stream.TryBeginControlRequest());
    }

    [Fact]
    public void TryConfirmControl_OnlyFromTheAskedPc_AndOnlyWhileRequesting()
    {
        var stream = NewStream();
        var asked = NewSocket();
        var stranger = NewSocket();
        stream.TryAttachPc(asked, stream.ViewId, out _);

        Assert.False(stream.TryConfirmControl(asked, 5));
        stream.TryBeginControlRequest();
        Assert.True(stream.IsAwaitingConfirmation(asked));
        Assert.False(stream.IsAwaitingConfirmation(stranger));
        Assert.False(stream.TryConfirmControl(stranger, 5));
        Assert.True(stream.TryConfirmControl(asked, 5));
        Assert.Equal(ScreenControlState.On, stream.ControlState);
        Assert.False(stream.TryConfirmControl(asked, 6));
    }

    [Fact]
    public void TryConfirmControl_FromAReplacedPc_IsRefused()
    {
        var stream = NewStream();
        var replaced = NewSocket();
        stream.TryAttachPc(replaced, stream.ViewId, out _);
        stream.TryBeginControlRequest();
        stream.TryAttachPc(NewSocket(), stream.ViewId, out _);

        Assert.False(stream.TryConfirmControl(replaced, 5));
        Assert.Equal(ScreenControlState.Requesting, stream.ControlState);
    }

    [Fact]
    public void TryExpireRequest_OnlyTheRequestItWasMadeFor()
    {
        var stream = NewStream();
        stream.TryAttachPc(NewSocket(), stream.ViewId, out _);
        var first = stream.TryBeginControlRequest()!.Value;
        stream.TryEndControl();
        var second = stream.TryBeginControlRequest()!.Value;

        Assert.Null(stream.TryExpireRequest(first.Id));
        Assert.NotNull(stream.TryExpireRequest(second.Id));
        Assert.Equal(ScreenControlState.Off, stream.ControlState);
    }

    [Fact]
    public void TryExpireRequest_DoesNothingOnceControlIsOn()
    {
        var stream = NewStream();
        var pc = NewSocket();
        stream.TryAttachPc(pc, stream.ViewId, out _);
        var request = stream.TryBeginControlRequest()!.Value;
        stream.TryConfirmControl(pc, 9);

        Assert.Null(stream.TryExpireRequest(request.Id));
        Assert.Equal(ScreenControlState.On, stream.ControlState);
    }

    [Fact]
    public void TryEndControl_HandsBackTheAuditRowAndThePc_WhileThePcIsAttached()
    {
        var stream = NewStream();
        var pc = NewSocket();
        stream.TryAttachPc(pc, stream.ViewId, out _);
        stream.TryBeginControlRequest();
        stream.TryConfirmControl(pc, 9);

        var ended = stream.TryEndControl();

        Assert.Equal(9, ended!.Value.AuditId);
        Assert.Same(pc, ended.Value.AttachedPc);
        Assert.Equal(ScreenControlState.Off, stream.ControlState);
        Assert.Null(stream.TryEndControl());
    }

    [Fact]
    public void TryEndControl_AfterThePcWasReplaced_DoesNotHandBackTheOldPc()
    {
        var stream = NewStream();
        var first = NewSocket();
        stream.TryAttachPc(first, stream.ViewId, out _);
        stream.TryBeginControlRequest();
        stream.TryConfirmControl(first, 9);
        stream.TryAttachPc(NewSocket(), stream.ViewId, out _);

        var ended = stream.TryEndControl();

        Assert.Null(ended!.Value.AttachedPc);
        Assert.Equal(9, ended.Value.AuditId);
    }

    [Fact]
    public void TryEndControl_AfterThePcDetached_DoesNotHandBackThePc()
    {
        var stream = NewStream();
        var pc = NewSocket();
        stream.TryAttachPc(pc, stream.ViewId, out _);
        stream.TryBeginControlRequest();
        stream.TryDetachPc(pc);

        var ended = stream.TryEndControl();

        Assert.NotNull(ended);
        Assert.Null(ended!.Value.AttachedPc);
        Assert.Null(ended.Value.AuditId);
    }

    private static readonly BrowserControlMessage MoveInput = new(BrowserControlKind.Move, "{}", InputRate.Move);
    private static readonly BrowserControlMessage WheelInput = new(BrowserControlKind.Input, "{}", InputRate.Other);
    private static readonly BrowserControlMessage ReleaseAllInput = new(BrowserControlKind.Input, "{}", InputRate.Exempt, HeldEffect.ReleaseAll);

    private static BrowserControlMessage PressInput(string key) => new(BrowserControlKind.Input, "{}", InputRate.Other, HeldEffect.Press, key);
    private static BrowserControlMessage ReleaseInput(string key) => new(BrowserControlKind.Input, "{}", InputRate.Exempt, HeldEffect.Release, key);

    // A stream with control on, held by the returned PC.
    private static ScreenStream ConfirmedStream(out ScreenSocket pc)
    {
        var stream = NewStream();
        pc = NewSocket();
        stream.TryAttachPc(pc, stream.ViewId, out _);
        stream.TryBeginControlRequest();
        stream.TryConfirmControl(pc, 9);
        return stream;
    }

    [Fact]
    public void TakeControlInputTarget_OnlyWhileOn_ForTheAttachedPc()
    {
        var stream = NewStream();
        var pc = NewSocket();
        var now = DateTimeOffset.UtcNow;
        stream.TryAttachPc(pc, stream.ViewId, out _);
        Assert.Null(stream.TakeControlInputTarget(MoveInput, now));

        stream.TryBeginControlRequest();
        Assert.Null(stream.TakeControlInputTarget(MoveInput, now));

        stream.TryConfirmControl(pc, 9);
        Assert.Same(pc, stream.TakeControlInputTarget(MoveInput, now));

        stream.TryDetachPc(pc);
        Assert.Null(stream.TakeControlInputTarget(MoveInput, now));
    }

    [Fact]
    public void TakeControlInputTarget_CapsMovesAndOtherInputSeparately()
    {
        var stream = ConfirmedStream(out _);
        var now = DateTimeOffset.UtcNow;

        var moves = Enumerable.Range(0, ScreenProtocol.MaxMovesPerSecond + 10)
            .Count(_ => stream.TakeControlInputTarget(MoveInput, now) is not null);
        var wheels = Enumerable.Range(0, ScreenProtocol.MaxOtherInputPerSecond + 10)
            .Count(_ => stream.TakeControlInputTarget(WheelInput, now) is not null);

        Assert.Equal(ScreenProtocol.MaxMovesPerSecond, moves);
        Assert.Equal(ScreenProtocol.MaxOtherInputPerSecond, wheels);
    }

    [Fact]
    public void TakeControlInputTarget_CapsTextOnItsOwn_IndependentOfMovesAndOtherInput()
    {
        var stream = ConfirmedStream(out _);
        var now = DateTimeOffset.UtcNow;
        var textInput = new BrowserControlMessage(BrowserControlKind.Input, "{}", InputRate.Text);
        for (var attempt = 0; attempt < ScreenProtocol.MaxOtherInputPerSecond; attempt++)
        {
            stream.TakeControlInputTarget(WheelInput, now);
        }

        var texts = Enumerable.Range(0, ScreenProtocol.MaxTextPerSecond + 5)
            .Count(_ => stream.TakeControlInputTarget(textInput, now) is not null);

        Assert.Equal(40, ScreenProtocol.MaxTextPerSecond);
        Assert.Equal(ScreenProtocol.MaxTextPerSecond, texts);
        Assert.Null(stream.TakeControlInputTarget(WheelInput, now));
        Assert.NotNull(stream.TakeControlInputTarget(MoveInput, now));
        Assert.NotNull(stream.TakeControlInputTarget(textInput, now.AddSeconds(1)));
    }

    [Fact]
    public void TakeControlInputTarget_AnUpOrReleaseIsRelayedOnlyForSomethingHeld()
    {
        var stream = ConfirmedStream(out _);
        var now = DateTimeOffset.UtcNow;

        Assert.Null(stream.TakeControlInputTarget(ReleaseInput("kKeyA"), now));
        Assert.Null(stream.TakeControlInputTarget(ReleaseAllInput, now));
        Assert.NotNull(stream.TakeControlInputTarget(PressInput("kKeyA"), now));
        Assert.NotNull(stream.TakeControlInputTarget(ReleaseInput("kKeyA"), now));
        Assert.Null(stream.TakeControlInputTarget(ReleaseInput("kKeyA"), now));
    }

    [Fact]
    public void TakeControlInputTarget_ReleaseAll_IsRelayedOnlyWhileSomethingIsHeld_AndClearsIt()
    {
        var stream = ConfirmedStream(out _);
        var now = DateTimeOffset.UtcNow;
        stream.TakeControlInputTarget(PressInput("kKeyA"), now);
        stream.TakeControlInputTarget(PressInput("b0"), now);

        Assert.NotNull(stream.TakeControlInputTarget(ReleaseAllInput, now));
        Assert.Null(stream.TakeControlInputTarget(ReleaseAllInput, now));
        Assert.Null(stream.TakeControlInputTarget(ReleaseInput("kKeyA"), now));
    }

    [Fact]
    public void TakeControlInputTarget_ExemptInputIsNotCappedByTheMoveOrOtherCaps()
    {
        var stream = ConfirmedStream(out _);
        var now = DateTimeOffset.UtcNow;
        stream.TakeControlInputTarget(PressInput("kKeyA"), now);
        for (var attempt = 0; attempt < ScreenProtocol.MaxOtherInputPerSecond; attempt++)
        {
            stream.TakeControlInputTarget(WheelInput, now);
        }

        Assert.Null(stream.TakeControlInputTarget(WheelInput, now));
        Assert.NotNull(stream.TakeControlInputTarget(ReleaseInput("kKeyA"), now));
    }

    [Fact]
    public void TakeControlInputTarget_HeldKeysAreBounded_AndAPressBeyondTheBoundIsDropped()
    {
        var stream = ConfirmedStream(out _);
        var now = DateTimeOffset.UtcNow;

        // Presses are capped at 200 a second, so the bound is reached over two seconds.
        for (var index = 0; index < ScreenProtocol.MaxHeldInputs; index++)
        {
            Assert.NotNull(stream.TakeControlInputTarget(PressInput($"k{index}"), now.AddSeconds(index / 200)));
        }

        var later = now.AddSeconds(2);
        Assert.Null(stream.TakeControlInputTarget(PressInput("kOverflow"), later));
        Assert.NotNull(stream.TakeControlInputTarget(PressInput("k0"), later));
    }

    [Fact]
    public void EndingControl_ClearsWhatWasHeld()
    {
        var stream = ConfirmedStream(out _);
        var now = DateTimeOffset.UtcNow;
        stream.TakeControlInputTarget(PressInput("kKeyA"), now);
        stream.TryEndControl();

        var pc = NewSocket();
        stream.TryAttachPc(pc, stream.ViewId, out _);
        stream.TryBeginControlRequest();
        stream.TryConfirmControl(pc, 10);

        Assert.Null(stream.TakeControlInputTarget(ReleaseInput("kKeyA"), now));
    }

    [Fact]
    public void TryEndRequestFrom_OnlyEndsARequestForThatPc()
    {
        var stream = NewStream();
        var asked = NewSocket();
        var stranger = NewSocket();
        stream.TryAttachPc(asked, stream.ViewId, out _);
        stream.TryBeginControlRequest();

        Assert.Null(stream.TryEndRequestFrom(stranger));
        Assert.NotNull(stream.TryEndRequestFrom(asked));
        Assert.Equal(ScreenControlState.Off, stream.ControlState);
    }

    [Fact]
    public void EndControlOnClose_HandsBackTheAuditRow_AndNothingCanStartAfterwards()
    {
        var stream = NewStream();
        var pc = NewSocket();
        stream.TryAttachPc(pc, stream.ViewId, out _);
        stream.TryBeginControlRequest();
        stream.TryConfirmControl(pc, 9);
        var onlyViewer = stream.OnlyViewer();
        stream.CloseOnlyViewer();

        Assert.Equal(9, stream.EndControlOnClose());
        Assert.Equal(ScreenControlState.Off, stream.ControlState);
        Assert.Null(stream.EndControlOnClose());
        Assert.Null(stream.TryBeginControlRequest(onlyViewer).Request);
    }

    [Fact]
    public void TryConfirmControl_AfterClose_IsRefused()
    {
        var stream = NewStream();
        var pc = NewSocket();
        stream.TryAttachPc(pc, stream.ViewId, out _);
        stream.TryBeginControlRequest();
        stream.CloseOnlyViewer();

        Assert.False(stream.IsAwaitingConfirmation(pc));
        Assert.False(stream.TryConfirmControl(pc, 9));
    }
}
