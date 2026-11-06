using LabGuard.Server.Screen;

namespace LabGuard.Server.Tests;

public class ScreenViewerTests
{
    private const string ViewId = "0123456789abcdef0123456789abcdef";

    private static ScreenViewer NewViewer(string viewId = ViewId) =>
        new(viewId, pcId: 7, mode: ScreenProtocol.NotifiedMode, auditId: 1, DateTimeOffset.UtcNow.AddMinutes(30),
            new ScreenSocket(new FakeWebSocket(), TimeSpan.FromSeconds(1)));

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
}
