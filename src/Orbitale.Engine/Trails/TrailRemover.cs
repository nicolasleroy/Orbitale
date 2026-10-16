using OpenCvSharp;
using Orbitale.Engine.Imaging;

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
    public const int DetectionLongSide = DetectionScale.LongSide;

    const int Feather = 7;                  // Gaussian feather kernel at detection scale
    const double SideOffset = 8.0;          // static-line guard: background samples beside the segment, detection px
    const double StaticContrastRatio = 0.5; // median contrast at or above this fraction of the frame's = static
    const int StarMaxSide = 15;             // star blob bounding box, detection-scale px
    const double StarMaxAspect = 2.0;       // axis ratio from second moments (eigenvalue ratio <= 4)
    const double StarAxisClearance = 3.0;   // blobs centred closer than this to a trail axis are trail, detection px
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

        int fullW = frame.Cols, fullH = frame.Rows;
        var detSize = DetectionScale.For(frame.Size());

        using var frameGray = DetectionScale.ToGray(frame, detSize);
        using var medianGray = DetectionScale.ToGray(median, detSize);
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
            ProtectStars(weight, frameGray, medianGray, trailMask, transient, s.Sigma, sx, sy);
            return new TrailCleanResult(Blend(frame, median, weight), segments);
        }
    }

    /// <summary>
    /// The removed-trail mask at full resolution (CV_8UC1, 0/255) for segments in full-resolution coordinates: the
    /// detection mask's width-7 lines and 5 px ellipse dilate, both scaled by full/detection long side. All zero when
    /// there are no segments. Used to keep removed trails out of star detection.
    /// </summary>
    public static Mat FullResMask(Size fullSize, IReadOnlyList<LineSegment> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        var mask = new Mat(fullSize, MatType.CV_8UC1, Scalar.All(0));
        if (segments.Count == 0) return mask;
        var det = DetectionScale.For(fullSize);
        double scale = (double)Math.Max(fullSize.Width, fullSize.Height) / Math.Max(det.Width, det.Height);
        int width = Math.Max(1, (int)Math.Round(TrailDetector.LineWidth * scale));
        int dilate = (int)Math.Round(TrailDetector.MaskDilate * scale) | 1;
        foreach (var g in segments)
            Cv2.Line(mask, new Point((int)Math.Round(g.X1), (int)Math.Round(g.Y1)), new Point((int)Math.Round(g.X2), (int)Math.Round(g.Y2)),
                     Scalar.All(255), width);
        using var k = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(dilate, dilate));
        Cv2.Dilate(mask, mask, k);
        return mask;
    }

    /// <summary>
    /// A trail is new light:along the segment the frame must be brighter than beside it (±8 px, outside the
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
    /// the stars inside the trail corridor. Finds the frame's own compact residual blobs (frame − median) that
    /// reach into the trail mask at detection scale and zeroes the repair weight in a feathered disc around each one.
    /// Elongated blobs (second-moment axis ratio > 2) and blobs on a trail axis (dashes) stay repaired, and so do
    /// their pixels inside a star's disc.
    /// A star lying on the trail line merges with the trail blob and is not protected.
    /// </summary>
    static unsafe void ProtectStars(Mat weight, Mat frameGray, Mat medianGray, Mat trailMask, IReadOnlyList<LineSegment> axes,
                                    double sigma, double sx, double sy)
    {
        using var residual = new Mat();
        Cv2.Subtract(frameGray, medianGray, residual);
        float threshold = TrailDetector.RobustThreshold(residual, sigma);
        using var binary = new Mat();
        Cv2.Compare(residual, Scalar.All(threshold), binary, CmpTypes.GT);

        // Blobs are measured whole (not clipped to the corridor, which would make a star at its edge look
        // elongated). A star is a compact blob that reaches into the trail mask and is not on a trail axis.
        using var labels = new Mat();
        using var stats = new Mat();
        using var centroids = new Mat();
        int n = Cv2.ConnectedComponentsWithStats(binary, labels, stats, centroids, PixelConnectivity.Connectivity8);
        int detRows = binary.Rows, detCols = binary.Cols;
        var inMask = new bool[n];
        var sxx = new double[n]; var syy = new double[n]; var sxy = new double[n];   // second moments about the centroid
        var ccx = new double[n]; var ccy = new double[n];
        for (int i = 1; i < n; i++) { ccx[i] = centroids.At<double>(i, 0); ccy[i] = centroids.At<double>(i, 1); }
        for (int r = 0; r < detRows; r++)
        {
            int* lab = (int*)labels.Ptr(r);
            byte* m = (byte*)trailMask.Ptr(r);
            for (int c = 0; c < detCols; c++)
            {
                int l = lab[c];
                if (l == 0) continue;
                if (m[c] != 0) inMask[l] = true;
                double dx = c - ccx[l], dy = r - ccy[l];
                sxx[l] += dx * dx; syy[l] += dy * dy; sxy[l] += dx * dy;
            }
        }
        var star = new bool[n];
        bool anyStar = false;
        for (int i = 1; i < n; i++)
        {
            if (!inMask[i]) continue;
            int bw = stats.At<int>(i, (int)ConnectedComponentsTypes.Width), bh = stats.At<int>(i, (int)ConnectedComponentsTypes.Height);
            int area = stats.At<int>(i, (int)ConnectedComponentsTypes.Area);
            if (area < TrailDetector.MinBlob || bw > StarMaxSide || bh > StarMaxSide) continue;
            // Elongation from the covariance eigenvalues (a bounding box is square for a 45 deg dash).
            double a = sxx[i] / area, d = syy[i] / area, b = sxy[i] / area;
            double mid = (a + d) / 2, half = Math.Sqrt((a - d) * (a - d) / 4 + b * b);
            double lmax = mid + half, lmin = mid - half;
            if (lmax > StarMaxAspect * StarMaxAspect * lmin) continue;
            // Dashes sit on the axis; stars that close are lost anyway (they merge with a solid trail).
            if (axes.Any(g => DistanceToSegment(ccx[i], ccy[i], g) < StarAxisClearance)) continue;
            anyStar |= star[i] = true;
        }
        if (!anyStar) return;

        // Pixels of elongated (trail) blobs, grown by 1 px: never protected, even inside a star's disc.
        using var trail = new Mat(detRows, detCols, MatType.CV_8UC1, Scalar.All(0));
        for (int r = 0; r < detRows; r++)
        {
            int* lab = (int*)labels.Ptr(r);
            byte* t = (byte*)trail.Ptr(r);
            for (int c = 0; c < detCols; c++)
                if (lab[c] > 0 && !star[lab[c]]) t[c] = 255;
        }
        using (var k = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(3, 3)))
            Cv2.Dilate(trail, trail, k);

        double scale = Math.Max(sx, sy);
        int fullW = weight.Cols, fullH = weight.Rows;
        for (int i = 1; i < n; i++)
        {
            if (!star[i]) continue;
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

    static double DistanceToSegment(double x, double y, LineSegment g)
    {
        double dx = g.X2 - g.X1, dy = g.Y2 - g.Y1, l2 = dx * dx + dy * dy;
        double t = l2 == 0 ? 0 : Math.Clamp(((x - g.X1) * dx + (y - g.Y1) * dy) / l2, 0, 1);
        double px = g.X1 + t * dx - x, py = g.Y1 + t * dy - y;
        return Math.Sqrt(px * px + py * py);
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
