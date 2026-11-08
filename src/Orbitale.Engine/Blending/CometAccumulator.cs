using OpenCvSharp;
using Orbitale.Engine.Imaging;

namespace Orbitale.Engine.Blending;

/// <summary>
/// Lighten with trails that fade in and out along the sequence (see <see cref="CometFade"/>). Tracks the background B,
/// the running per-pixel maximum of each frame's star-free version, and the trail light S = max(S, B + w·(frame − B)),
/// where w is the frame's <see cref="CometFade"/> weight. Result = max(B, S). A static scene has frame = B, so it is left
/// untouched; light that varies slowly (a horizon glow, a lake reflection, the landscape) is part of B and keeps its
/// Lighten brightness, only stars fade. With w = 1 for every frame the result is Lighten.
/// </summary>
/// <remarks>
/// <para>
/// Star-free version: a per-channel morphological opening with a 7 px ellipse at detection scale (see
/// <see cref="DetectionScale"/>; 7·fullLong/2000 px at full resolution), which removes stars and their exposure dashes but
/// keeps broad features. It is computed on the area-downscaled frame and upscaled bilinearly (pixel centres, as
/// <c>INTER_LINEAR</c>) row by row inside the fused pass, then clamped to the frame: an opening never exceeds its input,
/// and the clamp keeps the upscaled one from spilling sky light across the ridge.
/// </para>
/// <para>
/// Each Add is one detection-scale resize and opening (buffers reused) and one fused, row-parallel full-resolution pass
/// that also refreshes the result, so <see cref="IAccumulator.Current"/> is free. No full-size buffer is allocated per Add.
/// The weight depends on the frame's position in the sequence: <see cref="IAccumulator.Add(Mat, int)"/> takes the
/// frame's file index (frames the run could not decode leave their index unused); plain <see cref="IAccumulator.Add(Mat)"/>
/// uses the number of frames added so far.
/// </para>
/// </remarks>
public sealed class CometAccumulator : AccumulatorBase
{
    const int OpeningSize = 7;   // ellipse, detection px

    readonly int _total;
    readonly double _fadeIn, _fadeOut;
    Mat? _bg, _trail, _result, _small, _opened, _kernel;
    int[]? _x0, _x1;
    float[]? _fx, _samples;

    public CometAccumulator(int totalFrames, double fadeIn, double fadeOut)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(totalFrames, 1);
        _total = totalFrames;
        (_fadeIn, _fadeOut) = CometFade.Normalize(fadeIn, fadeOut);
    }

    /// <summary>Frame indices run over 0..totalFrames − 1.</summary>
    protected override void CheckIndex(int index)
    {
        base.CheckIndex(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, _total);
    }

    protected override void AddFirst(Mat frame)
    {
        int rows = frame.Rows, cols = frame.Cols;
        _bg = new Mat(rows, cols, MatType.CV_32FC3, Scalar.All(float.MinValue));   // max(B, x) = x on the first frame
        _trail = new Mat(rows, cols, MatType.CV_32FC3, Scalar.All(float.MinValue));   // max(S, x) = x on the first frame
        _result = new Mat(rows, cols, MatType.CV_32FC3);
        var det = DetectionScale.For(frame.Size());
        _small = det == frame.Size() ? null : new Mat(det, MatType.CV_32FC3);
        _opened = new Mat(det, MatType.CV_32FC3);
        _kernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(OpeningSize, OpeningSize));
        (_x0, _x1, _fx) = Taps(cols, det.Width);
        AddNext(frame);
    }

    protected override unsafe void AddNext(Mat frame)
    {
        // Star-free version at detection scale.
        Mat opened = _opened!;
        if (_small is { } small)
        {
            Cv2.Resize(frame, small, small.Size(), 0, 0, InterpolationFlags.Area);
            Cv2.MorphologyEx(small, opened, MorphTypes.Open, _kernel!);
        }
        else Cv2.MorphologyEx(frame, opened, MorphTypes.Open, _kernel!);

        Mat bg = _bg!, tr = _trail!, res = _result!;
        int cols = frame.Cols, srcRows = opened.Rows;
        int[] x0 = _x0!, x1 = _x1!;
        float[] fx = _fx!;
        double sy = (double)srcRows / frame.Rows;
        float w = CometFade.Weight(Position, _total, _fadeIn, _fadeOut);
        float[] allow = NoiseAllowance(frame, opened, sy);
        Parallel.For(0, frame.Rows, r =>
        {
            var (y0, y1, wy) = Tap(r, sy, srcRows);
            float* s0 = (float*)opened.Ptr(y0), s1 = (float*)opened.Ptr(y1);
            float* f = (float*)frame.Ptr(r), b = (float*)bg.Ptr(r), t = (float*)tr.Ptr(r), o = (float*)res.Ptr(r);
            for (int c = 0; c < cols; c++)
            {
                int a0 = 3 * x0[c], a1 = 3 * x1[c];
                float wx = fx[c];
                for (int ch = 0; ch < 3; ch++)
                {
                    float top = s0[a0 + ch] + (s0[a1 + ch] - s0[a0 + ch]) * wx;
                    float bottom = s1[a0 + ch] + (s1[a1 + ch] - s1[a0 + ch]) * wx;
                    int i = 3 * c + ch;
                    float fi = f[i];
                    // The opening (plus the frame's noise allowance) never exceeds the frame.
                    float starFree = Math.Min(top + (bottom - top) * wy + allow[ch], fi);
                    float bi = Math.Max(b[i], starFree);
                    float ti = Math.Max(t[i], bi + w * (fi - bi));   // the frame's star light, weighted, over B
                    b[i] = bi; t[i] = ti; o[i] = Math.Max(bi, ti);
                }
            }
        });
    }

    /// <summary>
    /// Per channel, how far above the upscaled opening sensor noise reaches: median + 4 robust sigmas (MAD·1.4826) of
    /// frame − opening, sampled on a sparse grid (about 160 rows × 1000 columns). Lighten keeps each pixel's noise maximum;
    /// letting that much of the frame into B keeps it in the comet too, so a noisy landscape is as bright as with Lighten.
    /// Stars stand far above it (they are a small fraction of the samples, the median and MAD ignore them).
    /// </summary>
    unsafe float[] NoiseAllowance(Mat frame, Mat opened, double sy)
    {
        int rows = frame.Rows, cols = frame.Cols;
        int rowStep = Math.Max(1, rows / 160), colStep = Math.Max(1, cols / 1000);
        int nr = (rows + rowStep - 1) / rowStep, nc = (cols + colStep - 1) / colStep;
        if (_samples is null || _samples.Length != 3 * nr * nc) _samples = new float[3 * nr * nc];
        var v = _samples;
        int[] x0 = _x0!, x1 = _x1!;
        float[] fx = _fx!;
        int n = nr * nc, srcRows = opened.Rows;
        Parallel.For(0, nr, j =>
        {
            int r = j * rowStep;
            var (y0, y1, wy) = Tap(r, sy, srcRows);
            float* s0 = (float*)opened.Ptr(y0), s1 = (float*)opened.Ptr(y1), f = (float*)frame.Ptr(r);
            for (int k = 0; k < nc; k++)
            {
                int c = k * colStep, a0 = 3 * x0[c], a1 = 3 * x1[c];
                for (int ch = 0; ch < 3; ch++)
                {
                    float top = s0[a0 + ch] + (s0[a1 + ch] - s0[a0 + ch]) * fx[c];
                    float bottom = s1[a0 + ch] + (s1[a1 + ch] - s1[a0 + ch]) * fx[c];
                    v[ch * n + j * nc + k] = f[3 * c + ch] - (top + (bottom - top) * wy);
                }
            }
        });
        var allow = new float[3];
        Parallel.For(0, 3, ch =>
        {
            var span = v.AsSpan(ch * n, n);
            span.Sort();
            float med = span[n / 2];
            for (int i = 0; i < n; i++) span[i] = Math.Abs(span[i] - med);
            span.Sort();
            allow[ch] = Math.Max(0, med + 4 * 1.4826f * span[n / 2]);
        });
        return allow;
    }

    /// <summary>Bilinear taps (INTER_LINEAR, pixel centres, clamped at the border) for every destination column.</summary>
    static (int[] X0, int[] X1, float[] Fx) Taps(int dst, int src)
    {
        var x0 = new int[dst]; var x1 = new int[dst]; var fx = new float[dst];
        double s = (double)src / dst;
        for (int c = 0; c < dst; c++) (x0[c], x1[c], fx[c]) = Tap(c, s, src);
        return (x0, x1, fx);
    }

    static (int I0, int I1, float W) Tap(int dst, double scale, int src)
    {
        double p = (dst + 0.5) * scale - 0.5;
        int i0 = (int)Math.Floor(p);
        float w = (float)(p - i0);
        if (i0 < 0) { i0 = 0; w = 0; }
        if (i0 >= src - 1) { i0 = src - 1; w = 0; }
        return (i0, Math.Min(i0 + 1, src - 1), w);
    }

    protected override Mat GetCurrent() => _result!;

    protected override void DisposeMats()
    {
        _bg?.Dispose(); _trail?.Dispose(); _result?.Dispose(); _small?.Dispose(); _opened?.Dispose(); _kernel?.Dispose();
    }
}
