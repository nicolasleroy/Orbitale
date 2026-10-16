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

    const int Feather = 7;                  // Gaussian feather kernel at detection scale
    const double SideOffset = 8.0;          // static-line guard: background samples beside the segment, detection px
    const double StaticContrastRatio = 0.5; // median contrast at or above this fraction of the frame's = static
    const int StarMaxSide = 6;              // star blob bounding box, detection-scale px
    const double StarMaxAspect = 2.0;       // longer / shorter bbox side
    const double StarMargin = 2.0;          // protected disc = blob radius + margin, full-res px
    const double StarFeather = 2.0;         // linear fall-off beyond the disc, full-res px

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

        using var frameGray = ToDetectionGray(frame, downscale, detSize);
        using var medianGray = ToDetectionGray(median, downscale, detSize);
        using (var detection = TrailDetector.Detect(frameGray, medianGray, s))
        {
            // Guard against static structures (a lit road, a wire, the horizon): keep only segments that are
            // bright in this frame but not in the neighbour median. Detection itself stays a pure prototype port.
            var transient = detection.Segments.Where(g => IsTransient(frameGray, medianGray, g)).ToList();
            if (transient.Count == 0)
                return new TrailCleanResult(frame.Clone(), []);
            using var trailMask = transient.Count == detection.Segments.Count
                ? detection.Mask8U.Clone()
                : TrailDetector.DrawMask(detection.Mask8U.Size(), transient);

            double sx = (double)fullW / detSize.Width, sy = (double)fullH / detSize.Height;
            // Pixel-centre mapping (consistent with Area/Linear resize); identity when not downscaled.
            float X(float x) => (float)((x + 0.5) * sx - 0.5);
            float Y(float y) => (float)((y + 0.5) * sy - 0.5);
            var segments = transient.Select(g => new LineSegment(X(g.X1), Y(g.Y1), X(g.X2), Y(g.Y2))).ToList();

            using var weight = FeatheredWeight(trailMask, frame.Size(), Math.Max(sx, sy));
            ProtectStars(weight, frameGray, medianGray, trailMask, s.Sigma, sx, sy);
            return new TrailCleanResult(Blend(frame, median, weight), segments);
        }
    }

    /// <summary>
    /// A trail is new light: along the segment the frame must be brighter than beside it (±8 px, outside the
    /// mask corridor), and the neighbour median must show less than half of that contrast. A static bright
    /// line has the same contrast in both and is rejected.
    /// </summary>
    static bool IsTransient(Mat frameGray, Mat medianGray, LineSegment g)
    {
        double dx = g.X2 - g.X1, dy = g.Y2 - g.Y1, len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 1) return true;
        double nx = -dy / len * SideOffset, ny = dx / len * SideOffset;
        int rows = frameGray.Rows, cols = frameGray.Cols;
        float At(Mat m, double x, double y) => m.At<float>(Math.Clamp((int)Math.Round(y), 0, rows - 1), Math.Clamp((int)Math.Round(x), 0, cols - 1));
        double frameContrast = 0, medianContrast = 0;
        int n = (int)len;
        for (int i = 0; i <= n; i++)
        {
            double x = g.X1 + dx * i / len, y = g.Y1 + dy * i / len;
            frameContrast += At(frameGray, x, y) - (At(frameGray, x + nx, y + ny) + At(frameGray, x - nx, y - ny)) / 2;
            medianContrast += At(medianGray, x, y) - (At(medianGray, x + nx, y + ny) + At(medianGray, x - nx, y - ny)) / 2;
        }
        return frameContrast > 0 && medianContrast < StaticContrastRatio * frameContrast;
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

    /// <summary>
    /// The neighbour median does not hold this frame's stars where they drifted, so a plain repair would erase
    /// the stars inside the trail corridor. Finds the frame's own compact residual blobs (frame − median) under
    /// the trail mask at detection scale and zeroes the repair weight in a feathered disc around each one.
    /// Elongated blobs (the trail, its dashes) stay repaired, and so do their pixels inside a star's disc.
    /// A star lying on the trail line merges with the trail blob and is not protected.
    /// </summary>
    static unsafe void ProtectStars(Mat weight, Mat frameGray, Mat medianGray, Mat trailMask, double sigma, double sx, double sy)
    {
        using var residual = new Mat();
        Cv2.Subtract(frameGray, medianGray, residual);
        float threshold = TrailDetector.RobustThreshold(residual, sigma);
        using var binary = new Mat();
        Cv2.Compare(residual, Scalar.All(threshold), binary, CmpTypes.GT);
        Cv2.BitwiseAnd(binary, trailMask, binary);

        using var labels = new Mat();
        using var stats = new Mat();
        using var centroids = new Mat();
        int n = Cv2.ConnectedComponentsWithStats(binary, labels, stats, centroids, PixelConnectivity.Connectivity8);
        var compact = new bool[n];
        bool anyStar = false;
        for (int i = 1; i < n; i++)
        {
            int bw = stats.At<int>(i, (int)ConnectedComponentsTypes.Width), bh = stats.At<int>(i, (int)ConnectedComponentsTypes.Height);
            int area = stats.At<int>(i, (int)ConnectedComponentsTypes.Area);
            anyStar |= compact[i] = area >= TrailDetector.MinBlob && bw <= StarMaxSide && bh <= StarMaxSide
                                    && Math.Max(bw, bh) <= StarMaxAspect * Math.Min(bw, bh);
        }
        if (!anyStar) return;

        // Pixels of elongated (trail) blobs, grown by 1 px: never protected, even inside a star's disc.
        int detRows = binary.Rows, detCols = binary.Cols;
        using var trail = new Mat(detRows, detCols, MatType.CV_8UC1, Scalar.All(0));
        for (int r = 0; r < detRows; r++)
        {
            int* lab = (int*)labels.Ptr(r);
            byte* t = (byte*)trail.Ptr(r);
            for (int c = 0; c < detCols; c++)
                if (lab[c] > 0 && !compact[lab[c]]) t[c] = 255;
        }
        using (var k = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(3, 3)))
            Cv2.Dilate(trail, trail, k);

        double scale = Math.Max(sx, sy);
        int fullW = weight.Cols, fullH = weight.Rows;
        for (int i = 1; i < n; i++)
        {
            if (!compact[i]) continue;
            int bw = stats.At<int>(i, (int)ConnectedComponentsTypes.Width), bh = stats.At<int>(i, (int)ConnectedComponentsTypes.Height);
            double cx = (centroids.At<double>(i, 0) + 0.5) * sx - 0.5, cy = (centroids.At<double>(i, 1) + 0.5) * sy - 0.5;
            double radius = Math.Max(bw, bh) / 2.0 * scale + StarMargin, outer = radius + StarFeather;
            int x0 = Math.Max(0, (int)Math.Floor(cx - outer)), x1 = Math.Min(fullW - 1, (int)Math.Ceiling(cx + outer));
            int y0 = Math.Max(0, (int)Math.Floor(cy - outer)), y1 = Math.Min(fullH - 1, (int)Math.Ceiling(cy + outer));
            for (int y = y0; y <= y1; y++)
            {
                float* w = (float*)weight.Ptr(y);
                byte* t = (byte*)trail.Ptr(Math.Min(detRows - 1, (int)((y + 0.5) / sy)));
                for (int x = x0; x <= x1; x++)
                {
                    double d = Math.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));
                    double keep = d <= radius ? 1 : Math.Max(0, 1 - (d - radius) / StarFeather);
                    if (keep > 0 && t[Math.Min(detCols - 1, (int)((x + 0.5) / sx))] == 0)
                        w[x] *= (float)(1 - keep);
                }
            }
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
