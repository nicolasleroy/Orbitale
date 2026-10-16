using OpenCvSharp;

namespace Orbitale.Engine.Blending;

/// <summary>
/// Lighten with fading tails. Tracks the background B (running per-pixel minimum) and the excess
/// E = max(E * d, frame - B) with d = 0.01^(1/T) and T = max(2, round(N * (1 - 0.9 * strength))).
/// Result = B + E. A static scene has E = 0, so it is left untouched.
/// </summary>
/// <remarks>
/// Each Add is one fused, row-parallel pass that also refreshes the result, so <see cref="IAccumulator.Current"/> is free.
/// Denormals cannot occur: E only decays for at most N frames, and N/T ≤ 10, so E ≥ E₀·0.01¹⁰ = E₀·1e-20, far above
/// float's normal range (1.2e-38) for any non-zero E₀ ≥ 1/65535.
/// </remarks>
public sealed class CometAccumulator : AccumulatorBase
{
    readonly float _decay;
    Mat? _bg, _excess, _result;

    public CometAccumulator(int totalFrames, double strength)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(totalFrames, 1);
        double s = Math.Clamp(strength, 0.0, 1.0);
        int tail = Math.Max(2, (int)Math.Round(totalFrames * (1.0 - 0.9 * s)));
        _decay = (float)Math.Pow(0.01, 1.0 / tail);
    }

    protected override void AddFirst(Mat frame)
    {
        _bg = frame.Clone();
        _excess = new Mat(frame.Rows, frame.Cols, MatType.CV_32FC3, Scalar.All(0));
        _result = frame.Clone();
    }

    protected override unsafe void AddNext(Mat frame)
    {
        Mat bg = _bg!, ex = _excess!, res = _result!;
        int len = frame.Cols * 3;
        float d = _decay;
        Parallel.For(0, frame.Rows, r =>
        {
            float* f = (float*)frame.Ptr(r), b = (float*)bg.Ptr(r), e = (float*)ex.Ptr(r), o = (float*)res.Ptr(r);
            for (int i = 0; i < len; i++)
            {
                float bi = Math.Min(b[i], f[i]);       // B includes this frame, so frame - B >= 0
                float ei = Math.Max(e[i] * d, f[i] - bi);
                b[i] = bi; e[i] = ei; o[i] = bi + ei;
            }
        });
    }

    protected override Mat GetCurrent() => _result!;

    protected override void DisposeMats() { _bg?.Dispose(); _excess?.Dispose(); _result?.Dispose(); }
}
