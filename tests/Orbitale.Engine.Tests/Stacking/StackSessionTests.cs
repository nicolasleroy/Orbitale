using Orbitale.Engine.Frames;
using Orbitale.Engine.Stacking;
using Orbitale.Engine.Tests.Support;
using static Orbitale.Engine.Tests.Stacking.StackFiles;

namespace Orbitale.Engine.Tests.Stacking;

public class StackSessionTests
{
    [Fact] public async Task StateMachine()
    {
        var seq = FrameSequence.Build(SyntheticFiles(8));
        var session = new StackSession();
        var states = new List<SessionState>();
        session.StateChanged += () => states.Add(session.State);

        Assert.Equal(SessionState.Idle, session.State);
        Assert.True(session.CanStart); Assert.False(session.CanStop); Assert.False(session.CanSave);
        Assert.Null(session.Result);

        // First run: stopped from inside the 2nd progress callback (the engine thread).
        var during = new List<(SessionState State, bool CanStart, bool CanStop, bool CanSave)>();
        var reports = new List<StackProgress>();
        await session.StartAsync(seq, new StackSettings(), new SyncProgress<StackProgress>(p =>
        {
            reports.Add(p);
            during.Add((session.State, session.CanStart, session.CanStop, session.CanSave));
            if (reports.Count == 2) session.Stop();
            during.Add((session.State, session.CanStart, session.CanStop, session.CanSave));
        }));
        Assert.Equal((SessionState.Running, false, true, false), during[0]);
        Assert.Equal((SessionState.Stopping, false, false, false), during[^1]);
        Assert.Equal(new[] { SessionState.Running, SessionState.Stopping, SessionState.Stopped }, states);
        Assert.Equal(SessionState.Stopped, session.State);
        Assert.True(session.CanSave); Assert.True(session.CanStart); Assert.False(session.CanStop);
        var first = session.Result!;
        Assert.False(first.Completed);
        Assert.Equal(2, first.FramesProcessed);
        Assert.Equal(2, reports.Count);

        // Stop outside Running does nothing.
        session.Stop();
        Assert.Equal(SessionState.Stopped, session.State);

        // Second run restarts from frame 1, disposes the previous result, and finishes.
        reports.Clear();
        await session.StartAsync(seq, new StackSettings(), new SyncProgress<StackProgress>(reports.Add));
        Assert.True(first.Image.IsDisposed);
        Assert.Equal(1, reports[0].Processed);
        Assert.Equal(8, reports.Count);
        Assert.Equal(SessionState.Finished, session.State);
        Assert.True(session.CanSave); Assert.True(session.CanStart); Assert.False(session.CanStop);
        Assert.True(session.Result!.Completed);
    }

    [Fact] public async Task StartWhileRunningThrows()
    {
        var seq = FrameSequence.Build(SyntheticFiles(4));
        var session = new StackSession();
        Task? inner = null;
        await session.StartAsync(seq, new StackSettings(), new SyncProgress<StackProgress>(p =>
        {
            if (p.Processed == 1) inner = session.StartAsync(seq, new StackSettings(), new SyncProgress<StackProgress>(_ => { }));
        }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => inner!);
        Assert.Equal(SessionState.Finished, session.State);
    }
}
