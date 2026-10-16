using OpenCvSharp;

namespace Orbitale.Engine.Blending;

/// <summary>Per-pixel running maximum (classic star trails).</summary>
public sealed class LightenAccumulator : AccumulatorBase
{
    Mat? _acc;
    protected override void AddFirst(Mat frame) => _acc = frame.Clone();

    protected override unsafe void AddNext(Mat frame)
    {
        // Row-parallel: Cv2.Max runs on one thread and costs ~120 ms on a 40 MP frame.
        Mat acc = _acc!;
        int len = frame.Cols * 3;
        Parallel.For(0, frame.Rows, r =>
        {
            float* f = (float*)frame.Ptr(r), a = (float*)acc.Ptr(r);
            for (int i = 0; i < len; i++) a[i] = Math.Max(a[i], f[i]);
        });
    }

    protected override Mat GetCurrent() => _acc!;
    protected override void DisposeMats() => _acc?.Dispose();
}
