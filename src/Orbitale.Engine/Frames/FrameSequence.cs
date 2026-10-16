using Orbitale.Engine.Imaging;

namespace Orbitale.Engine.Frames;

public sealed record SkippedFrame(string Path, string Reason);

public sealed class FrameSequence
{
    FrameSequence(List<string> files, List<SkippedFrame> skipped, int width, int height)
    {
        Files = files; Skipped = skipped; Width = width; Height = height;
    }

    public IReadOnlyList<string> Files { get; }
    public IReadOnlyList<SkippedFrame> Skipped { get; }
    public int Width { get; }
    public int Height { get; }

    public static FrameSequence Build(IEnumerable<string> paths)
    {
        var sorted = paths.ToList();
        sorted.Sort(NaturalCompare);

        var files = new List<string>();
        var skipped = new List<SkippedFrame>();
        ImageHeader? reference = null;
        foreach (var path in sorted)
        {
            ImageHeader header;
            try { header = ImageReader.ReadHeader(path); }
            catch (Exception e)
            {
                skipped.Add(new SkippedFrame(path, $"unreadable: {e.Message}"));
                continue;
            }
            if (reference is null) reference = header;
            else if (header != reference)
            {
                skipped.Add(new SkippedFrame(path,
                    $"size {header.Width}×{header.Height} differs from {reference.Width}×{reference.Height}"));
                continue;
            }
            files.Add(path);
        }

        if (reference is null) throw new InvalidOperationException("No readable frames");
        return new FrameSequence(files, skipped, reference.Width, reference.Height);
    }

    /// <summary>Digit runs compare numerically, everything else ordinal case-insensitive.</summary>
    internal static int NaturalCompare(string? a, string? b)
    {
        if (ReferenceEquals(a, b)) return 0;
        if (a is null) return -1;
        if (b is null) return 1;
        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            if (char.IsAsciiDigit(a[i]) && char.IsAsciiDigit(b[j]))
            {
                int si = i, sj = j;
                while (i < a.Length && char.IsAsciiDigit(a[i])) i++;
                while (j < b.Length && char.IsAsciiDigit(b[j])) j++;
                var na = a.AsSpan(si, i - si).TrimStart('0');
                var nb = b.AsSpan(sj, j - sj).TrimStart('0');
                int c = na.Length != nb.Length ? na.Length.CompareTo(nb.Length) : na.CompareTo(nb, StringComparison.Ordinal);
                if (c != 0) return c;
            }
            else
            {
                int si = i, sj = j;
                while (i < a.Length && !char.IsAsciiDigit(a[i])) i++;
                while (j < b.Length && !char.IsAsciiDigit(b[j])) j++;
                int c = a.AsSpan(si, i - si).CompareTo(b.AsSpan(sj, j - sj), StringComparison.OrdinalIgnoreCase);
                if (c != 0) return c;
            }
        }
        int rest = (a.Length - i).CompareTo(b.Length - j);
        // Deterministic tie-break ("a" vs "A", "IMG_01" vs "IMG_1"): List.Sort is unstable.
        return rest != 0 ? rest : string.CompareOrdinal(a, b);
    }
}
