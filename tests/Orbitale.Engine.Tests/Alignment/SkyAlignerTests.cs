using OpenCvSharp;
using Orbitale.Engine.Alignment;
using Orbitale.Engine.GapFill;
using Orbitale.Engine.Tests.Support;
using Xunit.Abstractions;

namespace Orbitale.Engine.Tests.Alignment;

public class SkyAlignerTests(ITestOutputHelper output)
{
    // 1200 x 800 is below the detection long side: scale 1 (detection px = full-res px).
    const double Scale = 1.0;

    [Theory][InlineData(3, 4)][InlineData(4, 3)][InlineData(1, 4)][InlineData(5, 2)]
    public void RecoversTheSkyRotationBetweenFrames(int from, int to)
    {
        using var sky = SyntheticSky.Generate(7, 1200, 800, 600, 0.4, seed: 21);
        var a = StarDetector.Detect(sky.Frames[from]);
        var b = StarDetector.Detect(sky.Frames[to]);
        using var m = SkyAligner.Estimate(a, b, maxShiftDetectPx: 6 + 12 * Math.Abs(to - from), Scale);
        Assert.NotNull(m);
        Assert.Equal(MatType.CV_64FC1, m.Type());
        Assert.Equal((2, 3), (m.Rows, m.Cols));

        // 20 random sky points (above the landscape band) land within 0.5 px of their true rotated positions.
        var rng = new Random(5);
        double worst = 0;
        for (int p = 0; p < 20; p++)
        {
            double x = rng.NextDouble() * 1200, y = rng.NextDouble() * 560;
            var (tx, ty) = sky.Rotate(x, y, from, to);
            double ex = m.At<double>(0, 0) * x + m.At<double>(0, 1) * y + m.At<double>(0, 2) - tx;
            double ey = m.At<double>(1, 0) * x + m.At<double>(1, 1) * y + m.At<double>(1, 2) - ty;
            worst = Math.Max(worst, Math.Sqrt(ex * ex + ey * ey));
        }
        output.WriteLine($"{from}->{to}: worst error {worst:F3} px");
        Assert.True(worst < 0.5, $"worst error {worst:F3} px");
    }

    // Barrel distortion k1 = -0.05 relative to the half-diagonal and fast rotation (1.1 deg/frame, 19 px per frame at the
    // sky's lower corners): the similarity alone misfits the frame edges by over a pixel; the cubic correction must bring
    // that to 0.3 px.
    // The sky's edges: left, right and top, down to 60 % of the height (below that is landscape in a real night scene,
    // and here the fastest-moving stars, whose matches get too sparse to measure the fit).
    static IEnumerable<(double X, double Y)> EdgePoints(int w, int h)
    {
        for (int y = 40; y <= h * 0.6; y += 40) { yield return (40, y); yield return (w - 40, y); }
        for (int x = 100; x <= w - 100; x += 100) yield return (x, 40);
    }

    static double WorstEdgeError(Sky sky, Func<double, double, (double X, double Y)> map, int from, int to)
        => EdgePoints(sky.W, sky.H).Max(p =>
        {
            var (tx, ty) = sky.Move(p.X, p.Y, from, to);
            var (mx, my) = map(p.X, p.Y);
            return Math.Sqrt((mx - tx) * (mx - tx) + (my - ty) * (my - ty));
        });

    [Fact] public void PolynomialAbsorbsLensDistortionAtTheEdges()
    {
        using var sky = SyntheticSky.Generate(5, 2000, 1333, 2200, 0.6, seed: 28, landscape: false, distortionK1: -0.05);
        var a = StarDetector.Detect(sky.Frames[3]);
        var b = StarDetector.Detect(sky.Frames[2]);
        using var affine = SkyAligner.Estimate(a, b, 22, Scale);
        var warp = SkyAligner.EstimateWarp(a, b, 22, Scale);
        output.WriteLine($"{a.Count}/{b.Count} stars, {StarMatcher.Match(a, b, 22, Scale).Count} matches");
        Assert.NotNull(affine);
        Assert.NotNull(warp);
        double affineErr = WorstEdgeError(sky, (x, y) => (affine.At<double>(0, 0) * x + affine.At<double>(0, 1) * y + affine.At<double>(0, 2),
                                                          affine.At<double>(1, 0) * x + affine.At<double>(1, 1) * y + affine.At<double>(1, 2)), 3, 2);
        double warpErr = WorstEdgeError(sky, warp.Map, 3, 2);
        double backErr = WorstEdgeError(sky, warp.MapBack, 2, 3);
        output.WriteLine($"edge misfit: similarity {affineErr:F3} px, polynomial {warpErr:F3} px, polynomial inverse {backErr:F3} px");
        Assert.True(affineErr > 1, $"the scene must defeat the similarity: {affineErr:F3} px");
        Assert.True(warp.IsPolynomial, "expected a polynomial fit");
        Assert.True(warpErr <= 0.3, $"polynomial edge misfit {warpErr:F3} px");
        Assert.True(backErr <= 0.3, $"polynomial inverse edge misfit {backErr:F3} px");
    }

    [Fact] public void ChainedPolynomialWarpsFollowTheDistortedSky()
    {
        // 1 -> 2 -> 3 composed pointwise, and its inverse, against the true motion 1 -> 3.
        using var sky = SyntheticSky.Generate(5, 2000, 1333, 2200, 0.6, seed: 28, landscape: false, distortionK1: -0.05);
        var s = sky.Frames.Select(f => StarDetector.Detect(f)).ToList();
        var w12 = SkyAligner.EstimateWarp(s[1], s[2], 22, Scale);
        var w23 = SkyAligner.EstimateWarp(s[2], s[3], 22, Scale);
        Assert.NotNull(w12); Assert.NotNull(w23);
        var chain = w12.Then(w23);
        double fwd = WorstEdgeError(sky, chain.Map, 1, 3), back = WorstEdgeError(sky, chain.Inverse().Map, 3, 1);
        output.WriteLine($"chained edge misfit {fwd:F3} px, inverse {back:F3} px");
        Assert.True(fwd <= 0.5, $"chained misfit {fwd:F3} px");   // two steps' errors add up (each <= 0.3)
        Assert.True(back <= 0.5, $"inverse chained misfit {back:F3} px");
    }

    [Fact] public void NoiseOnlyFramesGiveNull()
    {
        var frames = SyntheticSky.Make(2, 1200, 800, 0, 0.4, seed: 22, landscape: false);
        var a = StarDetector.Detect(frames[0]);
        var b = StarDetector.Detect(frames[1]);
        foreach (var f in frames) f.Dispose();
        using var m = SkyAligner.Estimate(a, b, 22, Scale);
        Assert.Null(m);
    }

    [Theory][InlineData(0.0)][InlineData(0.4)][InlineData(0.8)]
    public void StaticLightsGiveIdentityOrNull(double jitter)
    {
        // Pure landscape: the same 400 lights in both lists, each moved by up to `jitter` px of centroid noise.
        var rng = new Random(23);
        var a = new List<Star>(); var b = new List<Star>();
        for (int i = 0; i < 400; i++)
        {
            float x = (float)(rng.NextDouble() * 1200), y = (float)(rng.NextDouble() * 800);
            a.Add(new Star(x, y, 1, default));
            b.Add(new Star(x + (float)((rng.NextDouble() * 2 - 1) * jitter), y + (float)((rng.NextDouble() * 2 - 1) * jitter), 1, default));
        }
        using var m = SkyAligner.Estimate(a, b, 22, Scale);
        if (m is null) return;
        output.WriteLine($"jitter {jitter}: [{m.At<double>(0, 0):F5} {m.At<double>(0, 1):F5} {m.At<double>(0, 2):F3}; {m.At<double>(1, 0):F5} {m.At<double>(1, 1):F5} {m.At<double>(1, 2):F3}]");
        // Identity-like: every point of the frame moves by less than 1.5 px.
        foreach (var (x, y) in new[] { (0.0, 0.0), (1200.0, 0.0), (0.0, 800.0), (1200.0, 800.0) })
        {
            double ex = m.At<double>(0, 0) * x + m.At<double>(0, 1) * y + m.At<double>(0, 2) - x;
            double ey = m.At<double>(1, 0) * x + m.At<double>(1, 1) * y + m.At<double>(1, 2) - y;
            Assert.True(Math.Sqrt(ex * ex + ey * ey) < 1.5, $"corner ({x},{y}) moves {Math.Sqrt(ex * ex + ey * ey):F2} px");
        }
    }

    [Fact] public void TooFewStarsGiveNull()
    {
        using var sky = SyntheticSky.Generate(2, 1200, 800, 12, 0.4, seed: 24);
        using var m = SkyAligner.Estimate(StarDetector.Detect(sky.Frames[0]), StarDetector.Detect(sky.Frames[1]), 18, Scale);
        Assert.Null(m);
    }
}
