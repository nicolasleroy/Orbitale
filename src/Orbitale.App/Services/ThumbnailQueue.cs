namespace Orbitale.App.Services;

/// <summary>Something that shows a thumbnail: the queue reads its path and sets its image.</summary>
public interface IThumbnailSlot<TImage> where TImage : class
{
    string Path { get; }

    TImage? Thumbnail { get; set; }
}

/// <summary>
/// Schedules thumbnail loads for a virtualised list. At most <c>maxConcurrent</c> loads run at once. A load can be
/// cancelled (its container was recycled, or the item was removed): a cancelled load that is still waiting leaves the
/// queue without running, so the items on screen don't wait behind ones that scrolled away. A cancelled or failed load
/// leaves the slot without a thumbnail and not pending, so the next <see cref="RequestAsync"/> retries it.
/// UI-free (the image type is generic) so it can be unit-tested; the app uses it with XAML image sources.
/// </summary>
public sealed class ThumbnailQueue<TImage> where TImage : class
{
    readonly Func<string, CancellationToken, Task<TImage?>> _load;
    readonly Action<string, Exception> _onError;
    readonly SemaphoreSlim _gate;
    readonly Dictionary<IThumbnailSlot<TImage>, CancellationTokenSource> _pending = new(ReferenceEqualityComparer.Instance);
    readonly Lock _sync = new();

    public ThumbnailQueue(Func<string, CancellationToken, Task<TImage?>> load, Action<string, Exception> onError, int maxConcurrent = 4)
    {
        _load = load;
        _onError = onError;
        _gate = new SemaphoreSlim(maxConcurrent);
    }

    public bool IsPending(IThumbnailSlot<TImage> slot)
    {
        lock (_sync) return _pending.ContainsKey(slot);
    }

    /// <summary>
    /// Loads the slot's thumbnail unless it has one or a load is already pending. Never throws: cancellation is
    /// silent and failures go to the error callback.
    /// </summary>
    public async Task RequestAsync(IThumbnailSlot<TImage> slot)
    {
        CancellationTokenSource cts;
        lock (_sync)
        {
            if (slot.Thumbnail is not null || _pending.ContainsKey(slot)) return;
            cts = new CancellationTokenSource();
            _pending.Add(slot, cts);
        }

        var token = cts.Token;
        try
        {
            await _gate.WaitAsync(token);
            try
            {
                token.ThrowIfCancellationRequested();
                var image = await _load(slot.Path, token);
                token.ThrowIfCancellationRequested();
                slot.Thumbnail = image;
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Recycled or removed: it reloads when it comes back into view.
        }
        catch (Exception e)
        {
            _onError(slot.Path, e);
        }
        finally
        {
            lock (_sync)
            {
                if (_pending.TryGetValue(slot, out var current) && current == cts) _pending.Remove(slot);
            }
            cts.Dispose();
        }
    }

    /// <summary>Cancels the slot's pending load, if any.</summary>
    public void Cancel(IThumbnailSlot<TImage> slot)
    {
        CancellationTokenSource? cts;
        lock (_sync)
        {
            if (!_pending.Remove(slot, out cts)) return;
        }
        // Outside the lock: cancellation callbacks may run the load's continuation inline.
        try { cts.Cancel(); } catch (ObjectDisposedException) { /* the load finished meanwhile */ }
    }
}
