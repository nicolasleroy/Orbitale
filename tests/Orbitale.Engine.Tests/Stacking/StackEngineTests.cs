using OpenCvSharp;
using Orbitale.Engine.Blending;
using Orbitale.Engine.Frames;
using Orbitale.Engine.Imaging;
using Orbitale.Engine.Stacking;
using Orbitale.Engine.Tests.Support;
using static Orbitale.Engine.Tests.Stacking.StackFiles;

namespace Orbitale.Engine.Tests.Stacking;

public class StackEngineTests
{
    [Fact] public async Task ReportsEveryFrameAndCompletes()
    {
        var seq = FrameSequence.Build(SyntheticFiles(8)); var reports = new List<StackProgress>();
        using var r = await new StackEngine().RunAsync(seq, new StackSettings(), new SyncProgress<StackProgress>(reports.Add), default);
        Assert.True(r.Completed); Assert.Equal(8, reports.Count); Assert.Equal(8, reports[^1].Processed);
        Assert.Equal(8, r.FramesProcessed);
        Assert.All(reports, p => Assert.Equal(8, p.Total));
        Assert.Equal(Enumerable.Range(1, 8), reports.Select(p => p.Processed));
        Assert.Equal(Enumerable.Range(0, 8), reports.Select(p => p.FileIndex));
        Assert.Equal((Width, Height), (r.Image.Width, r.Image.Height));
        Assert.Equal((Width, Height), (reports[^1].Preview.Width, reports[^1].Preview.Height));
        Assert.Equal(TimeSpan.Zero, reports[^1].Eta);
        Assert.All(reports, p => Assert.Null(p.Notice));
    }

    [Fact] public async Task ResultIsTheLightenOfTheFramesWhenTrailRemovalIsOff()
    {
        var files = SyntheticFiles(4);
        var seq = FrameSequence.Build(files);
        using var r = await new StackEngine().RunAsync(seq, new StackSettings(RemoveTrails: false), new SyncProgress<StackProgress>(_ => { }), default);
        using var expected = ImageReader.Read(files[0]);
        foreach (var f in files.Skip(1)) { using var m = ImageReader.Read(f); Cv2.Max(expected, m, expected); }
        Assert.Equal(0, Cv2.Norm(expected, r.Image, NormTypes.INF));
    }

    [Fact] public async Task CancelKeepsPartialResult()
    {
        var seq = FrameSequence.Build(SyntheticFiles(8)); var reports = new List<StackProgress>();
        using var cts = new CancellationTokenSource();
        using var r = await new StackEngine().RunAsync(seq, new StackSettings(), new SyncProgress<StackProgress>(p =>
        {
            reports.Add(p);
            if (reports.Count == 3) cts.Cancel();
        }), cts.Token);
        Assert.False(r.Completed);
        Assert.Equal(3, r.FramesProcessed);
        Assert.Equal(3, reports.Count);
        Assert.False(r.Image.Empty());
        Assert.True(Cv2.Norm(r.Image, NormTypes.INF) > 0);
        var path = Path.Combine(NewTempDir(), "partial.tif");
        r.Save(path);
        using var back = ImageReader.Read(path);
        Assert.Equal((Width, Height), (back.Width, back.Height));
    }

    [Fact] public async Task CancelledBeforeStartReturnsAnEmptyPartialResult()
    {
        var seq = FrameSequence.Build(SyntheticFiles(3)); var reports = new List<StackProgress>();
        using var r = await new StackEngine().RunAsync(seq, new StackSettings(), new SyncProgress<StackProgress>(reports.Add), new CancellationToken(true));
        Assert.False(r.Completed);
        Assert.Equal(0, r.FramesProcessed);
        Assert.Empty(reports);
        Assert.Equal((Width, Height), (r.Image.Width, r.Image.Height));
    }

    [Fact] public async Task TwoFramesTurnsTrailRemovalOffWithNotice()   // Review Focus 3
    {
        var seq = FrameSequence.Build(SyntheticFiles(2)); var reports = new List<StackProgress>();
        using var r = await new StackEngine().RunAsync(seq, new StackSettings(), new SyncProgress<StackProgress>(reports.Add), default);
        Assert.True(r.Completed);
        Assert.Contains("at least 3 frames", reports[0].Notice);
        Assert.Null(reports[1].Notice);
    }

    [Fact] public async Task HeadAtStartProcessesInReverse()
    {
        var seq = FrameSequence.Build(SyntheticFiles(8)); var reports = new List<StackProgress>();
        using var r = await new StackEngine().RunAsync(seq, new StackSettings(HeadAtStart: true), new SyncProgress<StackProgress>(reports.Add), default);
        Assert.Equal(Enumerable.Range(0, 8).Reverse(), reports.Select(p => p.FileIndex));
        Assert.Equal(Enumerable.Range(1, 8), reports.Select(p => p.Processed));
    }

    [Fact] public async Task ReverseOrderUsesTheSameNeighboursByFileIndex()
    {
        // Lighten is order-independent, and the neighbours of each frame are chosen by file index, so the cleaned frames
        // and hence the stacks are identical whichever end the run starts from.
        var files = SyntheticFiles(6);
        var dir = Path.GetDirectoryName(files[0])!;
        using (var m = ImageReader.Read(files[2]))
        {
            SyntheticSky.DrawStreak(m, new Point(10, 30), new Point(310, 120), 0.8, dashed: false);
            ImageWriter.Save(m, files[2], new ImageMetadata());
        }
        var seq = FrameSequence.Build(files);
        var forwardReports = new List<StackProgress>();
        using var forward = await new StackEngine().RunAsync(seq, new StackSettings(), new SyncProgress<StackProgress>(forwardReports.Add), default);
        using var reverse = await new StackEngine().RunAsync(seq, new StackSettings(HeadAtStart: true), new SyncProgress<StackProgress>(_ => { }), default);
        Assert.True(forwardReports.Single(p => p.FileIndex == 2).TrailsInFrame >= 1, "the streak must be detected");
        Assert.Equal(0, Cv2.Norm(forward.Image, reverse.Image, NormTypes.INF));
    }

    [Fact] public async Task TrailsAreCountedAndMappedToPreviewCoordinates()
    {
        var files = SyntheticFiles(5);
        using (var m = ImageReader.Read(files[2]))
        {
            SyntheticSky.DrawStreak(m, new Point(10, 30), new Point(310, 120), 0.8, dashed: false);
            ImageWriter.Save(m, files[2], new ImageMetadata());
        }
        var seq = FrameSequence.Build(files); var reports = new List<StackProgress>();
        // Preview at half size: segment coordinates are halved through pixel centres.
        using var r = await new StackEngine().RunAsync(seq, new StackSettings(PreviewMaxLongSide: Width / 2), new SyncProgress<StackProgress>(reports.Add), default);
        var p2 = reports[2];
        Assert.True(p2.TrailsInFrame >= 1);
        Assert.Equal(p2.TrailsInFrame, p2.RemovedTrailsPreviewCoords.Count);
        Assert.Equal(reports.Sum(p => p.TrailsInFrame), reports[^1].TrailsTotal);
        Assert.Equal((Width / 2, Height / 2), (p2.Preview.Width, p2.Preview.Height));
        foreach (var s in p2.RemovedTrailsPreviewCoords)
        {
            // The streak runs from (10, 30) to (310, 120) at full res; in the preview it lies on the halved line.
            double dist = Math.Abs(90.0 * (s.X1 - 4.75) - 300.0 * (s.Y1 - 14.75)) / Math.Sqrt(90.0 * 90 + 300.0 * 300);
            Assert.True(dist < 2.5, $"preview segment start {s.X1:F1},{s.Y1:F1} is {dist:F1} px off the streak");
            Assert.InRange(s.X1, 0, Width / 2.0); Assert.InRange(s.X2, 0, Width / 2.0);
        }
        Assert.All(reports.Where(p => p.FileIndex != 2), p => Assert.Empty(p.RemovedTrailsPreviewCoords));
        // The streak is gone from the stack.
        Assert.True(r.Image.At<Vec3f>(75, 160).Item0 < 0.5, "streak pixel still bright in the stack");
    }

    [Fact] public async Task SkippedFramesAreNotProcessed()
    {
        var files = SyntheticFiles(5);
        var odd = Path.Combine(Path.GetDirectoryName(files[0])!, "f02b.jpg");
        using (var small = new Mat(Height / 2, Width / 2, MatType.CV_32FC3, Scalar.All(0.1)))
            ImageWriter.Save(small, odd, new ImageMetadata());
        var seq = FrameSequence.Build(files.Append(odd));
        Assert.Single(seq.Skipped);
        var reports = new List<StackProgress>();
        using var r = await new StackEngine().RunAsync(seq, new StackSettings(), new SyncProgress<StackProgress>(reports.Add), default);
        Assert.True(r.Completed);
        Assert.All(reports, p => Assert.Equal(5, p.Total));
        Assert.Equal(5, reports.Count);
    }

    [Fact] public async Task UndecodableFrameIsSkippedWithANoticeAndCounted()
    {
        var files = SyntheticFiles(6);
        TruncateKeepingHeader(files[3]);
        var seq = FrameSequence.Build(files);
        Assert.Empty(seq.Skipped);                       // the header is valid: it passes validation
        Assert.Equal(6, seq.Files.Count);
        var reports = new List<StackProgress>();
        using var r = await new StackEngine().RunAsync(seq, new StackSettings(), new SyncProgress<StackProgress>(reports.Add), default);
        Assert.True(r.Completed);
        Assert.Equal(6, r.FramesProcessed);
        Assert.Equal(Enumerable.Range(1, 6), reports.Select(p => p.Processed));
        var notices = reports.Where(p => p.Notice != null).ToList();
        Assert.Single(notices);
        Assert.Contains("f03.jpg", notices[0].Notice);
    }

    [Fact] public async Task CometAndGapFillRunWithTrailRemoval()
    {
        var seq = FrameSequence.Build(SyntheticFiles(6)); var reports = new List<StackProgress>();
        var s = new StackSettings(Comet: true, CometStrength: 0.8, GapFill: true, GapFillStrength: 0.5);
        using var r = await new StackEngine().RunAsync(seq, s, new SyncProgress<StackProgress>(reports.Add), default);
        Assert.True(r.Completed);
        Assert.Equal(6, reports.Count);
    }

    [Fact] public async Task NonLightenModesIgnoreCometAndGapFill()
    {
        var seq = FrameSequence.Build(SyntheticFiles(4));
        using var plain = await new StackEngine().RunAsync(seq, new StackSettings(Mode: BlendMode.Darken), new SyncProgress<StackProgress>(_ => { }), default);
        using var flagged = await new StackEngine().RunAsync(seq, new StackSettings(Mode: BlendMode.Darken, Comet: true, GapFill: true, GapFillStrength: 1),
            new SyncProgress<StackProgress>(_ => { }), default);
        Assert.Equal(0, Cv2.Norm(plain.Image, flagged.Image, NormTypes.INF));
    }

    [Fact] public async Task ResultCarriesTheFirstFramesMetadataAndRendersFull()
    {
        var seq = FrameSequence.Build(SyntheticFiles(3));
        using var r = await new StackEngine().RunAsync(seq, new StackSettings(), new SyncProgress<StackProgress>(_ => { }), default);
        var expected = ImageMetadata.FromFile(seq.Files[0]);
        Assert.Equal(expected.IccProfile, r.Metadata.IccProfile);
        var full = r.RenderFull();
        Assert.Equal((Width, Height, Width * Height * 4), (full.Width, full.Height, full.Bgra.Length));
    }

    /// <summary>Cuts a JPG 64 bytes into its scan (after the SOS marker): the header still reads, the pixels no longer decode.</summary>
    static void TruncateKeepingHeader(string path)
    {
        var bytes = File.ReadAllBytes(path);
        int sos = -1;
        for (int i = 2; i + 1 < bytes.Length; i++)
            if (bytes[i] == 0xFF && bytes[i + 1] == 0xDA) { sos = i; break; }
        Assert.True(sos > 0, "no SOS marker");
        File.WriteAllBytes(path, bytes[..(sos + 64)]);
    }
}
