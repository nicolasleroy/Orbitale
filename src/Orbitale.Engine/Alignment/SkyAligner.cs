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
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        var pairs = StarMatcher.Match(from, to, maxShiftDetectPx, scale);
        if (pairs.Count < MinInliers) return null;

        using var src = Mat.FromArray(pairs.Select(p => new Point2d(p.Prev.X, p.Prev.Y)).ToArray());
        using var dst = Mat.FromArray(pairs.Select(p => new Point2d(p.Cur.X, p.Cur.Y)).ToArray());
        using var inliers = new Mat();
        var m = Cv2.EstimateAffinePartial2D(src, dst, inliers, RobustEstimationAlgorithms.RANSAC, ReprojThreshold * scale);
        if (m is null || m.Empty()) { m?.Dispose(); return null; }

        double s = Math.Sqrt(m.At<double>(0, 0) * m.At<double>(0, 0) + m.At<double>(1, 0) * m.At<double>(1, 0));
        if (Cv2.CountNonZero(inliers) < MinInliers || s < MinScale || s > MaxScale)
        {
            m.Dispose();
            return null;
        }
        return m;
    }
}
