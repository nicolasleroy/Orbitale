using OpenCvSharp;
using Orbitale.Engine.Blending;
using Orbitale.Engine.GapFill;
using Orbitale.Engine.Tests.Support;
using Xunit.Abstractions;

namespace Orbitale.Engine.Tests.GapFill;

public class GapFillerTests(ITestOutputHelper output)
{
    const double Background = 0.05;   // SyntheticSky sky level
    const int Frames = 10;

    // 10 frames at 0.6 deg/frame: stars 380..770 px from the pole jump 4..8 px between frames. The shutter is open for a
    // quarter of each interval, so each Lighten trail is a dotted line.
    static Sky DottedSky(bool landscape = false) =>
        SyntheticSky.Generate(Frames, 1200, 800, 300, 0.6, seed: 11, landscape: landscape, exposure: 0.25);

    // Strength 0.6 -> matching radius 8 detection px, enough for the 8 px jumps (no downscale at 1200 x 800).
    const double Strength = 0.6;

    [Fact] public void FillsGapsBetweenConsecutivePositions()
    {
        using var sky = DottedSky();
        var bright = BrightIsolatedStars(sky, 20);
        using var filled = Stack(sky.Frames, new GapFiller(Strength));
        using var plain = Stack(sky.Frames, null);

        double with = Continuity(sky, filled, bright), without = Continuity(sky, plain, bright);
        output.WriteLine($"continuity with gap fill {with:P1}, without {without:P1}");
        Assert.True(without <= 0.70, $"without gap fill {without:P1}: the scene has no gaps to fill");
        Assert.True(with >= 0.95, $"with gap fill {with:P1}");
    }

    [Fact] public void DoesNotBleedIntoLandscapeForSettingStar()
    {
        using var sky = DottedSky(landscape: true);
        // The scene must contain stars that set: visible in frame f-1, behind the ridge in frame f.
        int setting = sky.Stars.Count(s => s.Amp > 0.2 && Enumerable.Range(1, Frames - 1).Any(f =>
            !IsLand(sky, sky.Centre(s, f - 1)) && IsLand(sky, sky.Centre(s, f))));
        Assert.True(setting >= 3, $"only {setting} bright setting stars in the scene");

        using var stacked = Stack(sky.Frames, new GapFiller(Strength));
        using var lightenOnly = Stack(sky.Frames, null);
        // The landscape band: ground pixels whose 3x3 neighbourhood is all ground. The ground row touching the sky is
        // excluded: a star grazing the ridge in two consecutive frames is matched, and its segment's anti-aliased rim
        // (thickness/2 + 0.5 px) reaches that row, as the star's own image would without the occluder.
        using var band = new Mat();
        using (var k = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(3, 3)))
            Cv2.Erode(sky.Land!, band, k);
        double diff = Cv2.Norm(stacked, lightenOnly, NormTypes.INF, band);
        output.WriteLine($"{setting} setting stars, landscape INF diff {diff:E2} (whole ground incl. ridge row {Cv2.Norm(stacked, lightenOnly, NormTypes.INF, sky.Land!):E2})");
        Assert.True(diff < 1e-4, $"landscape changed by {diff:E2}");
        // ...while gap fill did run in the sky.
        Assert.True(Cv2.Norm(stacked, lightenOnly, NormTypes.INF) > 0.05);
    }

    [Fact] public void StaticLightsDrawNothing()
    {
        // Identical frames: Apply leaves the frame bit-identical.
        using var sky = SyntheticSky.Generate(2, 1200, 800, 300, 0.0, seed: 5);
        var stars = StarDetector.Detect(sky.Frames[0]);
        Assert.True(stars.Count > 100, $"only {stars.Count} stars detected");
        using var frame = sky.Frames[0].Clone();
        new GapFiller(1.0).Apply(frame, stars, stars);
        Assert.Equal(0, Cv2.Norm(frame, sky.Frames[0], NormTypes.INF));

        // Same static scene, independent noise: centroid jitter stays below the 0.5 px motion floor.
        var next = StarDetector.Detect(sky.Frames[1]);
        using var frame1 = sky.Frames[1].Clone();
        new GapFiller(1.0).Apply(frame1, stars, next);
        Assert.Equal(0, Cv2.Norm(frame1, sky.Frames[1], NormTypes.INF));
    }

    [Fact] public void DoesNotThickenTrailsNoticeably()
    {
        using var sky = DottedSky();
        var bright = BrightIsolatedStars(sky, 20);
        using var filled = Stack(sky.Frames, new GapFiller(Strength));

        var ratios = new List<double>();
        foreach (var s in bright)
        {
            // Single star image: cross-section of frame 0 through the star, perpendicular to its motion.
            double single = Fwhm(sky.Frames[0], sky, s, 0);
            // Trail: cross-sections at the middle of each gap, where only the gap-fill segment lights the trail.
            var gaps = Enumerable.Range(0, Frames - 1).Select(f => Fwhm(filled, sky, s, f + 0.5)).ToList();
            ratios.Add(gaps.Average() / single);
        }
        output.WriteLine($"FWHM ratio trail/star: mean {ratios.Average():F2}, max {ratios.Max():F2}");
        Assert.True(ratios.Average() <= 1.3, $"mean trail FWHM {ratios.Average():F2} x the star's");
        Assert.True(ratios.Max() <= 1.5, $"worst trail FWHM {ratios.Max():F2} x the star's");
    }

    [Fact] public void DetectsStarsAtFullResolutionPositions()
    {
        // 3000 x 2000 frame: detection runs at 2000 x 1333 (scale 1.5); centroids map back through pixel centres.
        using var sky = SyntheticSky.Generate(1, 3000, 2000, 400, 0.0, seed: 3, landscape: false, exposure: 0.0);
        var stars = StarDetector.Detect(sky.Frames[0]);
        var bright = sky.Stars.Where(s => s.Amp > 0.3 && InFrame(sky.Centre(s, 0), 3000, 2000, 10)).ToList();
        Assert.True(bright.Count > 20);
        var errors = bright.Select(s =>
        {
            var (x, y) = sky.Centre(s, 0);
            return stars.Min(d => Math.Sqrt((d.X - x) * (d.X - x) + (d.Y - y) * (d.Y - y)));
        }).OrderBy(e => e).ToList();
        double median = errors[errors.Count / 2];
        output.WriteLine($"median centroid error {median:F3} full-res px");
        Assert.True(median < 0.35, $"median centroid error {median:F3} px");
    }

    [Fact] public void DropsStarsUnderTheExcludeMask()
    {
        using var sky = SyntheticSky.Generate(1, 1200, 800, 300, 0.0, seed: 5, landscape: false);
        var all = StarDetector.Detect(sky.Frames[0]);
        using var mask = new Mat(800, 1200, MatType.CV_8UC1, Scalar.All(0));
        Cv2.Rectangle(mask, new Rect(0, 0, 600, 800), Scalar.All(255), -1);   // left half "removed trail"
        var kept = StarDetector.Detect(sky.Frames[0], mask);
        Assert.DoesNotContain(kept, s => s.X < 599);
        Assert.Equal(all.Count(s => s.X >= 600), kept.Count);
    }

    [Fact] public void MatcherRejectsAmbiguousAndInconsistentPairs()
    {
        static Star S(float x, float y) => new(x, y, 1, new Vec3f(0.5f, 0.5f, 0.5f));
        // A 4x4 grid of stars moving by (+5, 0) inside one 200 px cell...
        var prev = new List<Star>(); var cur = new List<Star>();
        for (int i = 0; i < 16; i++) { prev.Add(S(20 + 40 * (i % 4), 20 + 40 * (i / 4))); cur.Add(S(25 + 40 * (i % 4), 20 + 40 * (i / 4))); }
        // ...one star moving (0, +5) against the field (local motion outlier)...
        prev.Add(S(60, 180)); cur.Add(S(60, 185));
        // ...and one with two equally near candidates (ratio test), far from the others.
        prev.Add(S(500, 500)); cur.Add(S(505, 500)); cur.Add(S(500, 505.5f));

        var m = StarMatcher.Match(prev, cur, radiusDetectPx: 7, scale: 1);
        Assert.Equal(16, m.Count);
        Assert.All(m, p => Assert.Equal(5, p.Cur.X - p.Prev.X, 3));
    }

    // --- helpers ---

    static Mat Stack(IReadOnlyList<Mat> frames, GapFiller? filler)
    {
        using var acc = AccumulatorFactory.Create(BlendMode.Lighten, false, 0, frames.Count);
        IReadOnlyList<Star>? prev = null;
        foreach (var f in frames)
        {
            var stars = StarDetector.Detect(f);
            using var work = f.Clone();
            if (filler != null && prev != null) filler.Apply(work, prev, stars);
            acc.Add(work);
            prev = stars;
        }
        return acc.Current.Clone();
    }

    /// <summary>The brightest stars jumping 4..8 px whose whole trail is in the frame and away from every other star's trail.</summary>
    static List<SkyStar> BrightIsolatedStars(Sky sky, int count)
    {
        var size = sky.Frames[0].Size();
        IEnumerable<(double X, double Y)> Trail(SkyStar s) => Enumerable.Range(0, 41).Select(i => sky.Position(s, -0.5 + i * Frames / 40.0, 0));
        var candidates = sky.Stars.Where(s => sky.Jump(s) is >= 4 and <= 8 && Trail(s).All(p => InFrame(p, size.Width, size.Height, 12))).ToList();
        var picked = candidates.OrderByDescending(s => s.Amp)
            .Where(s => !sky.Stars.Any(o => o != s && o.Amp > 0.03 && Trail(s).Any(p => Trail(o).Any(q => Dist(p, q) < 10))))
            .Take(count).ToList();
        Assert.Equal(count, picked.Count);
        return picked;
    }

    /// <summary>Fraction of 200 points along the true arcs (frame 0 centre to last frame centre) above half the star's peak.</summary>
    static double Continuity(Sky sky, Mat stack, IReadOnlyList<SkyStar> stars)
    {
        int lit = 0, total = 0;
        foreach (var s in stars)
        {
            double peak = Enumerable.Range(0, Frames).Max(f => Peak(sky.Frames[f], sky.Centre(s, f))) - Background;
            for (int i = 0; i < 200; i++)
            {
                var p = sky.Centre(s, (Frames - 1) * i / 199.0);
                if (Sample(stack, p.X, p.Y) - Background >= 0.5 * peak) lit++;
                total++;
            }
        }
        return (double)lit / total;
    }

    /// <summary>FWHM of the cross-section through the star's path at frame time <paramref name="t"/>, perpendicular to its motion.</summary>
    static double Fwhm(Mat img, Sky sky, SkyStar s, double t)
    {
        var (x, y) = sky.Centre(s, t);
        var (x2, y2) = sky.Centre(s, t + 0.01);
        double dx = x2 - x, dy = y2 - y, len = Math.Sqrt(dx * dx + dy * dy);
        double nx = -dy / len, ny = dx / len;
        var profile = Enumerable.Range(-80, 161).Select(i => (Off: i * 0.05, V: Sample(img, x + nx * i * 0.05, y + ny * i * 0.05) - Background)).ToList();
        int top = profile.FindIndex(p => p.V == profile.Max(q => q.V));
        double half = profile[top].V / 2;
        int l = top; while (l > 0 && profile[l].V > half) l--;
        int r = top; while (r < profile.Count - 1 && profile[r].V > half) r++;
        double Cross(int a, int b) => profile[a].Off + (half - profile[a].V) / (profile[b].V - profile[a].V) * (profile[b].Off - profile[a].Off);
        return Cross(r - 1, r) - Cross(l, l + 1);
    }

    static double Peak(Mat img, (double X, double Y) p)
    {
        double best = double.MinValue;
        for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
                best = Math.Max(best, Gray(img, (int)Math.Round(p.X) + dx, (int)Math.Round(p.Y) + dy));
        return best;
    }

    /// <summary>Bilinear sample of the channel mean.</summary>
    static double Sample(Mat img, double x, double y)
    {
        int x0 = (int)Math.Floor(x), y0 = (int)Math.Floor(y);
        double fx = x - x0, fy = y - y0;
        return Gray(img, x0, y0) * (1 - fx) * (1 - fy) + Gray(img, x0 + 1, y0) * fx * (1 - fy)
             + Gray(img, x0, y0 + 1) * (1 - fx) * fy + Gray(img, x0 + 1, y0 + 1) * fx * fy;
    }

    static double Gray(Mat img, int x, int y)
    {
        var v = img.At<Vec3f>(Math.Clamp(y, 0, img.Rows - 1), Math.Clamp(x, 0, img.Cols - 1));
        return (v.Item0 + v.Item1 + v.Item2) / 3.0;
    }

    static bool IsLand(Sky sky, (double X, double Y) p)
    {
        int x = (int)Math.Round(p.X), y = (int)Math.Round(p.Y);
        var land = sky.Land!;
        return x >= 0 && y >= 0 && x < land.Cols && y < land.Rows && land.At<byte>(y, x) != 0;
    }

    static bool InFrame((double X, double Y) p, int w, int h, int margin) =>
        p.X >= margin && p.Y >= margin && p.X < w - margin && p.Y < h - margin;

    static double Dist((double X, double Y) a, (double X, double Y) b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
}
