using Orbitale.Engine.Imaging;
using Orbitale.Engine.Tests.Support;

namespace Orbitale.Engine.Tests.Stacking;

/// <summary>Synthetic frame files for the stacking tests.</summary>
internal static class StackFiles
{
    public const int Width = 320, Height = 240;

    public static string NewTempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "orbitale-tests", "stack-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    /// <summary>Writes <paramref name="n"/> <see cref="SyntheticSky"/> frames as JPGs f00.jpg … into a new temp dir; returns their paths in order.</summary>
    public static List<string> SyntheticFiles(int n, string? dir = null) => SyntheticFiles(SyntheticSky.Make(n, Width, Height, 40, 0.5, seed: 3), dir);

    /// <summary>Writes (and disposes) <paramref name="frames"/> as JPGs f00.jpg … into a new temp dir; returns their paths in order.</summary>
    public static List<string> SyntheticFiles(IReadOnlyList<OpenCvSharp.Mat> frames, string? dir = null)
    {
        dir ??= NewTempDir();
        var paths = new List<string>();
        for (int i = 0; i < frames.Count; i++)
        {
            var p = Path.Combine(dir, $"f{i:D2}.jpg");
            ImageWriter.Save(frames[i], p, new ImageMetadata());
            frames[i].Dispose();
            paths.Add(p);
        }
        return paths;
    }
}
