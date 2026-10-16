using System.Runtime.InteropServices;
using OpenCvSharp;

namespace Orbitale.Engine.Trails;

/// <summary>Trail detection settings. <see cref="Sigma"/> is the residual threshold in robust noise sigmas.</summary>
public sealed record TrailSettings(double Sensitivity = 0.6)
{
    public double Sigma => 4.0 - 2.5 * Sensitivity;
}

public readonly record struct LineSegment(float X1, float Y1, float X2, float Y2);

/// <summary>Detection result at detection scale. Owns <see cref="Mask8U"/> (255 = trail).</summary>
public sealed record TrailDetection(Mat Mask8U, IReadOnlyList<LineSegment> Segments) : IDisposable
{
    public void Dispose() => Mask8U.Dispose();
}

/// <summary>
/// Port of the prototype's <c>detect_trails</c> (remove_trails.py). Runs on gray float frames at
/// detection scale (long side ≤ 2000 px), with the prototype's validated parameters.
/// </summary>
public static class TrailDetector
{
    const int StarDilate = 7;       // px, must exceed star drift between frames
    const int MinBlob = 4;          // drop residual blobs smaller than this (px)
    const double MinLength = 0.03;  // fraction of the long side
    const int MaxGap = 25;          // px gap allowed in dashed (blinking) trails
    const double MinFill = 0.35;    // fraction of a segment that must be lit
    const int LineWidth = 7;        // mask line thickness (px)
    const int MaskDilate = 5;       // grow over the trail's halo (px)

    public static TrailDetection Detect(Mat frameGray32f, Mat refGray32f, TrailSettings s)
    {
        ArgumentNullException.ThrowIfNull(frameGray32f);
        ArgumentNullException.ThrowIfNull(refGray32f);
        ArgumentNullException.ThrowIfNull(s);
        if (frameGray32f.Type() != MatType.CV_32FC1 || refGray32f.Type() != MatType.CV_32FC1)
            throw new ArgumentException("Trail detection needs CV_32FC1 gray frames.");
        if (frameGray32f.Size() != refGray32f.Size())
            throw new ArgumentException("Frame and reference sizes differ.");

        using var binary = Binarize(frameGray32f, refGray32f, s.Sigma);
        RemoveSmallBlobs(binary);

        int h = binary.Rows, w = binary.Cols;
        int minLen = (int)(MinLength * Math.Max(h, w));
        // OpenCvSharp already returns one entry per segment, so the prototype's reshape(-1, 4) is implicit.
        var lines = Cv2.HoughLinesP(binary, 1, Math.PI / 720, Math.Max(20, minLen / 2), minLen, MaxGap);

        var mask = new Mat(h, w, MatType.CV_8UC1, Scalar.All(0));
        try
        {
            var kept = new List<LineSegment>();
            foreach (var l in lines)
            {
                if (Fill(binary, l.P1.X, l.P1.Y, l.P2.X, l.P2.Y) is double fill && fill < MinFill) continue;
                kept.Add(new LineSegment(l.P1.X, l.P1.Y, l.P2.X, l.P2.Y));
                Cv2.Line(mask, l.P1, l.P2, Scalar.All(255), LineWidth);
            }
            if (kept.Count > 0)
            {
                using var k = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(MaskDilate, MaskDilate));
                Cv2.Dilate(mask, mask, k);
            }
            return new TrailDetection(mask, kept);
        }
        catch
        {
            mask.Dispose();
            throw;
        }
    }

    /// <summary>Star-tolerant residual, 3×3 blur, then a robust (median + σ·MAD) threshold. Returns 0/255 CV_8UC1.</summary>
    static Mat Binarize(Mat frame, Mat reference, double sigma)
    {
        using var residual = new Mat();
        using (var k = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(StarDilate, StarDilate)))
        using (var refDil = new Mat())
        {
            // A star that moved by <= StarDilate/2 px is still covered by the dilated reference.
            Cv2.Dilate(reference, refDil, k);
            Cv2.Subtract(frame, refDil, residual);
        }
        Cv2.GaussianBlur(residual, residual, new Size(3, 3), 0);

        var values = new float[residual.Rows * residual.Cols];
        Marshal.Copy(residual.Data, values, 0, values.Length); // fresh Mat from Subtract: continuous
        float med = Median(values);
        for (int i = 0; i < values.Length; i++) values[i] = MathF.Abs(values[i] - med);
        // float32 arithmetic, as numpy does on the prototype's float32 residual.
        float mad = Median(values) * 1.4826f + 1e-6f;
        float threshold = med + (float)sigma * mad;

        var binary = new Mat();
        Cv2.Compare(residual, Scalar.All(threshold), binary, CmpTypes.GT);
        return binary;
    }

    /// <summary>Removes 8-connected components smaller than <see cref="MinBlob"/> (moved stars, hot pixels).</summary>
    static unsafe void RemoveSmallBlobs(Mat binary)
    {
        using var labels = new Mat();
        using var stats = new Mat();
        using var centroids = new Mat();
        int n = Cv2.ConnectedComponentsWithStats(binary, labels, stats, centroids, PixelConnectivity.Connectivity8);
        var small = new bool[n];
        bool any = false;
        for (int i = 1; i < n; i++)
            any |= small[i] = stats.At<int>(i, (int)ConnectedComponentsTypes.Area) < MinBlob;
        if (!any) return;
        int cols = binary.Cols;
        Parallel.For(0, binary.Rows, r =>
        {
            int* lab = (int*)labels.Ptr(r);
            byte* b = (byte*)binary.Ptr(r);
            for (int c = 0; c < cols; c++)
                if (small[lab[c]]) b[c] = 0;
        });
    }

    /// <summary>
    /// Fraction of samples along the segment where the binary residual (or its ±1 row neighbour,
    /// for anti-aliased thin trails) is lit. Sampling mirrors numpy's linspace(..., length).astype(int).
    /// </summary>
    static double Fill(Mat binary, int x1, int y1, int x2, int y2)
    {
        int length = (int)Math.Sqrt((double)(x2 - x1) * (x2 - x1) + (double)(y2 - y1) * (y2 - y1));
        if (length == 0) return double.NaN; // numpy: mean of empty is NaN, which never fails the fill test
        int h = binary.Rows, lit = 0;
        int div = length - 1;
        double stepX = div > 0 ? (double)(x2 - x1) / div : 0, stepY = div > 0 ? (double)(y2 - y1) / div : 0;
        for (int i = 0; i < length; i++)
        {
            int x = i == div && div > 0 ? x2 : (int)(i * stepX + x1);
            int y = i == div && div > 0 ? y2 : (int)(i * stepY + y1);
            if (binary.At<byte>(y, x) > 0
                || binary.At<byte>(Math.Clamp(y + 1, 0, h - 1), x) > 0
                || binary.At<byte>(Math.Clamp(y - 1, 0, h - 1), x) > 0)
                lit++;
        }
        return (double)lit / length;
    }

    /// <summary>numpy-style median (mean of the two middle values for even counts). Reorders <paramref name="a"/>.</summary>
    static float Median(float[] a)
    {
        int n = a.Length, k = n / 2;
        float hi = Select(a, k);
        if ((n & 1) == 1) return hi;
        float lo = float.MinValue;
        for (int i = 0; i < k; i++) if (a[i] > lo) lo = a[i]; // after Select, a[0..k) <= a[k]
        return (lo + hi) * 0.5f;
    }

    /// <summary>Quickselect: places the k-th smallest at a[k], smaller-or-equal values before it.</summary>
    static float Select(float[] a, int k)
    {
        int lo = 0, hi = a.Length - 1;
        while (lo < hi)
        {
            float pivot = a[lo + (hi - lo) / 2];
            int i = lo, j = hi;
            while (i <= j)
            {
                while (a[i] < pivot) i++;
                while (a[j] > pivot) j--;
                if (i <= j) { (a[i], a[j]) = (a[j], a[i]); i++; j--; }
            }
            if (k <= j) hi = j;
            else if (k >= i) lo = i;
            else break;
        }
        return a[k];
    }
}
