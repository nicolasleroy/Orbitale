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

    [Fact] public void NoiseOnlyFramesGiveNull()
    {
        var frames = SyntheticSky.Make(2, 1200, 800, 0, 0.4, seed: 22, landscape: false);
        var a = StarDetector.Detect(frames[0]);
        var b = StarDetector.Detect(frames[1]);
        foreach (var f in frames) f.Dispose();
        using var m = SkyAligner.Estimate(a, b, 18, Scale);
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
        using var m = SkyAligner.Estimate(a, b, 18, Scale);
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
