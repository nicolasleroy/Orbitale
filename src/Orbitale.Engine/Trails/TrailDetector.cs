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
    internal const int StarDilate = 7; // px, must exceed star drift between frames
    internal const int MinBlob = 4; // drop residual blobs smaller than this (px)
    internal const double MinLength = 0.03;  // fraction of the long side
    internal const double ShortMinLength = 0.01;   // aligned path: Euclidean minimum, fraction of the long side (slow planes),
    const int ShortMinPixels = 20;                 // and at least 20 px (= 0.01 of the 2000 px detection long side)
    const double MergeAngleDeg = 2;      // distinct lines: pieces within this angle,
    const double MergeDistance = 4;      // this perpendicular distance (detection px)
    const double MergeGap = 25;          // and overlapping or this close end to end (detection px) are one line
    const int MaxGap = 25;          // px gap allowed in dashed (blinking) trails
    const double MinFill = 0.35;    // fraction of a segment that must be lit
    internal const int LineWidth = 7;        // mask line thickness (px)
    internal const int MaskDilate = 5;       // grow over the trail's halo (px)

    public static TrailDetection Detect(Mat frameGray32f, Mat refGray32f, TrailSettings s)
    {
        ArgumentNullException.ThrowIfNull(frameGray32f);
        ArgumentNullException.ThrowIfNull(refGray32f);
        ArgumentNullException.ThrowIfNull(s);
        if (frameGray32f.Type() != MatType.CV_32FC1 || refGray32f.Type() != MatType.CV_32FC1)
            throw new ArgumentException("Trail detection needs CV_32FC1 gray frames.");
        if (frameGray32f.Size() != refGray32f.Size())
            throw new ArgumentException("Frame and reference sizes differ.");

        using var residual = StarTolerantResidual(frameGray32f, refGray32f, StarDilate);
        return DetectResidual(residual, s);
    }

    /// <summary>
    /// The detection pipeline after the residual: 3×3 blur, robust threshold, small-blob removal, HoughLinesP and the fill
    /// check. <paramref name="residual"/> (CV_32FC1, not modified) is "frame − star-tolerant reference" at detection scale.
    /// With <paramref name="shortPieces"/> (the sky-aligned path) the minimum length is <see cref="ShortMinLength"/> of
    /// the long side (at least 20 px), measured as a Euclidean length: OpenCV's HoughLinesP checks its minimum per axis (|dx| or |dy|),
    /// which would drop a diagonal up to √2 times longer, so it is given min/√2 and the length is checked here.
    /// Otherwise it is the prototype's 0.03 of the long side, passed to HoughLinesP as is.
    /// </summary>
    internal static TrailDetection DetectResidual(Mat residual, TrailSettings s, bool shortPieces = false)
    {
        using var binary = Binarize(residual, s.Sigma);
        RemoveSmallBlobs(binary);

        int h = binary.Rows, w = binary.Cols;
        int minLen = shortPieces ? Math.Max(ShortMinPixels, (int)(ShortMinLength * Math.Max(h, w))) : (int)(MinLength * Math.Max(h, w));
        int houghMinLen = shortPieces ? (int)Math.Floor(minLen / Math.Sqrt(2)) : minLen;
        // OpenCvSharp already returns one entry per segment, so the prototype's reshape(-1, 4) is implicit.
        var lines = Cv2.HoughLinesP(binary, 1, Math.PI / 720, Math.Max(20, minLen / 2), houghMinLen, MaxGap);

        var kept = new List<LineSegment>();
        foreach (var l in lines)
        {
            if (shortPieces && Math.Sqrt((double)(l.P2.X - l.P1.X) * (l.P2.X - l.P1.X) + (double)(l.P2.Y - l.P1.Y) * (l.P2.Y - l.P1.Y)) < minLen) continue;
            if (Fill(binary, l.P1.X, l.P1.Y, l.P2.X, l.P2.Y) is double fill && fill < MinFill) continue;
            kept.Add(new LineSegment(l.P1.X, l.P1.Y, l.P2.X, l.P2.Y));
        }
        return new TrailDetection(DrawMask(binary.Size(), kept), kept);
    }

    /// <summary>
    /// Merges Hough pieces of the same trail into distinct lines: pieces within 2° of each other, whose ends lie within
    /// 4 px of the longer one's line, and that overlap or are at most 25 px apart end to end along it, become one line
    /// spanning their extreme ends. Repeats until nothing merges. Same units in and out (detection px).
    /// </summary>
    internal static List<LineSegment> MergeCollinear(IReadOnlyList<LineSegment> pieces)
    {
        var lines = pieces.ToList();
        for (bool merged = true; merged;)
        {
            merged = false;
            for (int i = 0; i < lines.Count && !merged; i++)
                for (int j = i + 1; j < lines.Count && !merged; j++)
                    if (TryMerge(lines[i], lines[j], out var m)) { lines[i] = m; lines.RemoveAt(j); merged = true; }
        }
        return lines;
    }

    static bool TryMerge(LineSegment a, LineSegment b, out LineSegment merged)
    {
        merged = default;
        double La = Length(a), Lb = Length(b);
        if (Lb > La) (a, b, La, Lb) = (b, a, Lb, La);   // a is the longer one: its line is the reference
        if (La < 1e-9) return false;
        double ux = (a.X2 - a.X1) / La, uy = (a.Y2 - a.Y1) / La;
        if (Lb > 1e-9)
        {
            double cos = Math.Abs(ux * (b.X2 - b.X1) / Lb + uy * (b.Y2 - b.Y1) / Lb);
            if (Math.Acos(Math.Min(1, cos)) * 180 / Math.PI > MergeAngleDeg) return false;
        }
        double Perp(double x, double y) => Math.Abs(-uy * (x - a.X1) + ux * (y - a.Y1));
        if (Perp(b.X1, b.Y1) > MergeDistance || Perp(b.X2, b.Y2) > MergeDistance) return false;
        double T(double x, double y) => ux * (x - a.X1) + uy * (y - a.Y1);
        double b0 = Math.Min(T(b.X1, b.Y1), T(b.X2, b.Y2)), b1 = Math.Max(T(b.X1, b.Y1), T(b.X2, b.Y2));
        if (Math.Max(0, Math.Max(b0, 0) - Math.Min(b1, La)) > MergeGap) return false;
        var ends = new[] { (a.X1, a.Y1), (a.X2, a.Y2), (b.X1, b.Y1), (b.X2, b.Y2) };
        var lo = ends.MinBy(p => T(p.Item1, p.Item2)); var hi = ends.MaxBy(p => T(p.Item1, p.Item2));
        merged = new LineSegment(lo.Item1, lo.Item2, hi.Item1, hi.Item2);
        return true;
    }

    static double Length(LineSegment g) => Math.Sqrt((double)(g.X2 - g.X1) * (g.X2 - g.X1) + (double)(g.Y2 - g.Y1) * (g.Y2 - g.Y1));

    /// <summary>
    /// frame − dilate(reference, <paramref name="dilate"/> px ellipse): a star of the reference that moved by at most
    /// dilate/2 px is still covered. CV_32FC1, owned by the caller.
    /// </summary>
    internal static Mat StarTolerantResidual(Mat frameGray32f, Mat refGray32f, int dilate)
    {
        var residual = new Mat();
        using var k = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(dilate, dilate));
        using var refDil = new Mat();
        Cv2.Dilate(refGray32f, refDil, k);
        Cv2.Subtract(frameGray32f, refDil, residual);
        return residual;
    }

    /// <summary>The prototype's mask: width-7 lines, then a 5 px ellipse dilate when any segment is kept.</summary>
    internal static Mat DrawMask(Size size, IReadOnlyList<LineSegment> segments)
    {
        var mask = new Mat(size, MatType.CV_8UC1, Scalar.All(0));
        foreach (var g in segments)
            Cv2.Line(mask, new Point((int)g.X1, (int)g.Y1), new Point((int)g.X2, (int)g.Y2), Scalar.All(255), LineWidth);
        if (segments.Count > 0)
        {
            using var k = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(MaskDilate, MaskDilate));
            Cv2.Dilate(mask, mask, k);
        }
        return mask;
    }

    /// <summary>3×3 blur of the residual, then a robust (median + σ·MAD) threshold. Returns 0/255 CV_8UC1.</summary>
    static Mat Binarize(Mat residual, double sigma)
    {
        using var blurred = new Mat();
        Cv2.GaussianBlur(residual, blurred, new Size(3, 3), 0);

        float threshold = RobustThreshold(blurred, sigma);

        var binary = new Mat();
        Cv2.Compare(blurred, Scalar.All(threshold), binary, CmpTypes.GT);
        return binary;
    }

    /// <summary>
    /// The prototype's adaptive threshold: median + σ·MAD·1.4826 (+1e-6), in float32 arithmetic as numpy
    /// does on a float32 residual. <paramref name="residual"/> must be a continuous CV_32FC1 Mat.
    /// </summary>
    internal static float RobustThreshold(Mat residual, double sigma)
    {
        if (residual.Type() != MatType.CV_32FC1 || !residual.IsContinuous())
            throw new ArgumentException("Residual must be a continuous CV_32FC1 Mat.", nameof(residual));
        var values = new float[residual.Rows * residual.Cols];
        Marshal.Copy(residual.Data, values, 0, values.Length);
        float med = Median(values);
        for (int i = 0; i < values.Length; i++) values[i] = MathF.Abs(values[i] - med);
        float mad = Median(values) * 1.4826f + 1e-6f;
        return med + (float)sigma * mad;
    }

    /// <summary>Robust noise sigma of a continuous CV_32FC1 Mat: MAD·1.4826 (the spread <see cref="RobustThreshold"/> scales).</summary>
    internal static float RobustSigma(Mat residual)
    {
        if (residual.Type() != MatType.CV_32FC1 || !residual.IsContinuous())
            throw new ArgumentException("Residual must be a continuous CV_32FC1 Mat.", nameof(residual));
        var values = new float[residual.Rows * residual.Cols];
        Marshal.Copy(residual.Data, values, 0, values.Length);
        float med = Median(values);
        for (int i = 0; i < values.Length; i++) values[i] = MathF.Abs(values[i] - med);
        return Median(values) * 1.4826f;
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
