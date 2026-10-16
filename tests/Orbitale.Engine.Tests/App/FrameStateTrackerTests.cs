using Orbitale.App.ViewModels;

namespace Orbitale.Engine.Tests.App;

public sealed class FrameStateTrackerTests
{
    sealed class Frame : IFrameStatus
    {
        public FrameState State { get; set; }
        public int TrailCount { get; set; }
    }

    static Frame[] Frames(int n) => Enumerable.Range(0, n).Select(_ => new Frame()).ToArray();

    [Fact]
    public void Reset_makes_every_frame_pending_without_trails_but_keeps_skipped_frames()
    {
        var f = Frames(3);
        f[0].State = FrameState.DoneWithTrails; f[0].TrailCount = 4;
        f[1].State = FrameState.Skipped;
        f[2].State = FrameState.Current; f[2].TrailCount = 1;

        new FrameStateTracker(f).Reset();

        Assert.Equal([FrameState.Pending, FrameState.Skipped, FrameState.Pending], f.Select(x => x.State));
        Assert.All(f, x => Assert.Equal(0, x.TrailCount));
    }

    [Fact]
    public void Each_processed_frame_becomes_current_and_the_previous_one_done_or_done_with_trails()
    {
        var f = Frames(3);
        var tracker = new FrameStateTracker(f);

        Assert.Same(f[0], tracker.Advance(0, 2));
        Assert.Equal(FrameState.Current, f[0].State);
        Assert.Equal(2, f[0].TrailCount);

        tracker.Advance(1, 0);
        Assert.Equal(FrameState.DoneWithTrails, f[0].State);
        Assert.Equal(FrameState.Current, f[1].State);

        tracker.Advance(2, 0);
        Assert.Equal(FrameState.Done, f[1].State);

        tracker.Finish();
        Assert.Equal([FrameState.DoneWithTrails, FrameState.Done, FrameState.Done], f.Select(x => x.State));
        Assert.Equal([2, 0, 0], f.Select(x => x.TrailCount));
    }

    [Fact]
    public void Reverse_order_follows_the_file_index_not_the_report_order()
    {
        var f = Frames(4);
        var tracker = new FrameStateTracker(f);

        tracker.Advance(3, 1);
        tracker.Advance(2, 0);

        Assert.Equal([FrameState.Pending, FrameState.Pending, FrameState.Current, FrameState.DoneWithTrails], f.Select(x => x.State));
    }

    [Fact]
    public void A_stop_finishes_the_current_frame_and_leaves_the_rest_pending()
    {
        var f = Frames(4);
        var tracker = new FrameStateTracker(f);
        tracker.Advance(0, 0);
        tracker.Advance(1, 3);

        tracker.Finish();

        Assert.Equal([FrameState.Done, FrameState.DoneWithTrails, FrameState.Pending, FrameState.Pending], f.Select(x => x.State));
        Assert.Null(tracker.Current);
    }

    [Fact]
    public void Out_of_range_indices_and_skipped_frames_are_left_alone()
    {
        var f = Frames(2);
        f[1].State = FrameState.Skipped;
        var tracker = new FrameStateTracker(f);
        tracker.Advance(0, 0);

        Assert.Null(tracker.Advance(7, 1));
        Assert.Null(tracker.Advance(1, 1));

        Assert.Equal(FrameState.Done, f[0].State);   // the previous current is still finished
        Assert.Equal(FrameState.Skipped, f[1].State);
        Assert.Equal(0, f[1].TrailCount);
    }
}
