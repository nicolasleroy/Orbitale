using OpenCvSharp;

namespace Orbitale.Engine.Blending;

/// <summary>
/// Lighten with fading tails. Tracks the background B (running per-pixel minimum) and the excess
/// E = max(E * d, frame - B) with d = 0.01^(1/T) and T = max(2, round(N * (1 - 0.9 * strength))).
/// Result = B + E. A static scene has E = 0, so it is left untouched.
/// </summary>
public sealed class CometAccumulator : AccumulatorBase
{
    readonly float _decay;
    Mat? _bg, _excess, _tmp, _result;

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
        _tmp = new Mat(frame.Rows, frame.Cols, MatType.CV_32FC3);
        _result = new Mat(frame.Rows, frame.Cols, MatType.CV_32FC3);
    }

    protected override void AddNext(Mat frame)
    {
        Cv2.Min(_bg!, frame, _bg!);                  // B includes this frame, so frame - B >= 0
        Cv2.Subtract(frame, _bg!, _tmp!);
        Cv2.Multiply(_excess!, Scalar.All(_decay), _excess!);
        Cv2.Max(_excess!, _tmp!, _excess!);
    }

    protected override Mat GetCurrent()
    {
        Cv2.Add(_bg!, _excess!, _result!);
        return _result!;
    }

    protected override void DisposeMats() { _bg?.Dispose(); _excess?.Dispose(); _tmp?.Dispose(); _result?.Dispose(); }
}
