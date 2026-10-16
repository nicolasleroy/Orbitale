namespace Orbitale.Engine.GapFill;

/// <summary>Pairs each star of the previous frame with its image in the current frame.</summary>
public static class StarMatcher
{
    const double RatioTest = 0.67;         // nearest must be < 0.67 × second nearest
    const double MotionCell = 200;         // local-motion cell side, detection px
    const double MotionTolerance = 2;      // allowed deviation from the cell's median displacement, detection px
    const int MotionMinMatches = 3;        // smaller cells keep their matches
    const double MinDisplacement = 0.5;    // full-res px; below this the light is static

    /// <summary>
    /// Matches stars (full-res coordinates; <paramref name="scale"/> = full-res px per detection px):
    /// (a) mutual nearest neighbours within <paramref name="radiusDetectPx"/>; (b) ratio test in both directions;
    /// (c) within each 200×200 detection-px cell (by previous position), drop matches whose displacement differs from
    /// the cell's median by more than 2 detection px (cells with fewer than 3 matches keep theirs);
    /// (d) drop displacements under 0.5 full-res px.
    /// </summary>
    public static IReadOnlyList<(Star Prev, Star Cur)> Match(IReadOnlyList<Star> prev, IReadOnlyList<Star> cur, double radiusDetectPx, double scale)
    {
        ArgumentNullException.ThrowIfNull(prev);
        ArgumentNullException.ThrowIfNull(cur);
        if (!(radiusDetectPx > 0)) throw new ArgumentOutOfRangeException(nameof(radiusDetectPx));
        if (!(scale > 0)) throw new ArgumentOutOfRangeException(nameof(scale));
        if (prev.Count == 0 || cur.Count == 0) return [];

        double radius = radiusDetectPx * scale;   // matching runs in full-res px; same geometry as detection px × scale
        var curGrid = new Grid(cur, radius);
        var prevGrid = new Grid(prev, radius);

        var pairs = new List<(int P, int C)>();
        for (int p = 0; p < prev.Count; p++)
        {
            var (c, okForward) = curGrid.Nearest(prev[p].X, prev[p].Y);
            if (!okForward) continue;
            var (back, okBackward) = prevGrid.Nearest(cur[c].X, cur[c].Y);
            if (okBackward && back == p) pairs.Add((p, c));
        }

        var kept = ConsistentMotion(pairs, prev, cur, MotionCell * scale, MotionTolerance * scale);
        var result = new List<(Star, Star)>(kept.Count);
        foreach (var (p, c) in kept)
        {
            double dx = cur[c].X - prev[p].X, dy = cur[c].Y - prev[p].Y;
            if (dx * dx + dy * dy >= MinDisplacement * MinDisplacement) result.Add((prev[p], cur[c]));
        }
        return result;
    }

    static List<(int P, int C)> ConsistentMotion(List<(int P, int C)> pairs, IReadOnlyList<Star> prev, IReadOnlyList<Star> cur, double cell, double tolerance)
    {
        var kept = new List<(int P, int C)>(pairs.Count);
        foreach (var group in pairs.GroupBy(m => ((long)Math.Floor(prev[m.P].X / cell), (long)Math.Floor(prev[m.P].Y / cell))))
        {
            var members = group.ToList();
            if (members.Count < MotionMinMatches) { kept.AddRange(members); continue; }
            double mdx = Median(members.Select(m => (double)(cur[m.C].X - prev[m.P].X)));
            double mdy = Median(members.Select(m => (double)(cur[m.C].Y - prev[m.P].Y)));
            foreach (var m in members)
            {
                double ex = cur[m.C].X - prev[m.P].X - mdx, ey = cur[m.C].Y - prev[m.P].Y - mdy;
                if (ex * ex + ey * ey <= tolerance * tolerance) kept.Add(m);
            }
        }
        return kept;
    }

    static double Median(IEnumerable<double> values)
    {
        var a = values.OrderBy(v => v).ToArray();
        int k = a.Length / 2;
        return a.Length % 2 == 1 ? a[k] : (a[k - 1] + a[k]) / 2;
    }

    /// <summary>Bucket grid with cell size = search radius: a query visits the 3×3 cells around its point.</summary>
    sealed class Grid
    {
        readonly IReadOnlyList<Star> _stars;
        readonly double _radius;
        readonly Dictionary<(long, long), List<int>> _cells = new();

        public Grid(IReadOnlyList<Star> stars, double radius)
        {
            _stars = stars;
            _radius = radius;
            for (int i = 0; i < stars.Count; i++)
            {
                var key = Key(stars[i].X, stars[i].Y);
                if (!_cells.TryGetValue(key, out var list)) _cells[key] = list = new List<int>();
                list.Add(i);
            }
        }

        (long, long) Key(double x, double y) => ((long)Math.Floor(x / _radius), (long)Math.Floor(y / _radius));

        /// <summary>Nearest star within the radius; ok is false when there is none or it fails the ratio test.</summary>
        public (int Index, bool Ok) Nearest(double x, double y)
        {
            var (kx, ky) = Key(x, y);
            int best = -1;
            double d1 = double.PositiveInfinity, d2 = double.PositiveInfinity, r2 = _radius * _radius;
            for (long cy = ky - 1; cy <= ky + 1; cy++)
                for (long cx = kx - 1; cx <= kx + 1; cx++)
                {
                    if (!_cells.TryGetValue((cx, cy), out var list)) continue;
                    foreach (int i in list)
                    {
                        double dx = _stars[i].X - x, dy = _stars[i].Y - y, d = dx * dx + dy * dy;
                        if (d > r2) continue;
                        if (d < d1) { d2 = d1; d1 = d; best = i; }
                        else if (d < d2) d2 = d;
                    }
                }
            // Squared distances: d1 < 0.67·d2  <=>  d1² < 0.67²·d2².
            bool ok = best >= 0 && (double.IsPositiveInfinity(d2) || d1 < RatioTest * RatioTest * d2);
            return (best, ok);
        }
    }
}
