using OpenCvSharp;
using Orbitale.Engine.Trails;

namespace Orbitale.Engine.Tests.Trails;

public class TemporalMedianTests
{
    static Mat Solid(float v, int rows = 3, int cols = 3) => new(rows, cols, MatType.CV_32FC3, Scalar.All(v));

    static void DisposeAll(IEnumerable<Mat> mats) { foreach (var m in mats) m.Dispose(); }

    [Fact] public void MedianOfFourIsMeanOfMiddleTwo()
    {
        var mats = new[] { 0.1f, 0.9f, 0.3f, 0.5f }.Select(v => Solid(v)).ToList();
        using var med = TemporalMedian.Compute(mats);
        Assert.Equal(0.4f, med.At<Vec3f>(1, 1).Item0, 5);
        DisposeAll(mats);
    }

    [Fact] public void MedianOfThreeRejectsOutlier()
    {
        var mats = new[] { 0.2f, 0.2f, 1.0f }.Select(v => Solid(v)).ToList();
        using var med = TemporalMedian.Compute(mats);
        var px = med.At<Vec3f>(2, 0);
        Assert.Equal(0.2f, px.Item0, 5);
        Assert.Equal(0.2f, px.Item1, 5);
        Assert.Equal(0.2f, px.Item2, 5);
        DisposeAll(mats);
    }

    [Theory]
    [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)] [InlineData(6)]
    public void MatchesReferenceMedianForEveryCountAndChannel(int n)
    {
        var rng = new Random(n);
        var mats = Enumerable.Range(0, n).Select(_ =>
        {
            var m = new Mat(5, 7, MatType.CV_32FC3);
            for (int r = 0; r < 5; r++)
                for (int c = 0; c < 7; c++)
                    m.Set(r, c, new Vec3f((float)rng.NextDouble(), (float)rng.NextDouble(), (float)rng.NextDouble()));
            return m;
        }).ToList();
        using var med = TemporalMedian.Compute(mats);
        Assert.Equal(5, med.Rows); Assert.Equal(7, med.Cols); Assert.Equal(MatType.CV_32FC3, med.Type());
        for (int r = 0; r < 5; r++)
            for (int c = 0; c < 7; c++)
            {
                var got = med.At<Vec3f>(r, c);
                for (int ch = 0; ch < 3; ch++)
                {
                    var s = mats.Select(m => m.At<Vec3f>(r, c)[ch]).Order().ToArray();
                    float expected = n % 2 == 1 ? s[n / 2] : (s[n / 2 - 1] + s[n / 2]) / 2f;
                    Assert.Equal(expected, got[ch], 5);
                }
            }
        DisposeAll(mats);
    }

    [Fact] public void ChannelsAreMedianedIndependently()
    {
        // channel 0 median 0.2, channel 1 median 0.5, channel 2 median 0.8; no frame holds all three
        var vals = new[] { new Vec3f(0.2f, 0.9f, 0.1f), new Vec3f(0.1f, 0.5f, 0.8f), new Vec3f(0.9f, 0.1f, 0.9f) };
        var mats = vals.Select(v => new Mat(2, 2, MatType.CV_32FC3, new Scalar(v.Item0, v.Item1, v.Item2))).ToList();
        using var med = TemporalMedian.Compute(mats);
        var px = med.At<Vec3f>(1, 1);
        Assert.Equal(0.2f, px.Item0, 5);
        Assert.Equal(0.5f, px.Item1, 5);
        Assert.Equal(0.8f, px.Item2, 5);
        DisposeAll(mats);
    }

    [Fact] public void WorksOnNonContinuousRoiInputs()
    {
        var parents = new[] { 0.1f, 0.9f, 0.3f }.Select(v => Solid(v, 8, 10)).ToList();
        // distinguish ROI content from surroundings: outside is 5.0
        foreach (var p in parents) { p.SetTo(Scalar.All(5.0)); }
        var rois = new List<Mat>();
        var vals = new[] { 0.1f, 0.9f, 0.3f };
        for (int i = 0; i < 3; i++)
        {
            var roi = new Mat(parents[i], new Rect(2, 1, 4, 3));
            roi.SetTo(Scalar.All(vals[i]));
            Assert.False(roi.IsContinuous());
            rois.Add(roi);
        }
        using var med = TemporalMedian.Compute(rois);
        Assert.Equal(3, med.Rows); Assert.Equal(4, med.Cols);
        for (int r = 0; r < 3; r++)
            for (int c = 0; c < 4; c++)
                Assert.Equal(0.3f, med.At<Vec3f>(r, c).Item2, 5);
        DisposeAll(rois); DisposeAll(parents);
    }

    [Fact] public void RejectsTooFewOrTooManyFrames()
    {
        var one = new List<Mat> { Solid(0.5f) };
        Assert.Throws<ArgumentException>(() => TemporalMedian.Compute(one));
        var seven = Enumerable.Range(0, 7).Select(_ => Solid(0.5f)).ToList();
        Assert.Throws<ArgumentException>(() => TemporalMedian.Compute(seven));
        DisposeAll(one); DisposeAll(seven);
    }

    [Fact] public void GrayFramesGiveAGrayMedian()
    {
        var frames = new[] { 0.1f, 0.9f, 0.3f, 0.5f }.Select(v => new Mat(3, 5, MatType.CV_32FC1, Scalar.All(v))).ToList();
        using var m = TemporalMedian.Compute(frames);
        DisposeAll(frames);
        Assert.Equal(MatType.CV_32FC1, m.Type());
        Assert.Equal(0.4f, m.At<float>(2, 4), 6);   // mean of the middle two, at the last pixel of the last row
    }

    [Fact] public void RejectsMismatchedSizeOrType()
    {
        using var a = Solid(0.5f); using var b = Solid(0.5f, 3, 4);
        using var c = new Mat(3, 3, MatType.CV_8UC3, Scalar.All(1));
        using var d = new Mat(3, 3, MatType.CV_32FC1, Scalar.All(1));
        Assert.Throws<ArgumentException>(() => TemporalMedian.Compute(new[] { a, b }));
        Assert.Throws<ArgumentException>(() => TemporalMedian.Compute(new[] { a, c }));
        Assert.Throws<ArgumentException>(() => TemporalMedian.Compute(new[] { a, d }));
    }
}
