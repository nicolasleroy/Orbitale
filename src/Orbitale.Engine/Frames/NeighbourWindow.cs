namespace Orbitale.Engine.Frames;

/// <summary>Which neighbouring frames form the "real sky" reference for a given frame.</summary>
public static class NeighbourWindow
{
    /// <summary>Largest usable half-window: <c>min(k, (frameCount-1)/2)</c>; 0 when fewer than 3 frames.</summary>
    public static int EffectiveK(int frameCount, int k) => Math.Max(0, Math.Min(k, (frameCount - 1) / 2));

    /// <summary>
    /// Indices of up to 2k neighbours of frame <paramref name="i"/> (excluding itself), the window
    /// being shifted, not shrunk, near the sequence ends. Matches remove_trails.py.
    /// </summary>
    public static int[] Indices(int i, int frameCount, int k)
    {
        int kEff = EffectiveK(frameCount, k);
        if (kEff == 0) return Array.Empty<int>();
        int lo = Math.Max(0, Math.Min(i - kEff, frameCount - 1 - 2 * kEff));
        int hi = Math.Min(frameCount, lo + 2 * kEff + 1);
        var idx = new List<int>(2 * kEff);
        for (int j = lo; j < hi; j++) if (j != i) idx.Add(j);
        return idx.ToArray();
    }
}
