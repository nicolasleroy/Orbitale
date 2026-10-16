using Orbitale.Engine.Frames;

namespace Orbitale.Engine.Stacking;

public enum SessionState { Idle, Running, Stopping, Finished, Stopped }

/// <summary>
/// The UI's view of the engine: Idle → Running → Finished, or Running → Stopping → Stopped on <see cref="Stop"/>.
/// Start is allowed in Idle, Finished and Stopped; it disposes the previous result and always restarts from the first
/// frame. Save is allowed in Finished and Stopped. <see cref="StateChanged"/> is raised on the thread that changed the
/// state: the caller's (Start, Stop, and the end of <see cref="StartAsync"/>, which resumes on the caller's context).
/// </summary>
public sealed class StackSession
{
    readonly StackEngine _engine;
    volatile SessionState _state = SessionState.Idle;
    CancellationTokenSource? _cts;

    public StackSession() : this(new StackEngine()) { }

    internal StackSession(StackEngine engine) => _engine = engine;

    public SessionState State => _state;

    public event Action? StateChanged;

    public bool CanStart => _state is SessionState.Idle or SessionState.Finished or SessionState.Stopped;
    public bool CanStop => _state == SessionState.Running;
    public bool CanSave => _state is SessionState.Finished or SessionState.Stopped && Result is not null;

    /// <summary>The last run's result, owned by the session (disposed on the next Start).</summary>
    public StackResult? Result { get; private set; }

    /// <summary>
    /// Runs the engine to completion or until <see cref="Stop"/>. Throws <see cref="InvalidOperationException"/> unless
    /// <see cref="CanStart"/>. If the engine fails, the state returns to Idle and the exception propagates.
    /// </summary>
    public async Task StartAsync(FrameSequence seq, StackSettings settings, IProgress<StackProgress> progress)
    {
        if (!CanStart) throw new InvalidOperationException($"Cannot start while {_state}.");
        Result?.Dispose();
        Result = null;
        using var cts = new CancellationTokenSource();
        _cts = cts;
        SetState(SessionState.Running);
        try
        {
            var result = await _engine.RunAsync(seq, settings, progress, cts.Token);
            Result = result;
            SetState(result.Completed ? SessionState.Finished : SessionState.Stopped);
        }
        catch
        {
            SetState(SessionState.Idle);
            throw;
        }
        finally
        {
            _cts = null;
        }
    }

    /// <summary>Requests a stop (only while Running): the state becomes Stopping, then Stopped when the engine returns.</summary>
    public void Stop()
    {
        if (_state != SessionState.Running) return;
        SetState(SessionState.Stopping);
        try { _cts?.Cancel(); } catch (ObjectDisposedException) { /* the run ended meanwhile */ }
    }

    void SetState(SessionState s)
    {
        _state = s;
        StateChanged?.Invoke();
    }
}
