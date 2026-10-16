using OpenCvSharp;
using Orbitale.Engine.Imaging;
using Orbitale.Engine.Trails;

namespace Orbitale.Engine.GapFill;

/// <summary>
/// A detected star in full-resolution coordinates. <see cref="Radius"/> = sqrt(area/π) of its half-maximum core (blob
/// pixels at or above half the blob's top-hat peak), mapped to full-resolution px, so 1.6·Radius is about the star's
/// FWHM; <see cref="Color"/> = per-channel max of the frame in the 3×3 window at the centroid.
/// </summary>
public readonly record struct Star(float X, float Y, float Radius, Vec3f Color);

/// <summary>Finds point stars in one frame for gap filling. Runs at detection scale (see <see cref="DetectionScale"/>).</summary>
public static class StarDetector
{
    const int TopHatSize = 9;            // ellipse, detection px: removes sky gradients and anything wider than a star
    const double ThresholdSigma = 5;     // median + 5 robust sigmas (MAD·1.4826) of the top-hat
    const int MinArea = 1, MaxArea = 60; // blob area, detection px

    /// <summary>
    /// Gray → white top-hat → robust threshold → 8-connected blobs of area 1..60 → top-hat-weighted centroid and
    /// half-maximum radius, mapped to full resolution through pixel centres. Stars whose full-res centroid lies under <paramref name="excludeMask8U"/>
    /// (non-zero = removed trail) are dropped.
    /// </summary>
    public static unsafe IReadOnlyList<Star> Detect(Mat frame32f, Mat? excludeMask8U = null)
    {
        ArgumentNullException.ThrowIfNull(frame32f);
        if (frame32f.Type() != MatType.CV_32FC3)
            throw new ArgumentException($"Frame is {frame32f.Type()}, expected CV_32FC3.", nameof(frame32f));
        if (excludeMask8U != null && (excludeMask8U.Type() != MatType.CV_8UC1 || excludeMask8U.Size() != frame32f.Size()))
            throw new ArgumentException("Exclude mask must be CV_8UC1 at the frame's size.", nameof(excludeMask8U));

        var detSize = DetectionScale.For(frame32f.Size());
        using var top = new Mat();
        using (var gray = DetectionScale.ToGray(frame32f, detSize))
        using (var k = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(TopHatSize, TopHatSize)))
            Cv2.MorphologyEx(gray, top, MorphTypes.TopHat, k);

        float threshold = TrailDetector.RobustThreshold(top, ThresholdSigma);
        using var binary = new Mat();
        Cv2.Compare(top, Scalar.All(threshold), binary, CmpTypes.GT);

        using var labels = new Mat();
        using var stats = new Mat();
        using var centroids = new Mat();
        int n = Cv2.ConnectedComponentsWithStats(binary, labels, stats, centroids, PixelConnectivity.Connectivity8);
        if (n <= 1) return [];

        var area = new int[n];
        for (int i = 1; i < n; i++) area[i] = stats.At<int>(i, (int)ConnectedComponentsTypes.Area);

        // Top-hat-weighted centroids and half-maximum core areas. Only each kept blob's bounding box is visited.
        var sw = new double[n]; var swx = new double[n]; var swy = new double[n];
        var core = new int[n];
        int rows = labels.Rows, cols = labels.Cols;
        for (int i = 1; i < n; i++)
        {
            if (area[i] < MinArea || area[i] > MaxArea) continue;
            int left = stats.At<int>(i, (int)ConnectedComponentsTypes.Left), topRow = stats.At<int>(i, (int)ConnectedComponentsTypes.Top);
            int bw = stats.At<int>(i, (int)ConnectedComponentsTypes.Width), bh = stats.At<int>(i, (int)ConnectedComponentsTypes.Height);
            float peak = 0;
            for (int r = topRow; r < topRow + bh; r++)
            {
                int* lab = (int*)labels.Ptr(r);
                float* t = (float*)top.Ptr(r);
                for (int c = left; c < left + bw; c++)
                {
                    if (lab[c] != i) continue;
                    double w = t[c];
                    sw[i] += w; swx[i] += w * c; swy[i] += w * r;
                    peak = Math.Max(peak, t[c]);
                }
            }
            for (int r = topRow; r < topRow + bh; r++)
            {
                int* lab = (int*)labels.Ptr(r);
                float* t = (float*)top.Ptr(r);
                for (int c = left; c < left + bw; c++)
                    if (lab[c] == i && t[c] >= peak / 2) core[i]++;
            }
        }

        int fullW = frame32f.Cols, fullH = frame32f.Rows;
        double sx = (double)fullW / cols, sy = (double)fullH / rows, s = Math.Sqrt(sx * sy);
        var result = new List<Star>();
        for (int i = 1; i < n; i++)
        {
            if (area[i] < MinArea || area[i] > MaxArea || sw[i] <= 0) continue;
            double x = (swx[i] / sw[i] + 0.5) * sx - 0.5, y = (swy[i] / sw[i] + 0.5) * sy - 0.5;
            int px = Math.Clamp((int)Math.Round(x), 0, fullW - 1), py = Math.Clamp((int)Math.Round(y), 0, fullH - 1);
            if (excludeMask8U != null && *(byte*)excludeMask8U.Ptr(py, px) != 0) continue;
            result.Add(new Star((float)x, (float)y, (float)(Math.Sqrt(core[i] / Math.PI) * s), MaxColor(frame32f, px, py)));
        }
        return result;
    }

    /// <summary>Per-channel max of the 3×3 window around (x, y), clipped to the frame.</summary>
    static unsafe Vec3f MaxColor(Mat frame, int x, int y)
    {
        float b = float.MinValue, g = float.MinValue, r = float.MinValue;
        for (int yy = Math.Max(0, y - 1); yy <= Math.Min(frame.Rows - 1, y + 1); yy++)
        {
            float* row = (float*)frame.Ptr(yy);
            for (int xx = Math.Max(0, x - 1); xx <= Math.Min(frame.Cols - 1, x + 1); xx++)
            {
                b = Math.Max(b, row[3 * xx]); g = Math.Max(g, row[3 * xx + 1]); r = Math.Max(r, row[3 * xx + 2]);
            }
        }
        return new Vec3f(b, g, r);
    }
}
