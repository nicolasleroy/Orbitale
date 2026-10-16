using OpenCvSharp;
using Orbitale.Engine.Imaging;

namespace Orbitale.Engine.Frames;

/// <summary>
/// LRU cache of decoded frames. Mats are owned by the window; callers must not dispose them.
/// Get runs on the engine thread; at most one Prefetch decode runs on a background task.
/// </summary>
public sealed class FrameWindow : IDisposable
{
    readonly IReadOnlyList<string> _files;
    readonly int _capacity;
    readonly Func<string, Mat> _decode;
    readonly object _gate = new();
    readonly Dictionary<int, Mat> _cache = new();
    readonly LinkedList<int> _lru = new(); // first = least recently used
    int _lastReturned = -1;
    int _pendingIndex = -1;
    Task? _pending;
    bool _disposed;

    public FrameWindow(IReadOnlyList<string> files, int capacity) : this(files, capacity, ImageReader.Read) { }

    internal FrameWindow(IReadOnlyList<string> files, int capacity, Func<string, Mat> decode)
    {
        _decode = decode;
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _files = files;
        _capacity = capacity;
    }

    /// <summary>True when <paramref name="index"/> is decoded and cached (a <see cref="Get"/> would not decode).</summary>
    internal bool IsCached(int index) { lock (_gate) return _cache.ContainsKey(index); }

    /// <summary>Test hook: the in-flight (or last) prefetch task.</summary>
    internal Task? PendingPrefetch { get { lock (_gate) return _pending; } }

    public Mat Get(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, _files.Count);

        Task? wait = null;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (TryHit(index, out var hit)) return hit;
            if (_pendingIndex == index) wait = _pending;
        }

        if (wait is not null)
        {
            wait.Wait(); // never faults: the task swallows decode errors
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (TryHit(index, out var hit)) return hit;
            }
        }

        var mat = _decode(_files[index]);
        lock (_gate)
        {
            if (_disposed) { mat.Dispose(); throw new ObjectDisposedException(nameof(FrameWindow)); }
            if (_cache.TryGetValue(index, out var existing)) { mat.Dispose(); mat = existing; }
            else Insert(index, mat);
            Touch(index);
            _lastReturned = index;
            Evict();
            return mat;
        }
    }

    public void Prefetch(int index)
    {
        lock (_gate)
        {
            if (_disposed || index < 0 || index >= _files.Count) return;
            if (_cache.ContainsKey(index) || _pending is { IsCompleted: false }) return;
            _pendingIndex = index;
            _pending = Task.Run(() => PrefetchWorker(index));
        }
    }

    void PrefetchWorker(int index)
    {
        Mat? mat = null;
        try { mat = _decode(_files[index]); }
        catch { /* a failed prefetch is retried by Get on the caller's thread */ }

        lock (_gate)
        {
            if (mat is not null)
            {
                if (_disposed || _cache.ContainsKey(index)) mat.Dispose();
                else { Insert(index, mat); Evict(); }
            }
            _pendingIndex = -1;
        }
    }

    bool TryHit(int index, out Mat mat)
    {
        if (_cache.TryGetValue(index, out mat!))
        {
            Touch(index);
            _lastReturned = index;
            return true;
        }
        return false;
    }

    void Insert(int index, Mat mat) { _cache[index] = mat; _lru.AddLast(index); }

    void Touch(int index) { _lru.Remove(index); _lru.AddLast(index); }

    void Evict()
    {
        var node = _lru.First;
        while (_cache.Count > _capacity && node is not null)
        {
            var next = node.Next;
            if (node.Value != _lastReturned)
            {
                _cache[node.Value].Dispose();
                _cache.Remove(node.Value);
                _lru.Remove(node);
            }
            node = next;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var m in _cache.Values) m.Dispose();
            _cache.Clear();
            _lru.Clear();
            // An in-flight prefetch disposes its own Mat on completion (see PrefetchWorker).
        }
    }
}
