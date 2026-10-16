namespace Orbitale.App.ViewModels;

public enum FrameState { Pending, Current, Done, DoneWithTrails, Skipped }

/// <summary>What the tracker reads and sets on a frame of the strip.</summary>
public interface IFrameStatus
{
    FrameState State { get; set; }

    int TrailCount { get; set; }
}

/// <summary>
/// The strip's frame states during a run. Frames are indexed like the run's <c>FrameSequence.Files</c>; the engine
/// reports each processed frame by that index, in any order (reversed with "comet head at start"). The last reported
/// frame is Current; when the next one arrives (or the run ends) it becomes Done, or DoneWithTrails if trails were
/// removed from it. Skipped frames are never touched. UI-free so it can be unit-tested; call it on the UI thread.
/// </summary>
public sealed class FrameStateTracker(IReadOnlyList<IFrameStatus> frames)
{
    public IFrameStatus? Current { get; private set; }

    /// <summary>Every frame back to Pending without a trail count, except skipped ones.</summary>
    public void Reset()
    {
        Current = null;
        foreach (var f in frames)
        {
            f.TrailCount = 0;
            if (f.State != FrameState.Skipped) f.State = FrameState.Pending;
        }
    }

    /// <summary>Frame <paramref name="fileIndex"/> was processed with <paramref name="trails"/> trails removed. Returns it, or null if there is no such frame to show.</summary>
    public IFrameStatus? Advance(int fileIndex, int trails)
    {
        Finish();
        if ((uint)fileIndex >= (uint)frames.Count) return null;
        var frame = frames[fileIndex];
        if (frame.State == FrameState.Skipped) return null;
        frame.TrailCount = trails;
        frame.State = FrameState.Current;
        return Current = frame;
    }

    /// <summary>The current frame is done (the next one started, or the run ended).</summary>
    public void Finish()
    {
        if (Current is null) return;
        Current.State = Current.TrailCount > 0 ? FrameState.DoneWithTrails : FrameState.Done;
        Current = null;
    }
}
