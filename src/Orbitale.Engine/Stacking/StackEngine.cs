using Orbitale.Engine.Frames;

namespace Orbitale.Engine.Stacking;

/// <summary>Per-frame stage durations of a run, for profiling (the CLI harness prints them).</summary>
internal sealed record FrameTimings(int FileIndex, double DecodeMs, double CleanMs, double GapFillMs, double AccumulateMs, double PreviewMs);

/// <summary>
/// Stacks a frame sequence: for each frame, in file order (reversed with <see cref="StackSettings.HeadAtStart"/>),
/// decode, remove trails against its neighbours by file index, gap-fill against the previously processed frame, blend,
/// and report progress with a preview of the running result.
/// </summary>
public sealed class StackEngine
{
    /// <summary>Optional stage timings, called on the engine thread after each blended frame.</summary>
    internal Action<FrameTimings>? Timings { get; init; }

    /// <summary>
    /// Runs on a dedicated long-running task. Progress is reported on that task's thread once per frame; the callback
    /// must not block. Cancellation is checked before each frame and between stages; a cancelled run returns a result
    /// with <see cref="StackResult.Completed"/> false holding the partial stack (it never throws
    /// <see cref="OperationCanceledException"/>). Frames that fail to decode are skipped with a notice.
    /// </summary>
    public Task<StackResult> RunAsync(FrameSequence seq, StackSettings settings, IProgress<StackProgress> progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(seq);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(progress);
        var run = new StackRun(seq, settings, progress, ct, Timings);
        return Task.Factory.StartNew(run.Run, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }
}
