using LabGuard.Server.Screen;

namespace LabGuard.Server.Tests;

// One browser's view: the state it was last sent, its close and its share of control requests. The PC side is in ScreenStreamTests.
public class ScreenViewerTests
{
    private static ScreenViewer NewViewer() => SingleViewerStream.New().OnlyViewer();

    [Fact]
    public void TrySetState_StartsAtWaiting_AndIsTrueOnlyWhenTheStateChanges()
    {
        var viewer = NewViewer();

        Assert.False(viewer.TrySetState(ScreenProtocol.WaitingState));
        Assert.True(viewer.TrySetState(ScreenProtocol.LiveState));
        Assert.False(viewer.TrySetState(ScreenProtocol.LiveState));
        Assert.True(viewer.TrySetState(ScreenProtocol.PausedState));
    }

    [Fact]
    public void TryClose_IsOnlyTrueOnce_AndNoStateIsSetAfterwards()
    {
        var viewer = NewViewer();

        Assert.True(viewer.TryClose());
        Assert.False(viewer.TryClose());
        Assert.True(viewer.IsClosed);
        Assert.False(viewer.TrySetState(ScreenProtocol.LiveState));
    }

    [Fact]
    public void ControlState_IsTheStreamsOnlyForTheViewerThatAskedForControl()
    {
        var stream = SingleViewerStream.New();
        var asking = stream.OnlyViewer();
        var watching = SingleViewerStream.NewViewer(stream, adminId: 2);
        stream.TryJoin(watching);
        stream.TryAttachPc(new ScreenSocket(new FakeWebSocket(), TimeSpan.FromSeconds(1)), stream.ViewId, out _);

        stream.TryBeginControlRequest(asking);

        Assert.Equal(ScreenControlState.Requesting, asking.ControlState);
        Assert.Equal(ScreenControlState.Off, watching.ControlState);
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
    public void CancelLifetime_CancelsEverySourceLinkedToIt_AndDisposeStopsNewLinks()
    {
        var viewer = NewViewer();
        using var linked = viewer.LinkLifetime(CancellationToken.None);

        viewer.CancelLifetime();
        viewer.Dispose();

        Assert.True(linked!.IsCancellationRequested);
        Assert.Null(viewer.LinkLifetime(CancellationToken.None));
    }
}
