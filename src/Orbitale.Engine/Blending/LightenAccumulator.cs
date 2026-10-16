using OpenCvSharp;

namespace Orbitale.Engine.Blending;

/// <summary>Per-pixel running maximum (classic star trails).</summary>
public sealed class LightenAccumulator : AccumulatorBase
{
    Mat? _acc;
    protected override void AddFirst(Mat frame) => _acc = frame.Clone();
    protected override void AddNext(Mat frame) => Cv2.Max(_acc!, frame, _acc!);
    protected override Mat GetCurrent() => _acc!;
    protected override void DisposeMats() => _acc?.Dispose();
}
