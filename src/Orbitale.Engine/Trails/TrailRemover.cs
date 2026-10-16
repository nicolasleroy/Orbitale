using OpenCvSharp;
using Orbitale.Engine.Imaging;

namespace Orbitale.Engine.Trails;

/// <summary>Cleaned frame (owned, disposed with the result) and the kept trail segments in full-resolution coordinates.</summary>
public sealed record TrailCleanResult(Mat Cleaned, IReadOnlyList<LineSegment> Segments) : IDisposable
{
    public void Dispose() => Cleaned.Dispose();
}

/// <summary>A neighbour frame (owned by the caller) and its sky motion into the current frame (2×3 CV_64FC1, full-res px), or null when unknown.</summary>
public readonly record struct AlignedNeighbour(Mat Frame, Mat? ToCurrent)
{
    /// <summary>Optional <see cref="Frame"/> already area-downscaled to detection scale (CV_32FC3, owned by the caller): saves a 40 MP resize per use.</summary>
    internal Mat? Detection { get; init; }
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
    // Aligned reference dilate, detection px. The brief's 3 px was too small on the 40 MP data: the similarity fit leaves
    // up to ~0.8 detection px of lens-distortion misfit at the edges between consecutive frames, ~1.6 px two frames apart.
    const int AlignedStarDilate = 5;
    const int SkyBox = 15, SkyBoxWide = 61; // sky/landscape choice: error averaging boxes, detection px
    const float SkyNoiseSigmas = 3;         // sky/landscape choice: errors below this many noise sigmas count as 0
    const int RepairTile = 32;              // aligned repair tiles, detection px

    /// <summary>Cleans <paramref name="frame"/> against unaligned neighbours (the prototype's behaviour).</summary>
    public static TrailCleanResult Clean(Mat frame, IReadOnlyList<Mat> neighbours, TrailSettings s)
    {
        ArgumentNullException.ThrowIfNull(neighbours);
        return Clean(frame, neighbours.Select(n => new AlignedNeighbour(n, null)).ToList(), s, null, distinctLines: false);
    }

    /// <summary>
    /// Cleans <paramref name="frame"/> against neighbours whose sky motion into it may be known
    /// (<see cref="AlignedNeighbour.ToCurrent"/>). A pixel is a trail candidate only if it is new against both the static
    /// median of the neighbours (the landscape cancels) and the median of the neighbours with their sky rotated into this
    /// frame (the stars cancel). The repair takes the aligned median in the sky and the static median in the landscape.
    /// With an alignment, pieces down to 0.01 of the long side are kept (slow planes). When no alignment is known the
    /// detection and repair are exactly the unaligned prototype port. <see cref="TrailCleanResult.Segments"/> are the
    /// distinct lines (collinear Hough pieces merged), which is what a user counts as trails.
    /// </summary>
    public static TrailCleanResult Clean(Mat frame, IReadOnlyList<AlignedNeighbour> neighbours, TrailSettings s) => Clean(frame, neighbours, s, null);

    /// <summary><see cref="Clean(Mat, IReadOnlyList{AlignedNeighbour}, TrailSettings)"/> with the frame's detection-scale copy (CV_32FC3) at hand, or null.</summary>
    internal static TrailCleanResult Clean(Mat frame, IReadOnlyList<AlignedNeighbour> neighbours, TrailSettings s, Mat? frameDetection, bool distinctLines = true)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(neighbours);
        ArgumentNullException.ThrowIfNull(s);
        if (frame.Type() != MatType.CV_32FC3)
            throw new ArgumentException($"Frame is {frame.Type()}, expected CV_32FC3.", nameof(frame));
        foreach (var n in neighbours)
        {
            if (n.Frame is null || n.Frame.Size() != frame.Size())
                throw new ArgumentException("Neighbours must have the frame's size.", nameof(neighbours));
            if (n.ToCurrent is { } m && (m.Type() != MatType.CV_64FC1 || m.Rows != 2 || m.Cols != 3))
                throw new ArgumentException("ToCurrent must be a 2x3 CV_64FC1 affine.", nameof(neighbours));
        }

        // Owned here. In the aligned path its sky pixels under the trails become the aligned median: it is the repair source.
        using var median = TemporalMedian.Compute(neighbours.Select(n => n.Frame).ToList());

        int fullW = frame.Cols, fullH = frame.Rows;
        var detSize = DetectionScale.For(frame.Size());
        double sx = (double)fullW / detSize.Width, sy = (double)fullH / detSize.Height;

        using var frameGray = frameDetection is not null && frameDetection.Size() == detSize && frameDetection.Type() == MatType.CV_32FC3
            ? DetectionScale.ToGray(frameDetection, detSize)
            : DetectionScale.ToGray(frame, detSize);
        using var medianGray = DetectionScale.ToGray(median, detSize);
        using var alignedGray = neighbours.Any(n => n.ToCurrent is not null) ? AlignedMedianGray(neighbours, detSize, sx, sy) : null;
        using var detection = alignedGray is null
            ? TrailDetector.Detect(frameGray, medianGray, s)
            : DetectAligned(frameGray, medianGray, alignedGray, s);

        // Guard against static structures (a lit road, a wire, the horizon): keep only segments that are
        // bright in this frame but not in the neighbour median(s). Detection itself stays a pure prototype port.
        var transient = detection.Segments.Where(g => IsTransient(frameGray, medianGray, alignedGray, g)).ToList();
        if (transient.Count == 0)
            return new TrailCleanResult(frame.Clone(), []);
        using var trailMask = transient.Count == detection.Segments.Count
            ? detection.Mask8U.Clone()
            : TrailDetector.DrawMask(detection.Mask8U.Size(), transient);

        // Pixel-centre mapping (consistent with Area/Linear resize); identity when not downscaled.
        float X(float x) => (float)((x + 0.5) * sx - 0.5);
        float Y(float y) => (float)((y + 0.5) * sy - 0.5);
        // The repair masks every kept piece; the result reports distinct lines (Hough returns a trail as several pieces).
        var reported = distinctLines ? TrailDetector.MergeCollinear(transient) : transient;
        var segments = reported.Select(g => new LineSegment(X(g.X1), Y(g.Y1), X(g.X2), Y(g.Y2))).ToList();

        using var weight = FeatheredWeight(trailMask, frame.Size(), Math.Max(sx, sy));
        if (alignedGray is null)
        {
            ProtectStars(weight, frameGray, medianGray, trailMask, transient, s.Sigma, sx, sy);
        }
        else
        {
            using var sky = SkyMap(frameGray, medianGray, alignedGray, trailMask);
            using (var repairGray = medianGray.Clone())
            {
                // Star protection (second line of defence) looks for the frame's stars that the repair source lacks.
                alignedGray.CopyTo(repairGray, sky);
                ProtectStars(weight, frameGray, repairGray, trailMask, transient, s.Sigma, sx, sy);
            }
            PutAlignedSkyUnderTrails(median, neighbours, sky, trailMask, sx, sy);
        }
        return new TrailCleanResult(Blend(frame, median, weight), segments);
    }

    /// <summary>
    /// Detection residual = per-pixel min(frame − dilate(static median, 7), frame − dilate(aligned median, 3)), i.e. light
    /// that is new against both references, then the prototype's pipeline.
    /// </summary>
    static TrailDetection DetectAligned(Mat frameGray, Mat staticGray, Mat alignedGray, TrailSettings s)
    {
        using var rStatic = TrailDetector.StarTolerantResidual(frameGray, staticGray, TrailDetector.StarDilate);
        using var rAligned = TrailDetector.StarTolerantResidual(frameGray, alignedGray, AlignedStarDilate);
        Cv2.Min(rStatic, rAligned, rStatic);
        return TrailDetector.DetectResidual(rStatic, s, shortPieces: true);
    }

    /// <summary>
    /// Temporal median (CV_32FC1, detection scale) of the neighbours' gray detection-scale copies, each warped into the
    /// current frame with its <see cref="AlignedNeighbour.ToCurrent"/> (bilinear, replicated border). Neighbours without
    /// an alignment are used unwarped. (Median of grays rather than gray of the colour median: a third of the work, and
    /// only detection and the sky/landscape choice see it.)
    /// </summary>
    static Mat AlignedMedianGray(IReadOnlyList<AlignedNeighbour> neighbours, Size detSize, double sx, double sy)
    {
        var warped = new List<Mat>(neighbours.Count);
        var outside = new List<Mat?>(neighbours.Count);   // 255 where the warped neighbour saw nothing (beyond its frame)
        try
        {
            foreach (var n in neighbours)
            {
                var source = n.Detection is { } d && d.Size() == detSize && d.Type() == MatType.CV_32FC3 ? d : n.Frame;
                var gray = DetectionScale.ToGray(source, detSize);
                warped.Add(gray);
                outside.Add(null);
                if (n.ToCurrent is null) continue;
                using var m = DetectionAffine(n.ToCurrent, sx, sy);
                using var tmp = new Mat();
                Cv2.WarpAffine(gray, tmp, m, detSize, InterpolationFlags.Linear, BorderTypes.Replicate);
                tmp.CopyTo(gray);
                using var seen = new Mat();
                using (var ones = new Mat(detSize, MatType.CV_8UC1, Scalar.All(255)))
                    Cv2.WarpAffine(ones, seen, m, detSize, InterpolationFlags.Nearest, BorderTypes.Constant, Scalar.All(0));
                var o = new Mat();
                Cv2.Compare(seen, Scalar.All(0), o, CmpTypes.EQ);
                outside[^1] = o;
            }
            // Stars entering the frame at its edges are in the later neighbours only: a replicated border would make them
            // look new. Where a warped neighbour saw nothing, it takes the brightest value the others saw there.
            if (outside.Any(o => o is not null && Cv2.CountNonZero(o) > 0))
            {
                using var brightest = new Mat(detSize, MatType.CV_32FC1, Scalar.All(float.MinValue));
                for (int k = 0; k < warped.Count; k++)
                {
                    using var valid = new Mat(detSize, MatType.CV_32FC1, Scalar.All(float.MinValue));
                    if (outside[k] is { } o) { using var inside = new Mat(); Cv2.BitwiseNot(o, inside); warped[k].CopyTo(valid, inside); }
                    else warped[k].CopyTo(valid);
                    Cv2.Max(brightest, valid, brightest);
                }
                using var anySeen = new Mat();
                Cv2.Compare(brightest, Scalar.All(float.MinValue), anySeen, CmpTypes.GT);
                for (int k = 0; k < warped.Count; k++)
                {
                    if (outside[k] is not { } o) continue;
                    Cv2.BitwiseAnd(o, anySeen, o);
                    brightest.CopyTo(warped[k], o);
                }
            }
            return TemporalMedian.Compute(warped);
        }
        finally
        {
            foreach (var m in warped) m.Dispose();
            foreach (var o in outside) o?.Dispose();
        }
    }

    /// <summary>
    /// The full-resolution affine <paramref name="full"/> (p' = Ap + t) in detection px, through pixel centres
    /// (full = S(det + h) − h, h = (½, ½), S = diag(sx, sy)): A' = S⁻¹AS, t' = S⁻¹(ASh − Ah + t + h) − h.
    /// </summary>
    internal static Mat DetectionAffine(Mat full, double sx, double sy)
    {
        double a = full.At<double>(0, 0), b = full.At<double>(0, 1), tx = full.At<double>(0, 2);
        double c = full.At<double>(1, 0), d = full.At<double>(1, 1), ty = full.At<double>(1, 2);
        double ux = a * 0.5 * sx + b * 0.5 * sy - (a + b) * 0.5 + tx + 0.5;
        double uy = c * 0.5 * sx + d * 0.5 * sy - (c + d) * 0.5 + ty + 0.5;
        var m = new Mat(2, 3, MatType.CV_64FC1);
        m.Set(0, 0, a); m.Set(0, 1, b * sy / sx); m.Set(0, 2, ux / sx - 0.5);
        m.Set(1, 0, c * sx / sy); m.Set(1, 1, d); m.Set(1, 2, uy / sy - 0.5);
        return m;
    }

    /// <summary>
    /// Sky (255) / landscape (0) map at detection scale: 255 unless the static median fits the frame better than the
    /// aligned one. The errors |frame − median| are averaged over a 15×15 box using only pixels outside the trail mask (a
    /// 61×61 box where the small one sees less than half a pixel outside). Errors within 3 noise sigmas count as 0, so
    /// where both medians fit to the noise (empty sky, flat ground) the tie goes to the sky: there the choice only matters
    /// for a star hidden under the trail, which only the aligned median holds. Only the region the repair can reach is
    /// evaluated (plus the averaging margin); the rest of the map is 0.
    /// </summary>
    static Mat SkyMap(Mat frameGray, Mat staticGray, Mat alignedGray, Mat trailMask)
    {
        var map = new Mat(trailMask.Size(), MatType.CV_8UC1, Scalar.All(0));
        using var points = new Mat();
        Cv2.FindNonZero(trailMask, points);
        if (points.Empty()) return map;
        var box = Cv2.BoundingRect(points);
        int margin = (Feather + 3) / 2 + SkyBoxWide / 2 + 1;
        int x0 = Math.Max(0, box.X - margin), y0 = Math.Max(0, box.Y - margin);
        var roi = new Rect(x0, y0, Math.Min(trailMask.Cols, box.Right + margin) - x0, Math.Min(trailMask.Rows, box.Bottom + margin) - y0);
        using var f = new Mat(frameGray, roi);
        using var st = new Mat(staticGray, roi);
        using var al = new Mat(alignedGray, roi);
        using var tm = new Mat(trailMask, roi);
        using var part = SkyMapIn(f, st, al, tm);
        using var dst = new Mat(map, roi);
        part.CopyTo(dst);
        return map;
    }

    static Mat SkyMapIn(Mat frameGray, Mat staticGray, Mat alignedGray, Mat trailMask)
    {
        using var outside = new Mat();
        using (var out8 = new Mat())
        {
            Cv2.Compare(trailMask, Scalar.All(0), out8, CmpTypes.EQ);
            out8.ConvertTo(outside, MatType.CV_32FC1, 1.0 / 255);
        }
        using var eA = new Mat();
        using var eS = new Mat();
        Cv2.Subtract(frameGray, staticGray, eS);
        float noise;
        using (var sample = new Mat())
        {
            Cv2.Resize(eS, sample, new Size(Math.Max(1, eS.Cols / 4), Math.Max(1, eS.Rows / 4)), 0, 0, InterpolationFlags.Nearest);   // 1/16 of the pixels: plenty for a noise sigma
            noise = SkyNoiseSigmas * TrailDetector.RobustSigma(sample);
        }
        Cv2.Absdiff(frameGray, alignedGray, eA);
        Cv2.Absdiff(eS, Scalar.All(0), eS);
        foreach (var e in new[] { eA, eS })
        {
            Cv2.Subtract(e, Scalar.All(noise), e);
            Cv2.Max(e, Scalar.All(0), e);
            Cv2.Multiply(e, outside, e);
        }

        // Both box sums share the normaliser (the outside weight), so comparing the sums compares the outside-only means.
        Mat AlignedFitsAsWell(int box)
        {
            var k = new Size(box, box);
            using var sa = new Mat();
            using var ss = new Mat();
            Cv2.BoxFilter(eA, sa, MatType.CV_32F, k, normalize: false);
            Cv2.BoxFilter(eS, ss, MatType.CV_32F, k, normalize: false);
            var better = new Mat();
            Cv2.Compare(sa, ss, better, CmpTypes.LE);
            return better;
        }

        var map = AlignedFitsAsWell(SkyBox);
        using var weight = new Mat();
        Cv2.BoxFilter(outside, weight, MatType.CV_32F, new Size(SkyBox, SkyBox), normalize: false);
        using var blind = new Mat();
        Cv2.Compare(weight, Scalar.All(0.5), blind, CmpTypes.LT);
        if (Cv2.CountNonZero(blind) > 0)
        {
            using var wide = AlignedFitsAsWell(SkyBoxWide);
            wide.CopyTo(map, blind);
        }
        return map;
    }

    /// <summary>
    /// Replaces the sky pixels (by <paramref name="sky"/>, nearest upscale) of <paramref name="median"/> near the trails
    /// with the full-resolution aligned median. Only the 32-detection-px tiles the feathered repair weight reaches are
    /// warped: whole neighbours never are.
    /// </summary>
    static unsafe void PutAlignedSkyUnderTrails(Mat median, IReadOnlyList<AlignedNeighbour> neighbours, Mat sky, Mat trailMask, double sx, double sy)
    {
        // The feathered weight reaches about Feather/2 detection px beyond the mask (+1 for the linear upscale).
        using var reach = new Mat();
        using (var k = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(Feather + 3, Feather + 3)))
            Cv2.Dilate(trailMask, reach, k);
        int detW = sky.Cols, detH = sky.Rows, fullW = median.Cols, fullH = median.Rows;
        for (int ty = 0; ty < detH; ty += RepairTile)
            for (int tx = 0; tx < detW; tx += RepairTile)
            {
                var det = new Rect(tx, ty, Math.Min(RepairTile, detW - tx), Math.Min(RepairTile, detH - ty));
                using (var r = new Mat(reach, det))
                    if (Cv2.CountNonZero(r) == 0) continue;
                int x0 = (int)Math.Floor(det.X * sx), y0 = (int)Math.Floor(det.Y * sy);
                int x1 = Math.Min(fullW, (int)Math.Ceiling(det.Right * sx)), y1 = Math.Min(fullH, (int)Math.Ceiling(det.Bottom * sy));
                var tile = new Rect(x0, y0, x1 - x0, y1 - y0);

                var parts = new List<Mat>(neighbours.Count);
                try
                {
                    foreach (var n in neighbours)
                    {
                        if (n.ToCurrent is null) { parts.Add(new Mat(n.Frame, tile)); continue; }
                        // dst(x) = src(M⁻¹(x + tile origin)): shift the affine's output by −origin.
                        using var m = n.ToCurrent.Clone();
                        m.Set(0, 2, m.At<double>(0, 2) - tile.X);
                        m.Set(1, 2, m.At<double>(1, 2) - tile.Y);
                        var w = new Mat();
                        parts.Add(w);
                        Cv2.WarpAffine(n.Frame, w, m, tile.Size, InterpolationFlags.Linear, BorderTypes.Replicate);
                    }
                    using var alignedTile = TemporalMedian.Compute(parts);
                    for (int y = 0; y < tile.Height; y++)
                    {
                        int fy = tile.Y + y;
                        byte* skyRow = (byte*)sky.Ptr(Math.Min(detH - 1, (int)(fy / sy)));
                        float* dst = (float*)median.Ptr(fy);
                        float* src = (float*)alignedTile.Ptr(y);
                        for (int x = 0; x < tile.Width; x++)
                        {
                            int fx = tile.X + x;
                            if (skyRow[Math.Min(detW - 1, (int)(fx / sx))] == 0) continue;
                            dst[3 * fx] = src[3 * x]; dst[3 * fx + 1] = src[3 * x + 1]; dst[3 * fx + 2] = src[3 * x + 2];
                        }
                    }
                }
                finally
                {
                    foreach (var p in parts) p.Dispose();
                }
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
    /// line has the same contrast in both and is rejected. With an aligned median too, the median contrast is the larger
    /// of the two medians' summed contrasts: light that either reference already holds (the landscape in the static
    /// median, the stars in the aligned one) is not new. (The larger of the sums, not of each sample: a per-sample max
    /// of two noisy contrasts is biased upwards and rejects faint real trails.)
    /// </summary>
    static bool IsTransient(Mat frameGray, Mat medianGray, Mat? alignedGray, LineSegment g)
    {
        double dx = g.X2 - g.X1, dy = g.Y2 - g.Y1, len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 1) return true;
        double nx = -dy / len * SideOffset, ny = dx / len * SideOffset;
        int rows = frameGray.Rows, cols = frameGray.Cols;
        float At(Mat m, double x, double y) => m.At<float>(Math.Clamp((int)Math.Round(y), 0, rows - 1), Math.Clamp((int)Math.Round(x), 0, cols - 1));
        double Contrast(Mat m, double x, double y) => At(m, x, y) - (At(m, x + nx, y + ny) + At(m, x - nx, y - ny)) / 2;
        double frameContrast = 0, staticContrast = 0, alignedContrast = double.NegativeInfinity;
        if (alignedGray is not null) alignedContrast = 0;
        int n = (int)len;
        for (int i = 0; i <= n; i++)
        {
            double x = g.X1 + dx * i / len, y = g.Y1 + dy * i / len;
            frameContrast += Contrast(frameGray, x, y);
            staticContrast += Contrast(medianGray, x, y);
            if (alignedGray is not null) alignedContrast += Contrast(alignedGray, x, y);
        }
        double medianContrast = Math.Max(staticContrast, alignedContrast);
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
