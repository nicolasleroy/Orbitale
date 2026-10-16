using OpenCvSharp;

namespace Orbitale.Engine.Blending;

/// <summary>Per-pixel running minimum.</summary>
public sealed class DarkenAccumulator : AccumulatorBase
{
    Mat? _acc;
    protected override void AddFirst(Mat frame) => _acc = frame.Clone();
    protected override void AddNext(Mat frame) => Cv2.Min(_acc!, frame, _acc!);
    protected override Mat GetCurrent() => _acc!;
    protected override void DisposeMats() => _acc?.Dispose();
}
