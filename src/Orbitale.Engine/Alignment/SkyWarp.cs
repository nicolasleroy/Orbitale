using OpenCvSharp;

namespace Orbitale.Engine.Alignment;

/// <summary>
/// How the sky moved from one frame to another, in full-resolution px: a similarity (the RANSAC fit) refined by an
/// optional cubic polynomial correction that absorbs lens distortion, in both directions. Chains of such steps (frame to
/// frame) compose pointwise. Immutable.
/// </summary>
public sealed class SkyWarp
{
    readonly Step[] _steps;

    SkyWarp(Step[] steps) => _steps = steps;

    /// <summary>A pure similarity/affine warp from a 2×3 CV_64FC1 matrix (forward: from → to).</summary>
    internal static SkyWarp FromAffine(Mat affine)
    {
        ArgumentNullException.ThrowIfNull(affine);
        var a = new double[6];
        for (int r = 0; r < 2; r++) for (int c = 0; c < 3; c++) a[3 * r + c] = affine.At<double>(r, c);
        return FromAffine(a);
    }

    internal static SkyWarp FromAffine(double[] a) => new([new Step(a, Invert(a), null, null)]);

    internal static SkyWarp FromPolynomial(double[] a, CubicCorrection forward, CubicCorrection backward) =>
        new([new Step(a, Invert(a), forward, backward)]);

    /// <summary>True when any step carries a polynomial (distortion) correction.</summary>
    public bool IsPolynomial => _steps.Any(s => s.Forward is not null);

    /// <summary>Position in the target frame of the point (<paramref name="x"/>, <paramref name="y"/>) of the source frame.</summary>
    public (double X, double Y) Map(double x, double y)
    {
        foreach (var s in _steps)
        {
            var (px, py) = Apply(s.A, x, y);
            if (s.Forward is { } f) { var (dx, dy) = f.Eval(x, y); px += dx; py += dy; }
            (x, y) = (px, py);
        }
        return (x, y);
    }

    /// <summary>Position in the source frame of the point (<paramref name="x"/>, <paramref name="y"/>) of the target frame.</summary>
    public (double X, double Y) MapBack(double x, double y)
    {
        for (int i = _steps.Length - 1; i >= 0; i--)
        {
            var s = _steps[i];
            var (px, py) = Apply(s.AInv, x, y);
            if (s.Backward is { } b) { var (dx, dy) = b.Eval(x, y); px += dx; py += dy; }
            (x, y) = (px, py);
        }
        return (x, y);
    }

    /// <summary>The warp from the target frame back to the source frame.</summary>
    public SkyWarp Inverse() => new(_steps.Reverse().Select(s => new Step(s.AInv, s.A, s.Backward, s.Forward)).ToArray());

    /// <summary>This warp followed by <paramref name="next"/> (source → this target → next's target).</summary>
    public SkyWarp Then(SkyWarp next)
    {
        ArgumentNullException.ThrowIfNull(next);
        return new([.. _steps, .. next._steps]);
    }

    /// <summary>
    /// Remap grids (CV_32FC1, <paramref name="height"/>×<paramref name="width"/>) that pull the source frame into a region
    /// of the target frame: pixel (u, v) of the region is the target pixel (u + <paramref name="originX"/>, v +
    /// <paramref name="originY"/>) of an image whose pixel centres map to full resolution as full = (p + ½)·s − ½, and its
    /// map values are the source position in that same image space. The warp is evaluated on a grid every
    /// <paramref name="step"/> px and interpolated bilinearly in between (the warp is smooth: sub-0.01 px error).
    /// </summary>
    internal unsafe void BuildMaps(int width, int height, double originX, double originY, double sx, double sy, Mat mapX, Mat mapY, int step = 8)
    {
        int nx = (width - 1) / step + 2, ny = (height - 1) / step + 2;
        var gx = new float[ny, nx]; var gy = new float[ny, nx];
        for (int j = 0; j < ny; j++)
            for (int i = 0; i < nx; i++)
            {
                double u = originX + i * step, v = originY + j * step;
                var (fx, fy) = MapBack((u + 0.5) * sx - 0.5, (v + 0.5) * sy - 0.5);
                gx[j, i] = (float)((fx + 0.5) / sx - 0.5);
                gy[j, i] = (float)((fy + 0.5) / sy - 0.5);
            }
        mapX.Create(height, width, MatType.CV_32FC1);
        mapY.Create(height, width, MatType.CV_32FC1);
        var mx = mapX; var my = mapY;
        Parallel.For(0, height, v =>
        {
            int j = v / step; float t = (float)(v - j * step) / step;
            float* px = (float*)mx.Ptr(v); float* py = (float*)my.Ptr(v);
            for (int u = 0; u < width; u++)
            {
                int i = u / step; float s = (float)(u - i * step) / step;
                float a = gx[j, i] + (gx[j, i + 1] - gx[j, i]) * s, b = gx[j + 1, i] + (gx[j + 1, i + 1] - gx[j + 1, i]) * s;
                px[u] = a + (b - a) * t;
                a = gy[j, i] + (gy[j, i + 1] - gy[j, i]) * s; b = gy[j + 1, i] + (gy[j + 1, i + 1] - gy[j + 1, i]) * s;
                py[u] = a + (b - a) * t;
            }
        });
    }

    static (double, double) Apply(double[] a, double x, double y) => (a[0] * x + a[1] * y + a[2], a[3] * x + a[4] * y + a[5]);

    static double[] Invert(double[] a)
    {
        double det = a[0] * a[4] - a[1] * a[3];
        double i0 = a[4] / det, i1 = -a[1] / det, i3 = -a[3] / det, i4 = a[0] / det;
        return [i0, i1, -(i0 * a[2] + i1 * a[5]), i3, i4, -(i3 * a[2] + i4 * a[5])];
    }

    sealed record Step(double[] A, double[] AInv, CubicCorrection? Forward, CubicCorrection? Backward);
}

/// <summary>
/// A cubic 2-D polynomial correction (10 terms per output) on coordinates normalised to [−1, 1] over the fitted stars'
/// extent, added to the similarity. Beyond that extent it is held at its border value, and its magnitude is clamped to
/// what was seen on the fitted stars (×1.5 + one detection px), so it cannot run away where there are no stars (the
/// landscape, the corners).
/// </summary>
internal sealed class CubicCorrection
{
    readonly double _cx, _cy, _hx, _hy, _limit;
    readonly double[] _ax, _ay;

    internal CubicCorrection(double cx, double cy, double hx, double hy, double[] ax, double[] ay, double limit)
    {
        _cx = cx; _cy = cy; _hx = hx; _hy = hy; _ax = ax; _ay = ay; _limit = limit;
    }

    internal static void Terms(double x, double y, Span<double> t)
    {
        t[0] = 1; t[1] = x; t[2] = y; t[3] = x * x; t[4] = x * y; t[5] = y * y; t[6] = x * x * x; t[7] = x * x * y; t[8] = x * y * y; t[9] = y * y * y;
    }

    internal (double X, double Y) Eval(double x, double y)
    {
        Span<double> t = stackalloc double[10];
        // Outside the fitted stars' extent the correction is held at its value on the extent's border: a cubic must not be
        // extrapolated.
        Terms(Math.Clamp((x - _cx) / _hx, -1, 1), Math.Clamp((y - _cy) / _hy, -1, 1), t);
        double dx = 0, dy = 0;
        for (int k = 0; k < 10; k++) { dx += _ax[k] * t[k]; dy += _ay[k] * t[k]; }
        double m = Math.Sqrt(dx * dx + dy * dy);
        return m > _limit ? (dx * _limit / m, dy * _limit / m) : (dx, dy);
    }
}
