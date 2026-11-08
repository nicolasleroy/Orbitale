using OpenCvSharp;

namespace Orbitale.Engine.Blending;

/// <summary>Shared validation, counting and lifetime for the accumulators.</summary>
public abstract class AccumulatorBase : IAccumulator
{
    Size _size;
    bool _disposed;
    int? _index;   // the index given to the Add in progress

    public int Count { get; private set; }

    /// <summary>The position of the frame being added: its index when given, else the number of frames added so far.</summary>
    protected int Position => _index ?? Count;

    public Mat Current
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (Count == 0) throw new InvalidOperationException("No frame has been added yet.");
            return GetCurrent();
        }
    }

    public void Add(Mat frame32f)
    {
        ArgumentNullException.ThrowIfNull(frame32f);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (frame32f.Type() != MatType.CV_32FC3)
            throw new ArgumentException($"Frame is {frame32f.Type()}, expected CV_32FC3.", nameof(frame32f));
        if (Count == 0)
        {
            _size = frame32f.Size();
            AddFirst(frame32f);
        }
        else
        {
            if (frame32f.Size() != _size)
                throw new ArgumentException($"Frame is {frame32f.Width}x{frame32f.Height}, expected {_size.Width}x{_size.Height}.", nameof(frame32f));
            AddNext(frame32f);
        }
        Count++;
    }

    public void Add(Mat frame32f, int index)
    {
        CheckIndex(index);
        _index = index;
        try { Add(frame32f); }
        finally { _index = null; }
    }

    /// <summary>Throws <see cref="ArgumentOutOfRangeException"/> for an index this accumulator cannot place.</summary>
    protected virtual void CheckIndex(int index) => ArgumentOutOfRangeException.ThrowIfNegative(index);

    protected abstract void AddFirst(Mat frame);
    protected abstract void AddNext(Mat frame);
    protected abstract Mat GetCurrent();
    protected abstract void DisposeMats();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        DisposeMats();
    }
}
