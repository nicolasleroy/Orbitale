using System.Diagnostics;
using OpenCvSharp;
using Orbitale.Engine.Blending;
using Orbitale.Engine.Frames;
using Orbitale.Engine.GapFill;
using Orbitale.Engine.Imaging;
using Orbitale.Engine.Trails;

namespace Orbitale.Engine.Stacking;

/// <summary>One run of <see cref="StackEngine"/>: the per-frame pipeline and its state. Single use, single thread.</summary>
internal sealed class StackRun
{
    internal const string TooFewFramesNotice = "Trail removal needs at least 3 frames — turned off";

    readonly FrameSequence _seq;
    readonly StackSettings _s;
    readonly IProgress<StackProgress> _progress;
    readonly CancellationToken _ct;
    readonly Action<FrameTimings>? _timings;

    readonly int _n;
    readonly int[] _order;
    readonly int _k;                 // effective neighbour half-window; 0 = no trail removal
    readonly TrailSettings _trailSettings;
    readonly GapFiller? _gapFiller;
    readonly HashSet<int> _undecodable = new();
    readonly List<string> _notices = new();

    IReadOnlyList<Star>? _prevStars;
    int _processed, _trailsTotal;
    PreviewImage? _lastPreview;

    public StackRun(FrameSequence seq, StackSettings s, IProgress<StackProgress> progress, CancellationToken ct, Action<FrameTimings>? timings)
    {
        _seq = seq; _s = s; _progress = progress; _ct = ct; _timings = timings;
        _n = seq.Files.Count;
        _order = Enumerable.Range(0, _n).ToArray();
        if (s.HeadAtStart) Array.Reverse(_order);

        bool lighten = s.Mode == BlendMode.Lighten;
        _gapFiller = lighten && s.GapFill ? new GapFiller(s.GapFillStrength) : null;
        _trailSettings = new TrailSettings(s.TrailSensitivity);
        if (s.RemoveTrails && _n < 3) _notices.Add(TooFewFramesNotice);
        _k = s.RemoveTrails ? NeighbourWindow.EffectiveK(_n, s.NeighbourK) : 0;
    }

    public StackResult Run()
    {
        var metadata = ImageMetadata.FromFile(_seq.Files[0]);
        var clock = Stopwatch.StartNew();
        var window = new FrameWindow(_seq.Files, 2 * _k + 2);
        var acc = AccumulatorFactory.Create(_s.Mode, _s.Mode == BlendMode.Lighten && _s.Comet, _s.CometStrength, _n);
        bool completed = false;
        try
        {
            for (int p = 0; ; p++)
            {
                if (_ct.IsCancellationRequested) break;
                if (p == _n) { completed = true; break; }
                if (!ProcessFrame(p, window, acc, clock)) break;
            }
        }
        finally
        {
            window.Dispose();   // free the decoded frames before the result is copied
        }
        try
        {
            var image = acc.Count > 0 ? acc.Current.Clone() : new Mat(_seq.Height, _seq.Width, MatType.CV_32FC3, Scalar.All(0));
            return new StackResult(image, completed, _processed, metadata);
        }
        finally
        {
            acc.Dispose();
        }
    }

    /// <summary>Processes the frame at position <paramref name="p"/> of the run. Returns false when cancelled mid-frame.</summary>
    bool ProcessFrame(int p, FrameWindow window, IAccumulator acc, Stopwatch clock)
    {
        int i = _order[p];
        long t0 = Stopwatch.GetTimestamp();

        // 1. Decode the frame and its neighbours (by file index), then start decoding what the next frame adds.
        Mat? frame = TryGet(window, i);
        if (frame is null)
        {
            _prevStars = null;   // never bridge a gap across a frame we could not see
            _processed++;
            Report(i, [], _lastPreview ?? Preview.Blank(_seq.Width, _seq.Height, _s.PreviewMaxLongSide), clock);
            return true;
        }
        var neighbours = new List<Mat>();
        int[] neighbourIdx = _k > 0 ? NeighbourWindow.Indices(i, _n, _k) : [];
        foreach (int j in neighbourIdx)
            if (TryGet(window, j) is { } m) neighbours.Add(m);
        PrefetchNext(p, i, neighbourIdx, window);
        long t1 = Stopwatch.GetTimestamp();

        // 2. Trail removal. Owned: the cleaned frame, or a copy when gap filling will draw on it.
        Mat work;
        IReadOnlyList<LineSegment> segments = [];
        if (neighbours.Count >= TemporalMedian.MinFrames)
        {
            var clean = TrailRemover.Clean(frame, neighbours, _trailSettings);
            work = clean.Cleaned;
            segments = clean.Segments;
        }
        else work = _gapFiller is not null ? frame.Clone() : frame;
        bool owned = !ReferenceEquals(work, frame);
        try
        {
            long t2 = Stopwatch.GetTimestamp();
            if (_ct.IsCancellationRequested) return false;

            // 3. Gap filling against the previous processed frame's stars (removed trails never count as stars).
            if (_gapFiller is not null)
            {
                using var exclude = segments.Count > 0 ? TrailRemover.FullResMask(work.Size(), segments) : null;
                var stars = StarDetector.Detect(work, exclude);
                if (_prevStars is not null) _gapFiller.Apply(work, _prevStars, stars);
                _prevStars = stars;
            }
            long t3 = Stopwatch.GetTimestamp();
            if (_ct.IsCancellationRequested) return false;

            // 4. Blend.
            acc.Add(work);
            long t4 = Stopwatch.GetTimestamp();
            if (owned) { work.Dispose(); owned = false; }

            // 5. Preview (Current is read once: the comet recomputes it on every read).
            _processed++;
            _trailsTotal += segments.Count;
            var preview = Preview.Render(acc.Current, _s.PreviewMaxLongSide);
            _lastPreview = preview;
            long t5 = Stopwatch.GetTimestamp();
            Report(i, segments, preview, clock);
            _timings?.Invoke(new FrameTimings(i, Ms(t0, t1), Ms(t1, t2), Ms(t2, t3), Ms(t3, t4), Ms(t4, t5)));
            return true;
        }
        finally
        {
            if (owned) work.Dispose();
        }
    }

    /// <summary>The window's frame (owned by the window), or null when it cannot be decoded (noticed once per file).</summary>
    Mat? TryGet(FrameWindow window, int index)
    {
        if (_undecodable.Contains(index)) return null;
        try { return window.Get(index); }
        catch (InvalidDataException)
        {
            _undecodable.Add(index);
            _notices.Add($"{Path.GetFileName(_seq.Files[index])} could not be decoded and was skipped");
            return null;
        }
    }

    /// <summary>Starts decoding the frame that the next frame (in processing order) needs and this one did not.</summary>
    void PrefetchNext(int p, int i, int[] neighbourIdx, FrameWindow window)
    {
        if (p + 1 >= _n) return;
        int next = _order[p + 1];
        var needed = _k > 0 ? NeighbourWindow.Indices(next, _n, _k).Append(next) : [next];
        foreach (int j in needed)
        {
            if (j == i || neighbourIdx.Contains(j) || _undecodable.Contains(j)) continue;
            window.Prefetch(j);
            return;
        }
    }

    void Report(int fileIndex, IReadOnlyList<LineSegment> segments, PreviewImage preview, Stopwatch clock)
    {
        // Full-res → preview through pixel centres, as everywhere else.
        double fx = (double)preview.Width / _seq.Width, fy = (double)preview.Height / _seq.Height;
        float X(float x) => (float)((x + 0.5) * fx - 0.5);
        float Y(float y) => (float)((y + 0.5) * fy - 0.5);
        var mapped = segments.Select(g => new LineSegment(X(g.X1), Y(g.Y1), X(g.X2), Y(g.Y2))).ToList();

        var eta = TimeSpan.FromTicks(clock.Elapsed.Ticks / _processed * (_n - _processed));
        string? notice = _notices.Count > 0 ? string.Join("\n", _notices) : null;
        _notices.Clear();
        _progress.Report(new StackProgress(fileIndex, _processed, _n, segments.Count, _trailsTotal, eta, preview, mapped, notice));
    }

    static double Ms(long from, long to) => Stopwatch.GetElapsedTime(from, to).TotalMilliseconds;
}
