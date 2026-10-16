namespace Orbitale.App.Services;

/// <summary>The UI thread's queue, as the view model needs it (the app wraps the window's DispatcherQueue).</summary>
public interface IUiDispatcher
{
    bool HasThreadAccess { get; }

    /// <summary>Queues <paramref name="action"/> on the UI thread; false when the queue is shutting down.</summary>
    bool TryEnqueue(Action action);
}

/// <summary>
/// Progress from a worker thread to the UI thread that a slow UI can never fall behind: at most one drain is queued
/// at a time, and it applies only the <b>latest</b> report (the heavy part, e.g. the preview image) together with a
/// small <b>step</b> taken from <b>every</b> report since the previous drain (e.g. a frame's state change), in order.
/// <see cref="Report"/> may be called from any thread; <c>apply</c>, <see cref="Flush"/> and <see cref="Close"/> run on
/// the UI thread. UI-free so it can be unit-tested.
/// </summary>
public sealed class CoalescingProgress<T, TStep> : IProgress<T> where T : class
{
    readonly IUiDispatcher _ui;
    readonly Func<T, TStep> _step;
    readonly Action<IReadOnlyList<TStep>, T> _apply;
    readonly Lock _sync = new();
    List<TStep> _steps = [];
    T? _latest;
    bool _scheduled, _closed;

    public CoalescingProgress(IUiDispatcher ui, Func<T, TStep> step, Action<IReadOnlyList<TStep>, T> apply)
    {
        _ui = ui;
        _step = step;
        _apply = apply;
    }

    public void Report(T value)
    {
        var step = _step(value);
        lock (_sync)
        {
            if (_closed) return;
            _steps.Add(step);
            _latest = value;
            if (_scheduled) return;
            _scheduled = true;
        }
        if (!_ui.TryEnqueue(Drain))
        {
            // The UI is shutting down (or refused): let the next report try again.
            lock (_sync) _scheduled = false;
        }
    }

    /// <summary>Applies what is pending now (UI thread), e.g. before handling the end of the run.</summary>
    public void Flush() => Drain();

    /// <summary>Drops what is pending and ignores later reports (UI thread): the run is over or replaced.</summary>
    public void Close()
    {
        lock (_sync)
        {
            _closed = true;
            _steps = [];
            _latest = null;
        }
    }

    void Drain()
    {
        List<TStep> steps;
        T? latest;
        lock (_sync)
        {
            _scheduled = false;
            if (_closed || _latest is null) return;
            steps = _steps;
            latest = _latest;
            _steps = [];
            _latest = null;
        }
        _apply(steps, latest);
    }
}
