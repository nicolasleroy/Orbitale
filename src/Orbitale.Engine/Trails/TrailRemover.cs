using OpenCvSharp;

namespace Orbitale.Engine.Trails;

/// <summary>Cleaned frame (owned, disposed with the result) and the kept trail segments in full-resolution coordinates.</summary>
public sealed record TrailCleanResult(Mat Cleaned, IReadOnlyList<LineSegment> Segments) : IDisposable
{
    public void Dispose() => Cleaned.Dispose();
}

/// <summary>
/// Removes plane and satellite trails from one frame (port of the prototype's detect + <c>repair</c>):
/// detection on a copy whose long side is at most <see cref="DetectionLongSide"/> px, repair at full
/// resolution by blending in the temporal median of the neighbours under a feathered mask.
/// </summary>
public static class TrailRemover
{
    /// <summary>The scale the prototype was validated at.</summary>
    public const int DetectionLongSide = 2000;

    const int Feather = 7; // Gaussian feather kernel at detection scale

    public static TrailCleanResult Clean(Mat frame, IReadOnlyList<Mat> neighbours, TrailSettings s)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(neighbours);
        ArgumentNullException.ThrowIfNull(s);
        if (frame.Type() != MatType.CV_32FC3)
            throw new ArgumentException($"Frame is {frame.Type()}, expected CV_32FC3.", nameof(frame));
        if (neighbours.Count > 0 && neighbours[0].Size() != frame.Size())
            throw new ArgumentException("Neighbours must have the frame's size.", nameof(neighbours));

        using var median = TemporalMedian.Compute(neighbours);

        int fullW = frame.Cols, fullH = frame.Rows, fullLong = Math.Max(fullW, fullH);
        bool downscale = fullLong > DetectionLongSide;
        var detSize = downscale
            ? new Size((int)Math.Round((double)fullW * DetectionLongSide / fullLong), (int)Math.Round((double)fullH * DetectionLongSide / fullLong))
            : frame.Size();

        TrailDetection detection;
        using (var frameGray = ToDetectionGray(frame, downscale, detSize))
        using (var medianGray = ToDetectionGray(median, downscale, detSize))
            detection = TrailDetector.Detect(frameGray, medianGray, s);

        using (detection)
        {
            if (detection.Segments.Count == 0)
                return new TrailCleanResult(frame.Clone(), []);

            double sx = (double)fullW / detSize.Width, sy = (double)fullH / detSize.Height;
            // Pixel-centre mapping (consistent with Area/Linear resize); identity when not downscaled.
            float X(float x) => (float)((x + 0.5) * sx - 0.5);
            float Y(float y) => (float)((y + 0.5) * sy - 0.5);
            var segments = detection.Segments.Select(g => new LineSegment(X(g.X1), Y(g.Y1), X(g.X2), Y(g.Y2))).ToList();

            using var weight = FeatheredWeight(detection.Mask8U, frame.Size(), Math.Max(sx, sy));
            return new TrailCleanResult(Blend(frame, median, weight), segments);
        }
    }

    static Mat ToDetectionGray(Mat bgr, bool downscale, Size detSize)
    {
        var gray = new Mat();
        if (!downscale)
        {
            Cv2.CvtColor(bgr, gray, ColorConversionCodes.BGR2GRAY);
            return gray;
        }
        using var small = new Mat();
        Cv2.Resize(bgr, small, detSize, 0, 0, InterpolationFlags.Area);
        Cv2.CvtColor(small, gray, ColorConversionCodes.BGR2GRAY);
        return gray;
    }

    /// <summary>Mask (0/255, detection scale) to a 0..1 float weight at full size, feathered by Gaussian odd(7·scale).</summary>
    static Mat FeatheredWeight(Mat mask8U, Size fullSize, double scale)
    {
        var w = new Mat();
        try
        {
            mask8U.ConvertTo(w, MatType.CV_32FC1, 1.0 / 255.0);
            if (w.Size() != fullSize)
                Cv2.Resize(w, w, fullSize, 0, 0, InterpolationFlags.Linear);
            int k = (int)Math.Round(Feather * scale);
            if (k % 2 == 0) k++;
            Cv2.GaussianBlur(w, w, new Size(k, k), 0);
            return w;
        }
        catch
        {
            w.Dispose();
            throw;
        }
    }

    /// <summary>out = frame·(1−w) + median·w, per channel.</summary>
    static unsafe Mat Blend(Mat frame, Mat median, Mat weight)
    {
        var result = new Mat(frame.Rows, frame.Cols, MatType.CV_32FC3);
        int cols = frame.Cols;
        Parallel.For(0, frame.Rows, r =>
        {
            float* f = (float*)frame.Ptr(r);
            float* m = (float*)median.Ptr(r);
            float* wr = (float*)weight.Ptr(r);
            float* o = (float*)result.Ptr(r);
            for (int c = 0; c < cols; c++)
            {
                float a = wr[c], b = 1f - a;
                int i = c * 3;
                o[i] = f[i] * b + m[i] * a;
                o[i + 1] = f[i + 1] * b + m[i + 1] * a;
                o[i + 2] = f[i + 2] * b + m[i + 2] * a;
            }
        });
        return result;
    }
}
