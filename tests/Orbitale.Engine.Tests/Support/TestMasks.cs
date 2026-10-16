using OpenCvSharp;
using Orbitale.Engine.Trails;

namespace Orbitale.Engine.Tests.Support;

/// <summary>Small geometric and image helpers for the trail tests.</summary>
public static class TestMasks
{
    /// <summary>Rasterises segments the way the prototype draws its mask: lines of <paramref name="width"/>, then an ellipse dilate (only if any segment).</summary>
    public static Mat FromSegments(IReadOnlyList<LineSegment> segments, Size size, int width, int dilate)
    {
        var mask = new Mat(size, MatType.CV_8UC1, Scalar.All(0));
        foreach (var s in segments)
            Cv2.Line(mask, new Point((int)s.X1, (int)s.Y1), new Point((int)s.X2, (int)s.Y2), Scalar.All(255), width);
        if (segments.Count > 0)
        {
            using var k = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(dilate, dilate));
            Cv2.Dilate(mask, mask, k);
        }
        return mask;
    }

    /// <summary>Intersection over union of the non-zero pixels of two masks (1 when both are empty).</summary>
    public static double IoU(Mat a, Mat b)
    {
        using var ia = new Mat(); using var ib = new Mat();
        Cv2.Compare(a, 0, ia, CmpTypes.GT); Cv2.Compare(b, 0, ib, CmpTypes.GT);
        using var and = new Mat(); using var or = new Mat();
        Cv2.BitwiseAnd(ia, ib, and); Cv2.BitwiseOr(ia, ib, or);
        int u = Cv2.CountNonZero(or);
        return u == 0 ? 1.0 : (double)Cv2.CountNonZero(and) / u;
    }

    /// <summary>Segment direction in degrees, folded into (-90, 90].</summary>
    public static double AngleDeg(LineSegment s)
    {
        double a = Math.Atan2(s.Y2 - s.Y1, s.X2 - s.X1) * 180 / Math.PI;
        if (a > 90) a -= 180;
        if (a <= -90) a += 180;
        return a;
    }

    /// <summary>Largest perpendicular distance of the segment's endpoints to the infinite line through p and q.</summary>
    public static double DistanceToLine(LineSegment s, (double X, double Y) p, (double X, double Y) q)
    {
        double dx = q.X - p.X, dy = q.Y - p.Y, len = Math.Sqrt(dx * dx + dy * dy);
        double D(double x, double y) => Math.Abs(dy * (x - p.X) - dx * (y - p.Y)) / len;
        return Math.Max(D(s.X1, s.Y1), D(s.X2, s.Y2));
    }

    /// <summary>Mean of R − (G+B)/2 sampled every pixel along p→q (BGR float image).</summary>
    public static double MeanRedExcessAlong(Mat bgr, (int X, int Y) p, (int X, int Y) q)
        => Along(p, q).Average(pt => { var v = bgr.At<Vec3f>(pt.Y, pt.X); return v.Item2 - (v.Item0 + v.Item1) / 2.0; });

    /// <summary>Largest per-channel absolute difference between two BGR float images, sampled along p→q.</summary>
    public static double MaxAbsDiffAlong(Mat a, Mat b, (int X, int Y) p, (int X, int Y) q)
        => Along(p, q).Max(pt =>
        {
            var u = a.At<Vec3f>(pt.Y, pt.X); var v = b.At<Vec3f>(pt.Y, pt.X);
            return Math.Max(Math.Abs(u.Item0 - v.Item0), Math.Max(Math.Abs(u.Item1 - v.Item1), Math.Abs(u.Item2 - v.Item2)));
        });

    /// <summary>Mean per-channel absolute difference between two BGR float images, sampled along p→q.</summary>
    public static double MeanAbsDiffAlong(Mat a, Mat b, (int X, int Y) p, (int X, int Y) q)
        => Along(p, q).Average(pt =>
        {
            var u = a.At<Vec3f>(pt.Y, pt.X); var v = b.At<Vec3f>(pt.Y, pt.X);
            return (Math.Abs(u.Item0 - v.Item0) + Math.Abs(u.Item1 - v.Item1) + Math.Abs(u.Item2 - v.Item2)) / 3.0;
        });

    /// <summary>Counts stars in <paramref name="region"/>: connected peaks of the gray white top-hat (9×9 ellipse) above <paramref name="threshold"/>.</summary>
    public static int CountStars(Mat bgr, Rect region, double threshold = 0.08)
    {
        using var roi = new Mat(bgr, region);
        using var gray = new Mat();
        Cv2.CvtColor(roi, gray, ColorConversionCodes.BGR2GRAY);
        using var k = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(9, 9));
        using var top = new Mat();
        Cv2.MorphologyEx(gray, top, MorphTypes.TopHat, k);
        using var bin = new Mat();
        Cv2.Threshold(top, bin, threshold, 255, ThresholdTypes.Binary);
        using var bin8 = new Mat();
        bin.ConvertTo(bin8, MatType.CV_8UC1);
        using var labels = new Mat();
        return Cv2.ConnectedComponents(bin8, labels, PixelConnectivity.Connectivity8) - 1;
    }

    static IEnumerable<(int X, int Y)> Along((int X, int Y) p, (int X, int Y) q)
    {
        int n = (int)Math.Ceiling(Math.Sqrt(Math.Pow(q.X - p.X, 2) + Math.Pow(q.Y - p.Y, 2)));
        for (int i = 0; i <= n; i++)
            yield return ((int)Math.Round(p.X + (q.X - p.X) * (double)i / n), (int)Math.Round(p.Y + (q.Y - p.Y) * (double)i / n));
    }
}
