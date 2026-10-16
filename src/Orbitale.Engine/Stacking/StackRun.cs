using System.Diagnostics;
using OpenCvSharp;
using Orbitale.Engine.Alignment;
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
    internal const string AlignmentFailedNotice =
        "Trail removal skipped on {0}: sky alignment failed (clouds or too few stars). Such frames are counted in the final status";

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

    // Sky alignment: per file index the detection-scale copy of the frame (owned) and its brightest stars (raw frame, no
    // exclude mask), and the sky motion per index pair (lo, hi) as the warp lo -> hi (null = no reliable estimate).
    // All keep only what the current and next frame need.
    const int AlignStarsMax = 4000;  // brightest stars used for alignment: plenty for RANSAC, cheaper and less ambiguous matching
    readonly Dictionary<int, (Mat Detection, IReadOnlyList<Star> Stars)> _alignStars = new();
    readonly Dictionary<int, Task<(Mat Detection, IReadOnlyList<Star> Stars)>> _pendingAlign = new();   // started early, see StartAlignStars
    readonly Dictionary<(int Lo, int Hi), SkyWarp?> _skyMotion = new();
    readonly double _detScale;
    readonly bool _downscaled;       // detection works on a downscaled copy (long side over DetectionScale.LongSide)

    IReadOnlyList<Star>? _prevStars;
    int _prevIndex = -1;             // file index of _prevStars
    int _processed, _trailsTotal, _trailRemovalSkipped;
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
        var det = DetectionScale.For(new Size(seq.Width, seq.Height));
        _detScale = (double)Math.Max(seq.Width, seq.Height) / Math.Max(det.Width, det.Height);
        _downscaled = det != new Size(seq.Width, seq.Height);
    }

    public StackResult Run()
    {
        var metadata = ReadMetadata();
        var clock = Stopwatch.StartNew();
        var window = new FrameWindow(_seq.Files, 2 * _k + 2);
        var acc = AccumulatorFactory.Create(_s.Mode, _s.Mode == BlendMode.Lighten && _s.Comet, _s.CometStrength, _n);
        bool completed = false;
        try
        {
            for (int p = 0; ; p++)
            {
                // All frames first: a Stop during the last frame's report still completes the run.
                if (p == _n) { completed = true; break; }
                if (_ct.IsCancellationRequested) break;
                if (!ProcessFrame(p, window, acc, clock)) break;
            }
        }
        catch
        {
            acc.Dispose();   // a faulted run returns no result
            throw;
        }
        finally
        {
            foreach (var task in _pendingAlign.Values)
            {
                try { task.Result.Detection.Dispose(); } catch (AggregateException) { /* the run is over either way */ }
            }
            _pendingAlign.Clear();
            window.Dispose();   // free the decoded frames before the result is copied
            foreach (var a in _alignStars.Values) a.Detection.Dispose();
            _alignStars.Clear();
        }
        try
        {
            var image = acc.Count > 0 ? acc.Current.Clone() : new Mat(_seq.Height, _seq.Width, MatType.CV_32FC3, Scalar.All(0));
            return new StackResult(image, completed, _processed, metadata, _trailRemovalSkipped);
        }
        finally
        {
            acc.Dispose();
        }
    }

    /// <summary>
    /// The metadata (colour profile, EXIF) to save the result with: the first file's, else the first file whose metadata
    /// reads, else none (saved without a profile). Either fallback is noticed.
    /// </summary>
    ImageMetadata ReadMetadata()
    {
        foreach (var file in _seq.Files)
        {
            try
            {
                var metadata = ImageMetadata.FromFile(file);
                if (!ReferenceEquals(file, _seq.Files[0]))
                    _notices.Add($"The metadata of {Path.GetFileName(_seq.Files[0])} could not be read; the result uses {Path.GetFileName(file)}'s");
                return metadata;
            }
            catch (InvalidDataException) { /* try the next file */ }
        }
        _notices.Add("No frame's metadata could be read; the result is saved without a colour profile or EXIF");
        return new ImageMetadata();
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
            _prevIndex = -1;
            _processed++;
            Report(i, [], _lastPreview ?? Preview.Blank(_seq.Width, _seq.Height, _s.PreviewMaxLongSide), clock);
            return true;
        }
        var neighbours = new List<(int Index, Mat Frame)>();
        int[] neighbourIdx = _k > 0 ? NeighbourWindow.Indices(i, _n, _k) : [];
        foreach (int j in neighbourIdx)
            if (TryGet(window, j) is { } m) neighbours.Add((j, m));
        PrefetchNext(p, i, neighbourIdx, window);
        long t1 = Stopwatch.GetTimestamp();

        // 2. Sky alignment of each neighbour into this frame, then trail removal. Owned: the cleaned frame,
        // or a copy when gap filling will draw on it. When no neighbour aligns on a downscaled frame, trail removal is
        // skipped: the unaligned detection was validated at detection scale only and chops star trails at full resolution.
        Mat? work = null;
        IReadOnlyList<LineSegment> segments = [];
        long t1b = t1;
        if (neighbours.Count >= TemporalMedian.MinFrames)
        {
            var aligned = neighbours.Select(n => new AlignedNeighbour(n.Frame, SkyMotion(n.Index, i, window))
                { Detection = _alignStars.TryGetValue(n.Index, out var a) ? a.Detection : null }).ToList();
            t1b = Stopwatch.GetTimestamp();
            try
            {
                if (_downscaled && aligned.All(a => a.ToCurrent is null)) SkipTrailRemoval(i);
                else
                {
                    var clean = TrailRemover.Clean(frame, aligned, _trailSettings, _alignStars.TryGetValue(i, out var self) ? self.Detection : null);
                    work = clean.Cleaned;
                    segments = clean.Segments;
                }
            }
            finally
            {
                ForgetSkyMotionOutside(p, i, neighbourIdx);
            }
            StartAlignStars(p, window);
        }
        work ??= _gapFiller is not null ? frame.Clone() : frame;
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
                if (_prevStars is not null) _gapFiller.Apply(work, _prevStars, stars, GapFillMotion(_prevIndex, i, _prevStars, stars));
                _prevStars = stars;
                _prevIndex = i;
            }
            long t3 = Stopwatch.GetTimestamp();
            if (_ct.IsCancellationRequested) return false;

            // 4. Blend.
            acc.Add(work);
            long t4 = Stopwatch.GetTimestamp();
            if (owned) { work.Dispose(); owned = false; }

            // 5. Preview.
            _processed++;
            _trailsTotal += segments.Count;
            var preview = Preview.Render(acc.Current, _s.PreviewMaxLongSide);
            _lastPreview = preview;
            long t5 = Stopwatch.GetTimestamp();
            Report(i, segments, preview, clock);
            _timings?.Invoke(new FrameTimings(i, Ms(t0, t1), Ms(t1, t1b), Ms(t1b, t2), Ms(t2, t3), Ms(t3, t4), Ms(t4, t5)));
            return true;
        }
        finally
        {
            if (owned) work.Dispose();
        }
    }

    /// <summary>Counts a frame left unchanged because no neighbour aligned; the first one of the run raises the notice.</summary>
    void SkipTrailRemoval(int i)
    {
        if (_trailRemovalSkipped++ == 0)
            _notices.Add(string.Format(System.Globalization.CultureInfo.InvariantCulture, AlignmentFailedNotice, Path.GetFileName(_seq.Files[i])));
    }

    /// <summary>
    /// The sky motion from the previous processed file <paramref name="prev"/> into file <paramref name="i"/> for gap filling:
    /// the pair's cached alignment warp when trail removal estimated it (inverted under HeadAtStart, as everywhere), else
    /// estimated from the two frames' gap-fill stars (their brightest <see cref="AlignStarsMax"/>). Null when there is none.
    /// </summary>
    SkyWarp? GapFillMotion(int prev, int i, IReadOnlyList<Star> prevStars, IReadOnlyList<Star> stars)
    {
        if (_skyMotion.TryGetValue((Math.Min(prev, i), Math.Max(prev, i)), out var lohi))
            return lohi is null ? null : prev < i ? lohi : lohi.Inverse();
        return SkyAligner.EstimateWarp(Brightest(prevStars), Brightest(stars), 6 + 12 * Math.Abs(i - prev), _detScale);
    }

    static IReadOnlyList<Star> Brightest(IReadOnlyList<Star> stars) => stars.Count <= AlignStarsMax ? stars
        : stars.OrderByDescending(st => Math.Max(st.Color.Item0, Math.Max(st.Color.Item1, st.Color.Item2))).ThenBy(st => st.Y).ThenBy(st => st.X)
               .Take(AlignStarsMax).ToList();

    /// <summary>
    /// The sky motion from file <paramref name="j"/> into file <paramref name="i"/>, or null when it cannot be estimated.
    /// Each index pair is estimated once, lower index to higher, and inverted for the other direction, so the result does
    /// not depend on the processing order.
    /// </summary>
    SkyWarp? SkyMotion(int j, int i, FrameWindow window)
    {
        var lohi = PairMotion(Math.Min(i, j), Math.Max(i, j), window);
        return lohi is null ? null : j < i ? lohi : lohi.Inverse();
    }

    /// <summary>
    /// The warp lo → hi, cached. Estimated directly from the two frames' stars (polynomial when the stars support it).
    /// Over a gap of more than one frame, when the direct estimate fails or falls back to a plain similarity (the matching
    /// radius grows with the gap, and dense star fields then fail the ratio test), the frame-to-frame warps
    /// lo → lo+1 → … → hi are chained instead if they all exist and at least one carries the distortion correction.
    /// </summary>
    SkyWarp? PairMotion(int lo, int hi, FrameWindow window)
    {
        if (_skyMotion.TryGetValue((lo, hi), out var cached)) return cached;
        SkyWarp? result = null;
        if (AlignStars(lo, window) is { } a && AlignStars(hi, window) is { } b)
            result = SkyAligner.EstimateWarp(a, b, 6 + 12 * (hi - lo), _detScale);
        if ((result is null || !result.IsPolynomial) && hi - lo > 1)
        {
            SkyWarp? chain = PairMotion(lo, lo + 1, window);
            for (int t = lo + 1; t < hi && chain is not null; t++)
                chain = PairMotion(t, t + 1, window) is { } step ? chain.Then(step) : null;
            if (chain is not null && (result is null || chain.IsPolynomial)) result = chain;
        }
        _skyMotion[(lo, hi)] = result;
        return result;
    }

    /// <summary>
    /// The brightest stars of file <paramref name="index"/> for sky alignment, detected once on the raw frame (its
    /// detection-scale copy is kept for trail removal); null when the file cannot be decoded.
    /// </summary>
    IReadOnlyList<Star>? AlignStars(int index, FrameWindow window)
    {
        if (_alignStars.TryGetValue(index, out var cached)) return cached.Stars;
        if (_pendingAlign.Remove(index, out var pending)) return (_alignStars[index] = pending.Result).Stars;
        if (TryGet(window, index) is not { } frame) return null;
        return (_alignStars[index] = DetectAlignStars(frame)).Stars;
    }

    /// <summary>
    /// Starts detecting, on a worker thread, the alignment stars of the frames the next processed frame will need that are
    /// already decoded (in practice the one entering the neighbour window, prefetched during this frame). Star detection is
    /// mostly serial, so it overlaps well with the rest of this frame. The window keeps the frame: it is the most recently
    /// used one, and the next eviction (when the next prefetch lands) takes the least recently used.
    /// </summary>
    void StartAlignStars(int p, FrameWindow window)
    {
        if (p + 1 >= _n) return;
        int next = _order[p + 1];
        foreach (int j in NeighbourWindow.Indices(next, _n, _k).Append(next))
        {
            if (_alignStars.ContainsKey(j) || _pendingAlign.ContainsKey(j) || _undecodable.Contains(j) || !window.IsCached(j)) continue;
            var frame = window.Get(j);
            _pendingAlign[j] = Task.Run(() => DetectAlignStars(frame));
        }
    }

    /// <summary>The frame's detection-scale copy (owned by the caller) and its brightest stars.</summary>
    static (Mat Detection, IReadOnlyList<Star> Stars) DetectAlignStars(Mat frame)
    {
        var detection = new Mat();
        var detSize = DetectionScale.For(frame.Size());
        if (frame.Size() == detSize) frame.CopyTo(detection);
        else Cv2.Resize(frame, detection, detSize, 0, 0, InterpolationFlags.Area);
        return (detection, Brightest(StarDetector.Detect(frame, null, detection)));
    }

    /// <summary>Drops the stars and pair motions that neither this frame nor the next processed one needs.</summary>
    void ForgetSkyMotionOutside(int p, int i, int[] neighbourIdx)
    {
        var keep = new HashSet<int>(neighbourIdx) { i };
        if (p + 1 < _n)
        {
            int next = _order[p + 1];
            keep.Add(next);
            keep.UnionWith(NeighbourWindow.Indices(next, _n, _k));
        }
        foreach (int index in _alignStars.Keys.Where(x => !keep.Contains(x)).ToList())
        {
            _alignStars[index].Detection.Dispose();
            _alignStars.Remove(index);
        }
        foreach (var key in _skyMotion.Keys.Where(x => !keep.Contains(x.Lo) || !keep.Contains(x.Hi)).ToList()) _skyMotion.Remove(key);
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
        _progress.Report(new StackProgress(fileIndex, _processed, _n, segments.Count, _trailsTotal, eta, preview, mapped, notice, _trailRemovalSkipped));
    }

    static double Ms(long from, long to) => Stopwatch.GetElapsedTime(from, to).TotalMilliseconds;
}
