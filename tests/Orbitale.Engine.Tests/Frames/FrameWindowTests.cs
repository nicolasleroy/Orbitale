using OpenCvSharp;
using Orbitale.Engine.Frames;

namespace Orbitale.Engine.Tests.Frames;

public class FrameWindowTests
{
    static readonly string[] Samples3 = { FrameSequenceTests.Sample(1), FrameSequenceTests.Sample(2), FrameSequenceTests.Sample(3) };

    [Fact] public void EvictsBeyondCapacityAndDisposes()
    {
        using var w = new FrameWindow(Samples3, capacity: 2);
        var a = w.Get(0); w.Get(1); w.Get(2);
        Assert.True(a.IsDisposed);
    }

    [Fact] public void CachedFrameIsReturnedWithoutRedecode()
    {
        using var w = new FrameWindow(Samples3, 3);
        Assert.Same(w.Get(1), w.Get(1));
    }

    [Fact] public void RecentlyUsedFrameSurvivesEviction()
    {
        using var w = new FrameWindow(Samples3, 2);
        var a = w.Get(0); w.Get(1); w.Get(0); w.Get(2);
        Assert.False(a.IsDisposed);
    }

    [Fact] public void PrefetchedFrameIsReturned()
    {
        using var w = new FrameWindow(Samples3, 4);
        w.Prefetch(1);
        var m = w.Get(1);
        Assert.Equal(2000, m.Width);
    }

    [Fact] public void PrefetchOutOfRangeIsIgnored()
    {
        using var w = new FrameWindow(Samples3, 4);
        w.Prefetch(-1); w.Prefetch(3);
        Assert.Equal(2000, w.Get(0).Width);
    }

    [Fact] public void DisposeReleasesCachedFrames()
    {
        var w = new FrameWindow(Samples3, 4);
        var a = w.Get(0);
        w.Dispose();
        Assert.True(a.IsDisposed);
    }

    [Fact] public void PrefetchCompletingAfterDisposeDisposesItsMat()
    {
        using var started = new ManualResetEventSlim();
        using var gate = new ManualResetEventSlim();
        Mat? produced = null;
        Mat Decode(string _)
        {
            started.Set();
            Assert.True(gate.Wait(TimeSpan.FromSeconds(10)));
            return produced = new Mat(2, 2, MatType.CV_32FC3);
        }
        var w = new FrameWindow(Samples3, 4, Decode);
        w.Prefetch(1);
        Assert.True(started.Wait(TimeSpan.FromSeconds(10)));
        var pending = w.PendingPrefetch!;
        w.Dispose();
        gate.Set();
        Assert.True(pending.Wait(TimeSpan.FromSeconds(10)));
        Assert.NotNull(produced);
        Assert.True(produced!.IsDisposed);
    }
}
