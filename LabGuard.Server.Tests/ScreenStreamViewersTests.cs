using LabGuard.Server.Screen;

namespace LabGuard.Server.Tests;

// The stream's rules for several viewers: joining within the cap, the mode they make, the frame credits they share, leaving, and
// control held by one of them. The hub's sends around these are in ScreenStreamHubViewersTests.
public class ScreenStreamViewersTests
{
    private static ScreenSocket NewSocket() => new(new FakeWebSocket(), TimeSpan.FromSeconds(1));

    private static ScreenViewer Join(ScreenStream stream, string mode = ScreenProtocol.NotifiedMode, bool isPassive = false)
    {
        var viewer = SingleViewerStream.NewViewer(stream, mode, isPassive: isPassive);
        Assert.Equal(StreamJoinStatus.Joined, stream.TryJoin(viewer)!.Status);
        return viewer;
    }

    private static (ScreenStream Stream, ScreenViewer First, ScreenViewer Second, ScreenSocket Pc) TwoViewersLive()
    {
        var stream = SingleViewerStream.New();
        var first = stream.OnlyViewer();
        var second = Join(stream);
        var pc = NewSocket();
        stream.TryAttachPc(pc, stream.ViewId, out _);
        return (stream, first, second, pc);
    }

    // Joining

    [Fact]
    public void TryJoin_UpToTheCap_ThenAPassiveViewerIsBusy()
    {
        var stream = SingleViewerStream.New();
        for (var index = 1; index < ScreenProtocol.MaxViewersPerPc; index++)
        {
            Assert.False(stream.IsFull);
            Join(stream);
        }

        var late = SingleViewerStream.NewViewer(stream, isPassive: true);

        Assert.True(stream.IsFull);
        Assert.Same(StreamJoin.Busy, stream.TryJoin(late));
        Assert.DoesNotContain(late, stream.Viewers);
    }

    [Fact]
    public void TryJoin_OfAFullView_WhenFull_MarksTheOldestPassiveViewerAsLeaving()
    {
        var stream = SingleViewerStream.New();
        var oldestPassive = Join(stream, isPassive: true);
        var newerPassive = Join(stream, isPassive: true);
        Join(stream);
        var full = SingleViewerStream.NewViewer(stream);

        var join = stream.TryJoin(full)!;

        Assert.Equal(StreamJoinStatus.Joined, join.Status);
        Assert.Same(oldestPassive, join.Replaced);
        Assert.DoesNotContain(oldestPassive, stream.Viewers);
        Assert.Contains(newerPassive, stream.Viewers);
        Assert.Contains(full, stream.Viewers);
    }

    [Fact]
    public void TryJoin_OfAFullView_WhenFullOfFullViews_IsBusy()
    {
        var stream = SingleViewerStream.New();
        for (var index = 1; index < ScreenProtocol.MaxViewersPerPc; index++)
        {
            Join(stream);
        }

        Assert.Same(StreamJoin.Busy, stream.TryJoin(SingleViewerStream.NewViewer(stream)));
    }

    [Fact]
    public void TryJoin_DoesNotCountAViewerThatIsClosing()
    {
        var stream = SingleViewerStream.New();
        var closing = stream.OnlyViewer();
        for (var index = 1; index < ScreenProtocol.MaxViewersPerPc; index++)
        {
            Join(stream);
        }

        closing.TryClose();

        Assert.False(stream.IsFull);
        Assert.Equal(StreamJoinStatus.Joined, stream.TryJoin(SingleViewerStream.NewViewer(stream, isPassive: true))!.Status);
    }

    // The mode

    [Fact]
    public void ANotifiedViewerJoiningAnAnonymousStream_MakesItNotified_TakesANewViewId_AndHandsBackThePc()
    {
        var stream = SingleViewerStream.New(ScreenProtocol.AnonymousMode);
        var pc = NewSocket();
        stream.TryAttachPc(pc, stream.ViewId, out _);
        var anonymousViewId = stream.ViewId;

        var join = stream.TryJoin(SingleViewerStream.NewViewer(stream))!;

        Assert.Same(pc, join.DroppedPc);
        Assert.Null(stream.CurrentPc);
        Assert.Equal(ScreenProtocol.NotifiedMode, stream.Mode);
        Assert.NotEqual(anonymousViewId, stream.ViewId);
    }

    [Fact]
    public void AnAnonymousViewerJoiningANotifiedStream_ChangesNothing()
    {
        var stream = SingleViewerStream.New();
        var pc = NewSocket();
        stream.TryAttachPc(pc, stream.ViewId, out _);
        var viewId = stream.ViewId;

        var join = stream.TryJoin(SingleViewerStream.NewViewer(stream, ScreenProtocol.AnonymousMode))!;

        Assert.Null(join.DroppedPc);
        Assert.Same(pc, stream.CurrentPc);
        Assert.Equal(new ScreenStreamView(viewId, ScreenProtocol.NotifiedMode), stream.Describe());
    }

    [Fact]
    public void RemoveViewer_OfTheLastNotifiedViewer_MakesTheStreamAnonymous_AndKeepsThePc()
    {
        var stream = SingleViewerStream.New();
        var notified = stream.OnlyViewer();
        Join(stream, ScreenProtocol.AnonymousMode);
        var pc = NewSocket();
        stream.TryAttachPc(pc, stream.ViewId, out _);
        var viewId = stream.ViewId;

        notified.TryClose();
        var leave = stream.RemoveViewer(notified);

        Assert.False(leave.IsLast);
        Assert.Equal(ScreenProtocol.AnonymousMode, stream.Mode);
        Assert.NotEqual(viewId, stream.ViewId);
        Assert.Same(pc, stream.CurrentPc);
    }

    // Frames

    [Fact]
    public void TryTakeFrameCredit_GoesToEveryViewerWithRoom()
    {
        var (stream, first, second, pc) = TwoViewersLive();

        var relay = stream.TryTakeFrameCredit(pc)!;

        Assert.Equal(new[] { first, second }, relay.Recipients);
        Assert.False(relay.IsAckedAtOnce);
    }

    [Fact]
    public void TryReturnFrameCredit_OnlyTheFirstAckForAFrameGoesToItsSender()
    {
        var (stream, first, second, pc) = TwoViewersLive();
        stream.TryTakeFrameCredit(pc);

        Assert.True(stream.TryReturnFrameCredit(second, out var firstAck));
        Assert.True(stream.TryReturnFrameCredit(first, out var secondAck));
        Assert.False(stream.TryReturnFrameCredit(first, out _));

        Assert.Same(pc, firstAck);
        Assert.Null(secondAck);
    }

    [Fact]
    public void TryTakeFrameCredit_WithNoViewerWatching_DropsTheFrame_AndAcksItAtOnce()
    {
        var stream = SingleViewerStream.New();
        var pc = NewSocket();
        stream.TryAttachPc(pc, stream.ViewId, out _);
        stream.OnlyViewer().TryClose();

        var relay = stream.TryTakeFrameCredit(pc)!;

        Assert.Empty(relay.Recipients);
        Assert.True(relay.IsAckedAtOnce);
        Assert.NotNull(stream.TryTakeFrameCredit(pc));
        Assert.NotNull(stream.TryTakeFrameCredit(pc));
    }

    [Fact]
    public void RemoveViewer_AcksOnlyTheFramesNoOtherViewerStillHolds()
    {
        var (stream, first, second, pc) = TwoViewersLive();
        stream.TryTakeFrameCredit(pc);
        stream.TryTakeFrameCredit(pc);
        stream.TryReturnFrameCredit(second, out _);
        stream.TryReturnFrameCredit(second, out _);
        stream.TryTakeFrameCredit(pc);

        second.TryClose();
        var leave = stream.RemoveViewer(second);

        Assert.Equal(new[] { pc }, leave.Acks);
        first.TryClose();
        Assert.Empty(stream.RemoveViewer(first).Acks);
    }

    // Control

    [Fact]
    public void TryBeginControlRequest_WhileAnotherViewerAsks_IsRefusedAsInUse()
    {
        var (stream, first, second, _) = TwoViewersLive();
        stream.TryBeginControlRequest(first);

        var refused = stream.TryBeginControlRequest(second);

        Assert.Null(refused.Request);
        Assert.Equal(ControlReasons.InUse, refused.Refusal);
        Assert.Same(first, stream.Controller);
    }

    [Fact]
    public void TryBeginControlRequest_FromAViewerThatLeft_IsRefusedAsWaiting()
    {
        var (stream, first, second, _) = TwoViewersLive();
        second.TryClose();
        stream.RemoveViewer(second);

        var refused = stream.TryBeginControlRequest(second);

        Assert.Equal(ControlReasons.Waiting, refused.Refusal);
        Assert.Null(stream.Controller);
        Assert.NotNull(stream.TryBeginControlRequest(first).Request);
    }

    [Fact]
    public void RequesterAwaiting_IsTheViewerThatAsked()
    {
        var (stream, first, _, pc) = TwoViewersLive();

        stream.TryBeginControlRequest(first);

        Assert.Same(first, stream.RequesterAwaiting(pc));
        Assert.Null(stream.RequesterAwaiting(NewSocket()));
    }

    [Fact]
    public void TryEndControlOf_EndsOnlyTheControllersPeriod_AndNamesTheController()
    {
        var (stream, first, second, pc) = TwoViewersLive();
        stream.TryBeginControlRequest(first);
        stream.TryConfirmControl(pc, 9);

        Assert.Null(stream.TryEndControlOf(second));
        var ended = stream.TryEndControlOf(first)!.Value;

        Assert.Same(first, ended.Controller);
        Assert.Equal(9, ended.AuditId);
        Assert.Equal(ScreenControlState.Off, stream.ControlState);
    }

    [Fact]
    public void TakeControlInputTarget_OnlyForTheController()
    {
        var (stream, first, second, pc) = TwoViewersLive();
        var move = new BrowserControlMessage(BrowserControlKind.Move, "{}", InputRate.Move);
        stream.TryBeginControlRequest(first);
        stream.TryConfirmControl(pc, 9);

        Assert.Null(stream.TakeControlInputTarget(second, move, DateTimeOffset.UtcNow));
        Assert.Same(pc, stream.TakeControlInputTarget(first, move, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void RemoveViewer_OfTheController_LeavesControlForTheHubToEnd()
    {
        var (stream, first, _, pc) = TwoViewersLive();
        stream.TryBeginControlRequest(first);
        stream.TryConfirmControl(pc, 9);
        first.TryClose();

        var leave = stream.RemoveViewer(first);

        Assert.True(leave.IsControllerLeaving);
        Assert.Null(stream.TakeControlInputTarget(first, new BrowserControlMessage(BrowserControlKind.Move, "{}", InputRate.Move), DateTimeOffset.UtcNow));
        Assert.Same(first, stream.TryEndControlOf(first)!.Value.Controller);
    }

    // Review fixes: nothing that streams without the notice reaches a notified stream, and late acks credit nobody new

    [Fact]
    public void TryAttachPc_UnderAViewIdThatAModeChangeReplaced_IsRefused()
    {
        var stream = SingleViewerStream.New(ScreenProtocol.AnonymousMode);
        var anonymousViewId = stream.ViewId;
        stream.TryJoin(SingleViewerStream.NewViewer(stream));
        var pc = NewSocket();

        Assert.False(stream.TryAttachPc(pc, anonymousViewId, out _));
        Assert.False(stream.TryAttachPc(pc, null, out _));
        Assert.Null(stream.CurrentPc);
        Assert.Null(stream.TryTakeFrameCredit(pc));
        Assert.True(stream.TryAttachPc(pc, stream.ViewId, out _));
    }

    [Fact]
    public void ANotifiedViewerJoining_TakesOutTheStreamingHelperToo_AndEndsTheControlThroughIt()
    {
        var stream = SingleViewerStream.New(ScreenProtocol.AnonymousMode);
        var controller = stream.OnlyViewer();
        var pc = NewSocket();
        var helper = NewSocket();
        stream.TryAttachPc(pc, stream.ViewId, out _);
        stream.TryBeginControlRequest(controller);
        stream.TryConfirmControl(pc, 9);
        stream.TrySetPcPaused(pc, isPaused: true);
        stream.TrySwitchSource(helper);

        var join = stream.TryJoin(SingleViewerStream.NewViewer(stream))!;

        Assert.Same(pc, join.DroppedPc);
        Assert.Same(helper, join.StoppedHelper);
        var ended = join.EndedControl!.Value;
        Assert.Same(pc, ended.AttachedPc);
        Assert.Same(helper, ended.Helper);
        Assert.Same(controller, ended.Controller);
        Assert.Equal(9, ended.AuditId);
        Assert.Equal(ScreenControlState.Off, stream.ControlState);
        Assert.False(stream.IsCurrentSource(helper));
        Assert.Null(stream.TryTakeFrameCredit(helper));
    }

    [Fact]
    public void AfterAPcReconnects_LateAcksForTheOldConnectionsFrames_DoNotCreditTheNewOne()
    {
        var stream = SingleViewerStream.New();
        var viewer = stream.OnlyViewer();
        var first = NewSocket();
        var second = NewSocket();
        stream.TryAttachPc(first, stream.ViewId, out _);
        stream.TryTakeFrameCredit(first);
        stream.TryTakeFrameCredit(first);
        stream.TryAttachPc(second, stream.ViewId, out _);
        stream.TryTakeFrameCredit(second);

        Assert.True(stream.TryReturnFrameCredit(viewer, out var lateFirst));
        Assert.True(stream.TryReturnFrameCredit(viewer, out var lateSecond));
        Assert.True(stream.TryReturnFrameCredit(viewer, out var current));

        Assert.Null(lateFirst);
        Assert.Null(lateSecond);
        Assert.Same(second, current);
    }

    [Fact]
    public void TryBeginControlRequest_FromAViewerBeingReplaced_IsRefused()
    {
        var stream = SingleViewerStream.New();
        var replacedPassive = Join(stream, isPassive: true);
        Join(stream);
        Join(stream);
        stream.TryAttachPc(NewSocket(), stream.ViewId, out _);
        stream.TryJoin(SingleViewerStream.NewViewer(stream));

        var refused = stream.TryBeginControlRequest(replacedPassive);

        Assert.Equal(ControlReasons.Waiting, refused.Refusal);
        Assert.Null(stream.Controller);
    }

    [Fact]
    public void TakeControlInputTarget_FromAControllerThatIsClosing_IsNull()
    {
        var (stream, first, _, pc) = TwoViewersLive();
        stream.TryBeginControlRequest(first);
        stream.TryConfirmControl(pc, 9);

        first.TryClose();

        Assert.Null(stream.TakeControlInputTarget(first, new BrowserControlMessage(BrowserControlKind.Move, "{}", InputRate.Move), DateTimeOffset.UtcNow));
    }

    [Fact]
    public void ARequesterThatClosedBeforeTheConfirmation_CannotHaveControlTurnedOn()
    {
        var (stream, first, _, pc) = TwoViewersLive();
        stream.TryBeginControlRequest(first);

        first.TryClose();

        Assert.Null(stream.RequesterAwaiting(pc));
        Assert.False(stream.TryConfirmControl(pc, 9));
        Assert.Equal(ScreenControlState.Requesting, stream.ControlState);
    }
}
