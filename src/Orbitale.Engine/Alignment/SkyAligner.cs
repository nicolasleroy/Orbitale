using OpenCvSharp;
using Orbitale.Engine.GapFill;

namespace Orbitale.Engine.Alignment;

/// <summary>Estimates how the sky moved between two frames from their matched stars.</summary>
public static class SkyAligner
{
    const int MinInliers = 20;               // fewer RANSAC inliers: no reliable sky motion
    const double ReprojThreshold = 1.5;      // RANSAC inlier distance, detection px
    const double MinScale = 0.995, MaxScale = 1.005;   // the sky rotates, it does not zoom

    /// <summary>
    /// The similarity transform (2×3 CV_64FC1, full-resolution px) that maps positions in <paramref name="from"/>'s frame
    /// to <paramref name="to"/>'s frame, or null when it cannot be trusted. Stars (full-res coordinates) are paired with
    /// <see cref="StarMatcher.Match"/> within <paramref name="maxShiftDetectPx"/>, then fitted with RANSAC
    /// (<see cref="Cv2.EstimateAffinePartial2D"/>, threshold 1.5 detection px). Null when fewer than 20 inliers remain or
    /// the fitted scale leaves 0.995..1.005. <paramref name="scale"/> = full-res px per detection px. The caller owns the result.
    /// </summary>
    public static Mat? Estimate(IReadOnlyList<Star> from, IReadOnlyList<Star> to, double maxShiftDetectPx, double scale)
    {
        var fit = FitSimilarity(from, to, maxShiftDetectPx, scale);
        if (fit is null) return null;
        var m = new Mat(2, 3, MatType.CV_64FC1);
        for (int r = 0; r < 2; r++) for (int c = 0; c < 3; c++) m.Set(r, c, fit.Value.A[3 * r + c]);
        return m;
    }

    /// <summary>
    /// <see cref="Estimate"/> refined for lens distortion: a cubic polynomial correction on top of the similarity, fitted
    /// to the RANSAC inliers in both directions. Falls back to the similarity alone when too few inliers support it or the
    /// fit is ill-conditioned. Null when <see cref="Estimate"/> is.
    /// </summary>
    public static SkyWarp? EstimateWarp(IReadOnlyList<Star> from, IReadOnlyList<Star> to, double maxShiftDetectPx, double scale)
    {
        var fit = FitSimilarity(from, to, maxShiftDetectPx, scale);
        if (fit is null) return null;
        var (a, inliers) = fit.Value;
        var ainv = SkyWarp.FromAffine(a).Inverse();
        var src = inliers.Select(p => (p.Prev.X, p.Prev.Y)).Select(p => ((double)p.Item1, (double)p.Item2)).ToArray();
        var dst = inliers.Select(p => (p.Cur.X, p.Cur.Y)).Select(p => ((double)p.Item1, (double)p.Item2)).ToArray();
        // Residuals of the similarity, both ways: what the polynomial must absorb.
        var fwdRes = src.Select((p, k) => (dst[k].Item1 - (a[0] * p.Item1 + a[1] * p.Item2 + a[2]), dst[k].Item2 - (a[3] * p.Item1 + a[4] * p.Item2 + a[5]))).ToArray();
        var backRes = dst.Select((p, k) => { var (x, y) = ainv.Map(p.Item1, p.Item2); return (src[k].Item1 - x, src[k].Item2 - y); }).ToArray();

        var keep = Enumerable.Range(0, src.Length).ToArray();
        if (keep.Length < MinPolyInliers || FitCubic(src, fwdRes, keep, scale) is not { } first) return SkyWarp.FromAffine(a);
        // One robust re-weighting pass: drop matches whose residual from the fit is an outlier (median + 3 robust sigmas).
        var err = keep.Select(k => { var (dx, dy) = first.Eval(src[k].Item1, src[k].Item2); return Math.Sqrt(Sq(fwdRes[k].Item1 - dx) + Sq(fwdRes[k].Item2 - dy)); }).ToArray();
        double med = Median(err), mad = Median(err.Select(e => Math.Abs(e - med)).ToArray()) * 1.4826;
        keep = keep.Where((k, i) => err[i] <= med + 3 * mad).ToArray();
        if (keep.Length < MinPolyInliers) return SkyWarp.FromAffine(a);
        var forward = FitCubic(src, fwdRes, keep, scale);
        var backward = FitCubic(dst, backRes, keep, scale);
        return forward is null || backward is null ? SkyWarp.FromAffine(a) : SkyWarp.FromPolynomial(a, forward, backward);
    }

    const int MinPolyInliers = 60;            // fewer inliers: the similarity alone
    const double PolyCandidateDistance = 2.5;  // detection px from the similarity: candidate matches for the cubic fit
    const double MaxCondition = 1e6;          // design-matrix condition number beyond which the cubic fit is not trusted

    static double Sq(double v) => v * v;

    static double Median(double[] v)
    {
        var a = (double[])v.Clone();
        Array.Sort(a);
        int n = a.Length;
        return n == 0 ? 0 : n % 2 == 1 ? a[n / 2] : (a[n / 2 - 1] + a[n / 2]) / 2;
    }

    /// <summary>
    /// Least-squares cubic (10 terms per output) from <paramref name="points"/> to <paramref name="residuals"/> over the
    /// indices <paramref name="use"/>, on coordinates normalised to [−1, 1] over their extent. Null when ill-conditioned.
    /// </summary>
    static CubicCorrection? FitCubic((double X, double Y)[] points, (double X, double Y)[] residuals, int[] use, double scale)
    {
        double x0 = use.Min(k => points[k].X), x1 = use.Max(k => points[k].X), y0 = use.Min(k => points[k].Y), y1 = use.Max(k => points[k].Y);
        double cx = (x0 + x1) / 2, cy = (y0 + y1) / 2, hx = Math.Max(1, (x1 - x0) / 2), hy = Math.Max(1, (y1 - y0) / 2);
        using var design = new Mat(use.Length, 10, MatType.CV_64FC1);
        using var bx = new Mat(use.Length, 1, MatType.CV_64FC1);
        using var by = new Mat(use.Length, 1, MatType.CV_64FC1);
        Span<double> t = stackalloc double[10];
        for (int i = 0; i < use.Length; i++)
        {
            var p = points[use[i]];
            CubicCorrection.Terms((p.X - cx) / hx, (p.Y - cy) / hy, t);
            for (int c = 0; c < 10; c++) design.Set(i, c, t[c]);
            bx.Set(i, 0, residuals[use[i]].X);
            by.Set(i, 0, residuals[use[i]].Y);
        }
        using var w = new Mat();
        Cv2.SVDecomp(design, w, new Mat(), new Mat(), SVD.Flags.NoUV);
        Cv2.MinMaxLoc(w, out double wMin, out double wMax);
        if (wMin <= 0 || wMax / wMin > MaxCondition) return null;
        using var sx = new Mat();
        using var sy = new Mat();
        Cv2.Solve(design, bx, sx, DecompTypes.SVD);
        Cv2.Solve(design, by, sy, DecompTypes.SVD);
        var ax = new double[10]; var ay = new double[10];
        for (int c = 0; c < 10; c++) { ax[c] = sx.At<double>(c, 0); ay[c] = sy.At<double>(c, 0); }
        // Clamp: no larger than the correction seen on the fitted stars (x1.5) plus one detection px.
        var fit = new CubicCorrection(cx, cy, hx, hy, ax, ay, double.PositiveInfinity);
        double seen = use.Max(k => { var (dx, dy) = fit.Eval(points[k].X, points[k].Y); return Math.Sqrt(dx * dx + dy * dy); });
        return new CubicCorrection(cx, cy, hx, hy, ax, ay, seen * 1.5 + scale);
    }

    /// <summary>Matched stars → RANSAC similarity (row-major 2×3) and its inlier pairs, or null.</summary>
    static (double[] A, List<(Star Prev, Star Cur)> Inliers)? FitSimilarity(IReadOnlyList<Star> from, IReadOnlyList<Star> to, double maxShiftDetectPx, double scale)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        var pairs = StarMatcher.Match(from, to, maxShiftDetectPx, scale);
        if (pairs.Count < MinInliers) return null;

        using var src = Mat.FromArray(pairs.Select(p => new Point2d(p.Prev.X, p.Prev.Y)).ToArray());
        using var dst = Mat.FromArray(pairs.Select(p => new Point2d(p.Cur.X, p.Cur.Y)).ToArray());
        using var inliers = new Mat();
        using var m = Cv2.EstimateAffinePartial2D(src, dst, inliers, RobustEstimationAlgorithms.RANSAC, ReprojThreshold * scale);
        if (m is null || m.Empty()) return null;

        double s = Math.Sqrt(m.At<double>(0, 0) * m.At<double>(0, 0) + m.At<double>(1, 0) * m.At<double>(1, 0));
        if (Cv2.CountNonZero(inliers) < MinInliers || s < MinScale || s > MaxScale) return null;
        var a = new double[6];
        for (int r = 0; r < 2; r++) for (int c = 0; c < 3; c++) a[3 * r + c] = m.At<double>(r, c);
        // Candidates for the distortion fit: every match within 4 detection px of the similarity. The RANSAC inliers alone
        // (1.5 px) would leave out exactly the frame edges, where the lens distortion is; the robust pass trims the rest.
        double tol = PolyCandidateDistance * scale;
        var kept = pairs.Where(p => Sq(p.Cur.X - (a[0] * p.Prev.X + a[1] * p.Prev.Y + a[2])) + Sq(p.Cur.Y - (a[3] * p.Prev.X + a[4] * p.Prev.Y + a[5])) <= tol * tol)
                        .ToList();
        return (a, kept);
    }
}
