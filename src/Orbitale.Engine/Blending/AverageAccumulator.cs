using OpenCvSharp;

namespace Orbitale.Engine.Blending;

/// <summary>Per-pixel mean. Keeps a CV_64FC3 sum; the mean is computed lazily and cached until the next Add.</summary>
public sealed class AverageAccumulator : AccumulatorBase
{
    Mat? _sum, _mean;
    readonly Mat _noMask = new();
    bool _dirty;

    protected override void AddFirst(Mat frame)
    {
        _sum = new Mat();
        frame.ConvertTo(_sum, MatType.CV_64FC3);
        _mean = new Mat(frame.Rows, frame.Cols, MatType.CV_32FC3);
        _dirty = true;
    }

    protected override void AddNext(Mat frame)
    {
        Cv2.Accumulate(frame, _sum!, _noMask);
        _dirty = true;
    }

    protected override Mat GetCurrent()
    {
        if (_dirty)
        {
            _sum!.ConvertTo(_mean!, MatType.CV_32FC3, 1.0 / Count);
            _dirty = false;
        }
        return _mean!;
    }

    protected override void DisposeMats() { _sum?.Dispose(); _mean?.Dispose(); _noMask.Dispose(); }
}
