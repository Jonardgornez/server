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
    public void TryTakeFrameCredit_AllowsTheWindowAndNoMore()
    {
        var viewer = NewViewer();

        Assert.True(viewer.TryTakeFrameCredit());
        Assert.True(viewer.TryTakeFrameCredit());
        Assert.False(viewer.TryTakeFrameCredit());
    }

    [Fact]
    public void ReturnFrameCredit_FreesOneSlot_AndNeverGoesBelowZero()
    {
        var viewer = NewViewer();
        viewer.ReturnFrameCredit();
        viewer.TryTakeFrameCredit();
        viewer.TryTakeFrameCredit();

        viewer.ReturnFrameCredit();

        Assert.True(viewer.TryTakeFrameCredit());
        Assert.False(viewer.TryTakeFrameCredit());
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
        Assert.Same(second, viewer.CurrentPc);
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
    public void AfterClose_NothingCanAttachOrTakeCredit()
    {
        var viewer = NewViewer();
        viewer.TryClose(out _);

        Assert.False(viewer.TryAttachPc(NewSocket(), out _));
        Assert.False(viewer.TryTakeFrameCredit());
        Assert.Null(viewer.CurrentPc);
    }

    [Fact]
    public void CancelLifetime_CancelsTheTokenThatStopsTheRelay()
    {
        var viewer = NewViewer();
        Assert.False(viewer.Lifetime.IsCancellationRequested);

        viewer.CancelLifetime();

        Assert.True(viewer.Lifetime.IsCancellationRequested);
    }
}
