using OpenCvSharp;
using Orbitale.Engine.Frames;

namespace Orbitale.Engine.Tests.Frames;

public class FrameSequenceTests
{
    internal static string Sample(int n) => Path.Combine(AppContext.BaseDirectory, "samples", $"{n}.jpg");
    internal static string NewTempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "orbitale-tests", "frames-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    static string TempDirWithCopies(params (string Name, int Sample)[] items)
    {
        var d = NewTempDir();
        foreach (var (name, s) in items) File.Copy(Sample(s), Path.Combine(d, name));
        return d;
    }

    [Fact] public void SortsNaturally()
    {
        var d = TempDirWithCopies(("IMG_10.jpg", 1), ("IMG_9.jpg", 2), ("img_11.JPG", 3));
        Assert.Equal(new[] { "IMG_9.jpg", "IMG_10.jpg", "img_11.JPG" },
            FrameSequence.Build(Directory.GetFiles(d)).Files.Select(Path.GetFileName));
    }

    [Fact] public void SkipsMismatchedAndCorruptFramesNamingThem()
    {
        var d = TempDirWithCopies(("a.jpg", 1), ("b.jpg", 2));
        using (var big = Cv2.ImRead(Sample(3)))
        using (var small = big.Resize(new Size(1000, 667)))
            Cv2.ImWrite(Path.Combine(d, "small.jpg"), small);
        var rnd = new byte[4096]; new Random(1).NextBytes(rnd);
        File.WriteAllBytes(Path.Combine(d, "bad.jpg"), rnd);

        var seq = FrameSequence.Build(Directory.GetFiles(d));

        Assert.Equal(2, seq.Files.Count);
        Assert.Equal((2000, 1333), (seq.Width, seq.Height));
        Assert.Contains(seq.Skipped, s => s.Path.EndsWith("small.jpg") && s.Reason == "size 1000×667 differs from 2000×1333");
        Assert.Contains(seq.Skipped, s => s.Path.EndsWith("bad.jpg") && s.Reason.StartsWith("unreadable"));
    }

    [Fact] public void NoReadableFramesThrows()
    {
        var d = NewTempDir();
        var bad = Path.Combine(d, "bad.jpg");
        File.WriteAllBytes(bad, new byte[] { 1, 2, 3, 4, 5 });
        var ex = Assert.Throws<InvalidOperationException>(() => FrameSequence.Build(new[] { bad }));
        Assert.Equal("No readable frames", ex.Message);
    }
}
