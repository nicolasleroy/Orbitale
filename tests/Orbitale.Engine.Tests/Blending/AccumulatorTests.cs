using OpenCvSharp;
using Orbitale.Engine.Blending;
using Orbitale.Engine.Tests.Support;
using Xunit.Abstractions;

namespace Orbitale.Engine.Tests.Blending;

public class AccumulatorTests(ITestOutputHelper output)
{
    static Mat Const(float v) => new(4, 5, MatType.CV_32FC3, Scalar.All(v));

    static float Run(BlendMode mode, params Mat[] frames)
    {
        using var acc = AccumulatorFactory.Create(mode, false, 0, 1, frames.Length);
        foreach (var f in frames) acc.Add(f);
        return acc.Current.At<Vec3f>(2, 3).Item1;
    }

    /// <summary>One bright pixel moving 1 px/frame along row 10 over a flat 0.1 background; returns the result and the peak excess.</summary>
    static (Mat result, float peak) RunMovingStar(int n, double fadeIn, double fadeOut)
    {
        const float bg = 0.1f, star = 0.9f;
        using var acc = AccumulatorFactory.Create(BlendMode.Lighten, true, fadeIn, fadeOut, n);
        for (int i = 0; i < n; i++)
        {
            using var f = new Mat(20, n + 40, MatType.CV_32FC3, Scalar.All(bg));
            f.Set(10, 20 + i, new Vec3f(star, star, star));
            acc.Add(f);
        }
        return (acc.Current.Clone(), star - bg);
    }

    static float Excess(Mat m, int x) => m.At<Vec3f>(10, x).Item1 - 0.1f;

    [Fact] public void LightenDarkenAverage()
    { using var a = Const(0.2f); using var b = Const(0.6f);
      Assert.Equal(0.6f, Run(BlendMode.Lighten, a, b), 5);
      Assert.Equal(0.2f, Run(BlendMode.Darken,  a, b), 5);
      Assert.Equal(0.4f, Run(BlendMode.Average, a, b), 5); }

    [Fact] public void AverageIsRefreshedAfterEachAdd()
    { using var acc = new AverageAccumulator();
      using var a = Const(0.2f); using var b = Const(0.6f); using var c = Const(1.0f);
      acc.Add(a); Assert.Equal(0.2f, acc.Current.At<Vec3f>(0, 0).Item0, 5);
      acc.Add(b); Assert.Equal(0.4f, acc.Current.At<Vec3f>(0, 0).Item0, 5);
      acc.Add(c); Assert.Equal(0.6f, acc.Current.At<Vec3f>(0, 0).Item0, 5);
      Assert.Equal(3, acc.Count); }

    [Fact] public void FactoryIgnoresCometForNonLighten()
    { using var d = AccumulatorFactory.Create(BlendMode.Darken, true, 1.0, 1.0, 10);
      using var a = AccumulatorFactory.Create(BlendMode.Average, true, 1.0, 1.0, 10);
      Assert.IsType<DarkenAccumulator>(d);
      Assert.IsType<AverageAccumulator>(a);
      Assert.IsType<CometAccumulator>(AccumulatorFactory.Create(BlendMode.Lighten, true, 0.5, 1, 10));
      Assert.IsType<LightenAccumulator>(AccumulatorFactory.Create(BlendMode.Lighten, false, 0.5, 1, 10)); }

    [Theory]
    [InlineData(BlendMode.Lighten, false)] [InlineData(BlendMode.Lighten, true)]
    [InlineData(BlendMode.Darken, false)] [InlineData(BlendMode.Average, false)]
    public void ValidatesFramesAndState(BlendMode mode, bool comet)
    { using var acc = AccumulatorFactory.Create(mode, comet, 0.5, 1, 5);
      Assert.Throws<InvalidOperationException>(() => acc.Current);
      using var first = Const(0.2f);
      acc.Add(first);
      using var wrongSize = new Mat(6, 5, MatType.CV_32FC3, Scalar.All(0.1));
      using var wrongType = new Mat(4, 5, MatType.CV_8UC3, Scalar.All(1));
      Assert.Throws<ArgumentException>(() => acc.Add(wrongSize));
      Assert.Throws<ArgumentException>(() => acc.Add(wrongType));
      Assert.Equal(1, acc.Count); }

    [Fact] public void CurrentDoesNotAliasInputFrame()
    { using var acc = new LightenAccumulator();
      using var a = Const(0.2f); using var b = Const(0.6f);
      acc.Add(a); acc.Add(b);
      Assert.Equal(0.2f, a.At<Vec3f>(0, 0).Item0, 5); }

    [Fact] public void CometNeverChangesStaticLandscape()   // spec anti-goal; noise-free so the landscape is truly static
    { var frames = SyntheticSky.Make(40, 400, 300, 150, 0.4, seed: 3, noise: 0);
      using var comet = AccumulatorFactory.Create(BlendMode.Lighten, true, 0.3, 0.6, 40);
      using var light = AccumulatorFactory.Create(BlendMode.Lighten, false, 0, 1, 40);
      foreach (var f in frames) { comet.Add(f); light.Add(f); }
      var land = new Rect(0, 246, 400, 54);   // bottom 18 %: safely inside the static band
      Assert.True(Cv2.Norm(comet.Current[land], light.Current[land], NormTypes.INF) < 1e-4);
      foreach (var f in frames) f.Dispose(); }

    [Fact] public void CometLandscapeMatchesLighten()   // spec anti-goal: the comet must not darken landscape or glow
    { // Default noise plus a horizon glow whose brightness drifts by +-20 % over the night. The pixels any star trail
      // touches (from the same sky without noise or glow) are left out of the glow region: the comet fades those on purpose.
      using var sky = SyntheticSky.Generate(40, 400, 300, 150, 0.4, seed: 3);
      SyntheticSky.AddGlow(sky.Frames, sky.Land, 0.1, 0.2);
      using var clean = SyntheticSky.Generate(40, 400, 300, 150, 0.4, seed: 3, noise: 0);
      using var trails = new Mat(300, 400, MatType.CV_8UC1, Scalar.All(0));
      foreach (var f in clean.Frames)
      { using var g = new Mat(); Cv2.CvtColor(f, g, ColorConversionCodes.BGR2GRAY);
        using var lit = new Mat(); Cv2.Compare(g, Scalar.All(0.05 + 0.002), lit, CmpTypes.GT);
        Cv2.BitwiseOr(trails, lit, trails); }
      using (var k = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(5, 5))) Cv2.Dilate(trails, trails, k);
      using var comet = AccumulatorFactory.Create(BlendMode.Lighten, true, 1, 1, 40);
      using var light = AccumulatorFactory.Create(BlendMode.Lighten, false, 0, 1, 40);
      foreach (var f in sky.Frames) { comet.Add(f); light.Add(f); }

      var land = new Rect(0, 246, 400, 54);                    // bottom 18 %: safely inside the static band
      using var glow = new Mat(300, 400, MatType.CV_8UC1, Scalar.All(0));
      glow[new Rect(0, 165, 400, 36)].SetTo(Scalar.All(255));  // 55..67 % of the height: the glow above the ridge
      glow.SetTo(Scalar.All(0), trails);
      Assert.True(Cv2.CountNonZero(glow) > 2000, $"only {Cv2.CountNonZero(glow)} star-free glow px");
      double cl = Cv2.Mean(comet.Current[land]).Val0, ll = Cv2.Mean(light.Current[land]).Val0;
      double cg = Cv2.Mean(comet.Current, glow).Val0, lg = Cv2.Mean(light.Current, glow).Val0;
      output.WriteLine($"landscape comet {cl:F5} lighten {ll:F5} ({cl / ll - 1:P2}); glow comet {cg:F5} lighten {lg:F5} ({cg / lg - 1:P2})");
      Assert.True(Math.Abs(cl / ll - 1) <= 0.01, $"landscape: comet {cl:F5} vs lighten {ll:F5}");
      Assert.True(Math.Abs(cg / lg - 1) <= 0.01, $"glow: comet {cg:F5} vs lighten {lg:F5}");
      // ...while the star trails still fade: over the trail pixels of the upper sky (no glow), the comet keeps well
      // under the Lighten trail light above the sky background.
      using var upper = new Mat(300, 400, MatType.CV_8UC1, Scalar.All(0));
      trails[new Rect(0, 0, 400, 110)].CopyTo(upper[new Rect(0, 0, 400, 110)]);
      double ct = Cv2.Mean(comet.Current, upper).Val0 - 0.05, lt = Cv2.Mean(light.Current, upper).Val0 - 0.05;
      output.WriteLine($"trail light above the sky: comet {ct:F5} lighten {lt:F5} ({ct / lt:P0})");
      Assert.True(ct < 0.7 * lt, $"trails do not fade: comet {ct:F5} vs lighten {lt:F5}"); }

    [Fact] public void CometFadeInRampsUpToTheHead()
    { // fade-in over the whole sequence (head at the end): position k keeps k/(n-1) of the star light
      const int n = 41;
      var (r, peak) = RunMovingStar(n, 1, 1);
      using var _ = r;
      Assert.Equal(peak, Excess(r, 20 + n - 1), 4);                    // newest position = star value
      Assert.Equal(0, Excess(r, 20), 4);                               // first position: invisible
      Assert.Equal(0.5f * peak, Excess(r, 20 + 20), 3);                // half way: half
      for (int x = 20; x < 20 + n - 1; x++)
          Assert.True(Excess(r, x) < Excess(r, x + 1), $"trail not increasing toward head at x={x}"); }

    [Fact] public void CometFadeOutMirrorsTheFadeIn()
    { // both handles at the start: the head is at the start and the trail fades out toward the end
      const int n = 41;
      var (r, peak) = RunMovingStar(n, 0, 0);
      using var _ = r;
      Assert.Equal(peak, Excess(r, 20), 4);
      Assert.Equal(0, Excess(r, 20 + n - 1), 4);
      for (int x = 20; x < 20 + n - 1; x++)
          Assert.True(Excess(r, x) > Excess(r, x + 1), $"trail not decreasing away from head at x={x}"); }

    [Fact] public void CometKeepsFullBrightnessBetweenTheHandles()
    { const int n = 51;                                                // t = k / 50
      var (r, peak) = RunMovingStar(n, 0.2, 0.6);
      using var _ = r;
      Assert.Equal(0.5f * peak, Excess(r, 20 + 5), 3);                 // t = 0.1: half way up the fade-in
      for (int k = 10; k <= 30; k++) Assert.Equal(peak, Excess(r, 20 + k), 4);
      Assert.Equal(0.5f * peak, Excess(r, 20 + 40), 3);                // t = 0.8: half way down the fade-out
      Assert.Equal(0, Excess(r, 20 + 50), 4); }

    [Fact] public void CometWithoutFadeIsLighten()
    { var frames = SyntheticSky.Make(12, 200, 150, 75, 0.4, seed: 5);
      using var comet = AccumulatorFactory.Create(BlendMode.Lighten, true, 0, 1, 12);
      using var light = AccumulatorFactory.Create(BlendMode.Lighten, false, 0, 1, 12);
      foreach (var f in frames) { comet.Add(f); light.Add(f); }
      Assert.True(Cv2.Norm(comet.Current, light.Current, NormTypes.INF) < 1e-6);
      foreach (var f in frames) f.Dispose(); }

    [Fact] public void CometWeightFollowsTheFileIndex()
    { // a skipped (undecodable) frame leaves its index unused: the frames after it keep their own weight
      var frames = SyntheticSky.Make(10, 200, 150, 75, 0.4, seed: 7);
      using var counted = new CometAccumulator(10, 0.3, 0.8);
      using var indexed = new CometAccumulator(10, 0.3, 0.8);
      using var skipping = new CometAccumulator(10, 0.3, 0.8);
      for (int i = 0; i < 10; i++) { counted.Add(frames[i]); indexed.Add(frames[i], i); }
      Assert.Equal(0, Cv2.Norm(counted.Current, indexed.Current, NormTypes.INF));
      for (int i = 0; i < 10; i++) if (i != 4) skipping.Add(frames[i], i);
      using var plainSkip = new CometAccumulator(10, 0.3, 0.8);
      for (int i = 0; i < 10; i++) if (i != 4) plainSkip.Add(frames[i]);
      Assert.True(Cv2.Norm(skipping.Current, plainSkip.Current, NormTypes.INF) > 1e-3, "the index must set the weight");
      Assert.Throws<ArgumentOutOfRangeException>(() => indexed.Add(frames[0], 10));
      Assert.Throws<ArgumentOutOfRangeException>(() => indexed.Add(frames[0], -1));
      foreach (var f in frames) f.Dispose(); }

    [Theory]
    [InlineData(0, 11, 0.5, 1.0, 0.0)]
    [InlineData(5, 11, 0.5, 1.0, 1.0)]
    [InlineData(10, 11, 0.5, 1.0, 1.0)]
    [InlineData(10, 11, 1.0, 1.0, 1.0)]
    [InlineData(0, 11, 0.0, 0.0, 1.0)]
    [InlineData(10, 11, 0.0, 0.0, 0.0)]
    [InlineData(8, 11, 0.2, 0.6, 0.5)]
    [InlineData(0, 1, 0.5, 0.5, 1.0)]     // a single frame sits at the end
    [InlineData(2, 11, 0.8, 0.4, 0.5)]    // swapped bounds are reordered to (0.4, 0.8): t = 0.2 is half way up
    public void CometFadeWeights(int index, int total, double fadeIn, double fadeOut, double expected)
    { Assert.Equal(expected, CometFade.Weight(index, total, fadeIn, fadeOut), 5); }
}
