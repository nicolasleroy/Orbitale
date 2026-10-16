using OpenCvSharp;
using Orbitale.Engine.Imaging;
using Orbitale.Engine.Tests.Support;
using Orbitale.Engine.Trails;

namespace Orbitale.Engine.Tests.Trails;

public class TrailRemoverTests
{
    static Mat S(int n) => ImageReader.Read(Path.Combine(AppContext.BaseDirectory, "samples", $"{n}.jpg"));

    static void DisposeAll(IEnumerable<Mat> mats) { foreach (var m in mats) m.Dispose(); }

    [Theory][InlineData(1, new[] { 2, 3 })][InlineData(2, new[] { 1, 3 })][InlineData(3, new[] { 1, 2 })]
    public void MaskMatchesPrototypeReference(int i, int[] nb)   // samples validated with --k 1; drift guard (float vs uint8 prototype)
    {
        using var f = S(i); var ns = nb.Select(S).ToList();
        using var r = TrailRemover.Clean(f, ns, new TrailSettings());
        using var refMask = Cv2.ImRead(Path.Combine(AppContext.BaseDirectory, "Reference", $"{i}_mask.png"), ImreadModes.Grayscale);
        using var mine = TestMasks.FromSegments(r.Segments, f.Size(), width: 7, dilate: 5);
        double iou = TestMasks.IoU(mine, refMask);
        DisposeAll(ns);
        Assert.True(iou >= 0.55, $"IoU too low for frame {i}: {iou:F3}");
    }

    [Fact] public void RemovesRedAircraftTrailInSample2()
    {
        using var f = S(2); var ns = new[] { 1, 3 }.Select(S).ToList();
        using var r = TrailRemover.Clean(f, ns, new TrailSettings());
        DisposeAll(ns);
        // red trail runs approx (30,195) -> (535,590) in sample 2
        Assert.Contains(r.Segments, s => TestMasks.AngleDeg(s) is var a && Math.Abs(a - 38.0) < 4 && TestMasks.DistanceToLine(s, (30, 195), (535, 590)) < 15);
        double redExcessBefore = TestMasks.MeanRedExcessAlong(f, (60, 218), (500, 563));
        double redExcessAfter = TestMasks.MeanRedExcessAlong(r.Cleaned, (60, 218), (500, 563));
        Assert.True(redExcessAfter < redExcessBefore * 0.25, $"red excess {redExcessBefore:F4} -> {redExcessAfter:F4}");
    }

    [Fact] public void KeepsStarsAwayFromTrails()
    {
        // star count (top-hat peaks > 0.08) in region x 1400..2000, y 0..400 of sample 1, before vs after
        using var f = S(1); var ns = new[] { 2, 3 }.Select(S).ToList();
        using var r = TrailRemover.Clean(f, ns, new TrailSettings());
        DisposeAll(ns);
        var region = new Rect(1400, 0, 600, 400);
        int before = TestMasks.CountStars(f, region), after = TestMasks.CountStars(r.Cleaned, region);
        Assert.True(before > 20, $"only {before} stars in the region");
        Assert.True(after >= before * 0.98, $"stars {before} -> {after}");
    }

    [Theory][InlineData(0.6)][InlineData(1.0)]
    public void StaticStraightBrightLineIsNeverDetected(double sensitivity)   // Review Focus 4
    {
        var frames = SyntheticSky.Make(5, 1200, 800, 600, 0.15, seed: 1);
        SyntheticSky.DrawStaticLine(frames, new(100, 650), new(1100, 640), 0.9);   // in landscape, all frames
        using var r = TrailRemover.Clean(frames[2], new[] { frames[0], frames[1], frames[3], frames[4] }, new TrailSettings(Sensitivity: sensitivity));
        DisposeAll(frames);
        double lineAngle = Math.Atan2(640 - 650, 1100 - 100) * 180 / Math.PI;
        Assert.DoesNotContain(r.Segments, s => TestMasks.DistanceToLine(s, (100, 650), (1100, 640)) < 10
                                             && Math.Abs(TestMasks.AngleDeg(s) - lineAngle) < 5);
    }

    [Theory][InlineData(1, new[] { 2, 3 })][InlineData(2, new[] { 1, 3 })][InlineData(3, new[] { 1, 2 })]
    public void DetectorIsExactPortOfPrototype(int i, int[] nb)
    {
        // Feed the detector exactly what remove_trails.py sees: uint8 frame, uint8 median (numpy's astype
        // truncates the 2-frame mean), gray converted in uint8 then /255. The mask must match the reference.
        using var f = S(i); var ns = nb.Select(S).ToList();
        using var med = TemporalMedian.Compute(ns);
        DisposeAll(ns);
        using var f8 = new Mat(); f.ConvertTo(f8, MatType.CV_8UC3, 255);
        using var m8 = new Mat(); med.ConvertTo(m8, MatType.CV_8UC3, 255, -0.4999);   // floor((a+b)/2)
        using var fg = PrototypeGray(f8); using var mg = PrototypeGray(m8);
        using var d = TrailDetector.Detect(fg, mg, new TrailSettings());
        using var refMask = Cv2.ImRead(Path.Combine(AppContext.BaseDirectory, "Reference", $"{i}_mask.png"), ImreadModes.Grayscale);
        double iou = TestMasks.IoU(d.Mask8U, refMask);
        Assert.True(iou >= 0.99, $"detector IoU {iou:F3} for frame {i}");
    }

    static Mat PrototypeGray(Mat bgr8)
    {
        using var g8 = new Mat();
        Cv2.CvtColor(bgr8, g8, ColorConversionCodes.BGR2GRAY);
        var g = new Mat();
        g8.ConvertTo(g, MatType.CV_32FC1, 1 / 255.0);
        return g;
    }

    [Fact] public void RemovesDashedSyntheticStreakEndToEnd()
    {
        var frames = SyntheticSky.Make(5, 1200, 800, 600, 0.15, seed: 2);
        using var clean = frames[2].Clone();
        SyntheticSky.DrawStreak(frames[2], new(50, 100), new(1000, 400), 0.8, dashed: true);
        using var r = TrailRemover.Clean(frames[2], new[] { frames[0], frames[1], frames[3], frames[4] }, new TrailSettings());
        Assert.NotEmpty(r.Segments);
        Assert.All(r.Segments, s => Assert.True(TestMasks.DistanceToLine(s, (50, 100), (1000, 400)) < 6, $"stray segment {s}"));
        // The 15 px gaps are bridged: the segments' x-ranges cover the whole streak.
        for (int x = 60; x <= 990; x++)
            Assert.Contains(r.Segments, s => Math.Min(s.X1, s.X2) <= x && x <= Math.Max(s.X1, s.X2));
        double before = TestMasks.MeanAbsDiffAlong(frames[2], clean, (50, 100), (1000, 400));
        double after = TestMasks.MeanAbsDiffAlong(r.Cleaned, clean, (50, 100), (1000, 400));
        DisposeAll(frames);
        Assert.True(after < before * 0.05, $"mean diff along streak {before:F3} -> {after:F3}");
    }

    [Fact] public void FullyRemovesDashedStreakWithElongatedDashes()
    {
        // No stars: the only thing along the line is the streak, so any dash kept by star protection shows up.
        // Length 975 = 21 x 45 + 30, so the last dash is a full 30 px (elongated) one.
        var frames = SyntheticSky.Make(5, 1200, 800, 0, 0.15, seed: 6, landscape: false);
        using var clean = frames[2].Clone();
        SyntheticSky.DrawStreak(frames[2], new(200, 100), new(980, 685), 0.8, dashed: true);
        using var r = TrailRemover.Clean(frames[2], new[] { frames[0], frames[1], frames[3], frames[4] }, new TrailSettings());
        DisposeAll(frames);
        Assert.NotEmpty(r.Segments);
        double diff = TestMasks.MaxAbsDiffAlong(r.Cleaned, clean, (200, 100), (980, 685));
        Assert.True(diff < 0.05, $"max diff along streak {diff:F3}");
    }

    [Fact] public void KeepsThisFramesStarInsideTheTrailCorridor()
    {
        // 7728 px long side, as in production (detection scale 1/3.864, corridor about +-19 px at full res).
        // A peak-0.3 star sits 15 px off the streak axis in this frame only (it drifted, so the median lacks it).
        var frames = SyntheticSky.Make(5, 7728, 800, 1500, 0.03, seed: 7);
        var a = new Point(300, 100); var b = new Point(7400, 500);
        double len = Math.Sqrt(Math.Pow(b.X - a.X, 2) + Math.Pow(b.Y - a.Y, 2));
        int sx = (int)Math.Round((a.X + b.X) / 2.0 - 15 * (b.Y - a.Y) / len), sy = (int)Math.Round((a.Y + b.Y) / 2.0 + 15 * (b.X - a.X) / len);
        using var noStar = frames[2].Clone();
        SyntheticSky.DrawStar(frames[2], sx, sy, 0.3, 1.5);
        using var noStreak = frames[2].Clone();
        SyntheticSky.DrawStreak(frames[2], a, b, 0.8, dashed: false);
        using var r = TrailRemover.Clean(frames[2], new[] { frames[0], frames[1], frames[3], frames[4] }, new TrailSettings());
        double peakBefore = frames[2].At<Vec3f>(sy, sx).Item1 - noStar.At<Vec3f>(sy, sx).Item1;
        double peakAfter = r.Cleaned.At<Vec3f>(sy, sx).Item1 - noStar.At<Vec3f>(sy, sx).Item1;
        double streakBefore = TestMasks.MeanAbsDiffAlong(frames[2], noStreak, (a.X, a.Y), (b.X, b.Y));
        double streakAfter = TestMasks.MeanAbsDiffAlong(r.Cleaned, noStreak, (a.X, a.Y), (b.X, b.Y));
        DisposeAll(frames);
        Assert.Contains(r.Segments, s => TestMasks.DistanceToLine(s, (a.X, a.Y), (b.X, b.Y)) < 10);
        Assert.True(peakAfter >= 0.9 * peakBefore, $"star peak {peakBefore:F3} -> {peakAfter:F3}");
        Assert.True(streakAfter < streakBefore * 0.05, $"mean diff along streak {streakBefore:F3} -> {streakAfter:F3}");
    }

    [Fact] public void KeepsBrightWideStarBesideTheTrail()
    {
        // Production scale (1/3.864). A bright, wide star (peak 0.6, PSF sigma 2 px at detection scale) in this frame
        // only. Its above-threshold footprint has a radius of about 6 detection px, so it is centred 33 px (8.5 detection
        // px) off the axis: any closer and it touches the trail blob and merges (accepted loss). Its inner flank lies
        // in the full-weight corridor and must survive along with its peak.
        var frames = SyntheticSky.Make(5, 7728, 800, 1500, 0.03, seed: 8);
        var a = new Point(300, 100); var b = new Point(7400, 500);
        double len = Math.Sqrt(Math.Pow(b.X - a.X, 2) + Math.Pow(b.Y - a.Y, 2));
        double nx = -(b.Y - a.Y) / len, ny = (b.X - a.X) / len, mx = (a.X + b.X) / 2.0, my = (a.Y + b.Y) / 2.0;
        int cx = (int)Math.Round(mx + 33 * nx), cy = (int)Math.Round(my + 33 * ny);   // star centre
        int fx = (int)Math.Round(mx + 15 * nx), fy = (int)Math.Round(my + 15 * ny);   // inner flank, deep in the corridor
        using var noStar = frames[2].Clone();
        SyntheticSky.DrawStar(frames[2], cx, cy, 0.6, 2 * 3.864);
        using var noStreak = frames[2].Clone();
        SyntheticSky.DrawStreak(frames[2], a, b, 0.8, dashed: false);
        using var r = TrailRemover.Clean(frames[2], new[] { frames[0], frames[1], frames[3], frames[4] }, new TrailSettings());
        double Excess(Mat m, int x, int y) => m.At<Vec3f>(y, x).Item1 - noStar.At<Vec3f>(y, x).Item1;
        double peakBefore = Excess(frames[2], cx, cy), peakAfter = Excess(r.Cleaned, cx, cy);
        double flankBefore = Excess(frames[2], fx, fy), flankAfter = Excess(r.Cleaned, fx, fy);
        double streakBefore = TestMasks.MeanAbsDiffAlong(frames[2], noStreak, (a.X, a.Y), (b.X, b.Y));
        double streakAfter = TestMasks.MeanAbsDiffAlong(r.Cleaned, noStreak, (a.X, a.Y), (b.X, b.Y));
        DisposeAll(frames);
        Assert.Contains(r.Segments, s => TestMasks.DistanceToLine(s, (a.X, a.Y), (b.X, b.Y)) < 10);
        Assert.True(peakAfter >= 0.9 * peakBefore, $"star peak {peakBefore:F3} -> {peakAfter:F3}");
        Assert.True(flankAfter >= 0.9 * flankBefore, $"star flank in corridor {flankBefore:F3} -> {flankAfter:F3}");
        Assert.True(streakAfter < streakBefore * 0.05, $"mean diff along streak {streakBefore:F3} -> {streakAfter:F3}");
    }

    [Theory][InlineData(45)][InlineData(30)]
    public void RemovesDiagonalBlinkingTrailAtProductionScale(double angleDeg)
    {
        // 7728 px long side (detection scale 1/3.864). 30 px dashes and 30 px gaps: at 30-45 deg each dash is a
        // small, near-square blob at detection scale and must not be mistaken for a star.
        var frames = SyntheticSky.Make(5, 7728, 1400, 1500, 0.03, seed: 9, landscape: false);
        double t = angleDeg * Math.PI / 180;
        var a = new Point(1000, 100); var b = new Point((int)Math.Round(1000 + 1200 / Math.Tan(t)), 1300);
        using var noStreak = frames[2].Clone();
        SyntheticSky.DrawStreak(frames[2], a, b, 0.8, dashed: true, dash: 30, gap: 30);
        using var r = TrailRemover.Clean(frames[2], new[] { frames[0], frames[1], frames[3], frames[4] }, new TrailSettings());
        double before = TestMasks.MeanAbsDiffAlong(frames[2], noStreak, (a.X, a.Y), (b.X, b.Y));
        double after = TestMasks.MeanAbsDiffAlong(r.Cleaned, noStreak, (a.X, a.Y), (b.X, b.Y));
        DisposeAll(frames);
        Assert.Contains(r.Segments, s => TestMasks.DistanceToLine(s, (a.X, a.Y), (b.X, b.Y)) < 10);
        Assert.True(after < before * 0.05, $"mean diff along dashes {before:F4} -> {after:F4}");
    }

    [Fact] public void NoDetectionsReturnsUnchangedClone()
    {
        var frames = SyntheticSky.Make(5, 1200, 800, 600, 0.0, seed: 3);
        using var r = TrailRemover.Clean(frames[2], new[] { frames[0], frames[1], frames[3], frames[4] }, new TrailSettings());
        Assert.Empty(r.Segments);
        Assert.NotEqual(frames[2].Data, r.Cleaned.Data);
        Assert.Equal(frames[2].Size(), r.Cleaned.Size());
        Assert.Equal(frames[2].Type(), r.Cleaned.Type());
        Assert.Equal(0.0, Cv2.Norm(frames[2], r.Cleaned, NormTypes.INF));
        DisposeAll(frames);
    }

    [Fact] public void SigmaFollowsSensitivity()
    {
        Assert.Equal(2.5, new TrailSettings().Sigma, 10);
        Assert.Equal(4.0, new TrailSettings(Sensitivity: 0.0).Sigma, 10);
        Assert.Equal(1.5, new TrailSettings(Sensitivity: 1.0).Sigma, 10);
    }

    [Fact] public void LargeFrameIsDetectedAtTwoThousandAndMappedBackToFullResolution()
    {
        // 4000x2400 frame: detection runs on a 2000x1200 copy; segments and repair come back at full resolution.
        var frames = SyntheticSky.Make(5, 4000, 2400, 2000, 0.05, seed: 4);
        using var clean = frames[2].Clone();
        SyntheticSky.DrawStreak(frames[2], new(200, 300), new(3000, 1200), 0.8, dashed: false);
        using var r = TrailRemover.Clean(frames[2], new[] { frames[0], frames[1], frames[3], frames[4] }, new TrailSettings());
        Assert.Equal(frames[2].Size(), r.Cleaned.Size());
        Assert.NotEmpty(r.Segments);
        Assert.All(r.Segments, s => Assert.True(TestMasks.DistanceToLine(s, (200, 300), (3000, 1200)) < 8, $"stray segment {s}"));
        Assert.Contains(r.Segments, s => Math.Min(s.X1, s.X2) < 260 && Math.Max(s.X1, s.X2) > 2940);
        double before = TestMasks.MeanAbsDiffAlong(frames[2], clean, (200, 300), (3000, 1200));
        double after = TestMasks.MeanAbsDiffAlong(r.Cleaned, clean, (200, 300), (3000, 1200));
        DisposeAll(frames);
        Assert.True(after < before * 0.05, $"mean diff along streak {before:F3} -> {after:F3}");
    }

    [Fact] public void FullResMaskScalesTheDetectionMaskWidth()
    {
        // 4000 x 2000 -> detection 2000 x 1000, scale 2: the width-7 line + 5 px dilate (half-width ~5.5 detection px)
        // becomes ~11 full-res px either side of the axis.
        var segs = new[] { new LineSegment(500, 1000, 3500, 1000) };
        using var m = TrailRemover.FullResMask(new Size(4000, 2000), segs);
        Assert.Equal(MatType.CV_8UC1, m.Type());
        Assert.Equal(new Size(4000, 2000), m.Size());
        Assert.NotEqual(0, m.At<byte>(1000, 2000));
        Assert.NotEqual(0, m.At<byte>(1010, 2000));
        Assert.NotEqual(0, m.At<byte>(990, 2000));
        Assert.Equal(0, m.At<byte>(1014, 2000));
        Assert.Equal(0, m.At<byte>(986, 2000));
        Assert.Equal(0, m.At<byte>(1000, 400));
        using var empty = TrailRemover.FullResMask(new Size(4000, 2000), []);
        Assert.Equal(0, Cv2.CountNonZero(empty));
    }
}
