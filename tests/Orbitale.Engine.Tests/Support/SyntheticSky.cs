using OpenCvSharp;

namespace Orbitale.Engine.Tests.Support;

/// <summary>Deterministic synthetic night-sky sequences for trail tests (CV_32FC3, BGR, 0..1).</summary>
public static class SyntheticSky
{
    const double Background = 0.05;
    const double LandscapeLevel = 0.02;
    const int SubSteps = 6;

    /// <summary>
    /// <paramref name="n"/> frames of <paramref name="stars"/> Gaussian-PSF stars rotating by
    /// <paramref name="degPerFrame"/> about (w/2, -h/4), each trailed over its frame's exposure, plus seeded Gaussian
    /// sensor noise of <paramref name="noise"/> (independent per frame and channel; 0 = noise-free).
    /// With <paramref name="landscape"/>, the bottom 25 % is a static dark band with a jagged ridge that hides the stars behind it.
    /// </summary>
    public static List<Mat> Make(int n, int w, int h, int stars, double degPerFrame, int seed, bool landscape = true, double noise = 0.003)
    {
        using var sky = Generate(n, w, h, stars, degPerFrame, seed, landscape, noise);
        return sky.TakeFrames();
    }

    /// <summary>
    /// Like <see cref="Make"/>, but also returns the star catalogue and the landscape mask so tests can find where
    /// each star is. The shutter is open for <paramref name="exposure"/> of each interval (1 = whole interval, as in
    /// <see cref="Make"/>; smaller values leave a gap between consecutive images of a star).
    /// </summary>
    public static Sky Generate(int n, int w, int h, int stars, double degPerFrame, int seed, bool landscape = true,
                               double noise = 0.003, double exposure = 1.0)
    {
        var rng = new Random(seed);
        double cx = w / 2.0, cy = -h / 4.0;
        // Star catalogue: radius/angle about the pole, amplitude (mostly faint), PSF sigma.
        var catalogue = new List<SkyStar>();
        while (catalogue.Count < stars)
        {
            double x = rng.NextDouble() * w, y = rng.NextDouble() * h;
            double dx = x - cx, dy = y - cy;
            double u = rng.NextDouble();
            catalogue.Add(new SkyStar(Math.Sqrt(dx * dx + dy * dy), Math.Atan2(dy, dx), 0.06 + 0.7 * u * u * u, 0.8 + 0.6 * rng.NextDouble()));
        }

        var land = landscape ? LandscapeMask(w, h, rng) : null;
        var sky = new Sky(n, cx, cy, degPerFrame * Math.PI / 180.0, exposure, catalogue, land);
        for (int f = 0; f < n; f++)
        {
            var gray = new Mat(h, w, MatType.CV_32FC1, Scalar.All(Background));
            // The shutter stays open for `exposure` of the interval: each star is a short arc (sub-steps of the rotation).
            foreach (var s in catalogue)
                for (int k = 0; k < SubSteps; k++)
                {
                    double t = s.A + (f - n / 2) * sky.Step + sky.Step * exposure * k / SubSteps;   // same arithmetic as before `exposure`
                    Splat(gray, cx + s.R * Math.Cos(t), cy + s.R * Math.Sin(t), s.Amp / SubSteps, s.Sigma);
                }
            if (land != null) gray.SetTo(Scalar.All(LandscapeLevel), land);

            var frame = new Mat();
            Cv2.Merge(new[] { gray, gray, gray }, frame);
            gray.Dispose();
            if (noise > 0)
            {
                using var nz = new Mat(h, w, MatType.CV_32FC3);
                Cv2.SetTheRNG((ulong)seed * 1000UL + (ulong)f + 1UL);   // thread-local RNG: deterministic per frame
                Cv2.Randn(nz, Scalar.All(0), Scalar.All(noise));
                Cv2.Add(frame, nz, frame);
            }
            sky.Frames.Add(frame);
        }
        return sky;
    }

    /// <summary>Draws a 1 px streak of <paramref name="value"/> (all channels) on one frame; dashed = <paramref name="dash"/> px on, <paramref name="gap"/> px off.</summary>
    public static void DrawStreak(Mat m, Point a, Point b, double value, bool dashed, int dash = 30, int gap = 15)
    {
        if (!dashed) { Cv2.Line(m, a, b, Scalar.All(value), 1); return; }
        double len = Math.Sqrt(Math.Pow(b.X - a.X, 2) + Math.Pow(b.Y - a.Y, 2));
        double ux = (b.X - a.X) / len, uy = (b.Y - a.Y) / len;
        for (double t = 0; t < len; t += dash + gap)
        {
            double t2 = Math.Min(t + dash, len);
            var p = new Point((int)Math.Round(a.X + ux * t), (int)Math.Round(a.Y + uy * t));
            var q = new Point((int)Math.Round(a.X + ux * t2), (int)Math.Round(a.Y + uy * t2));
            Cv2.Line(m, p, q, Scalar.All(value), 1);
        }
    }

    /// <summary>Draws the same line on every frame (a static feature, e.g. a lit road or a wire).</summary>
    public static void DrawStaticLine(IList<Mat> frames, Point a, Point b, double value)
    {
        foreach (var m in frames) Cv2.Line(m, a, b, Scalar.All(value), 2);
    }

    static Mat LandscapeMask(int w, int h, Random rng)
    {
        // Jagged ridge: random walk around 75 % height, everything below is ground.
        var pts = new List<Point>();
        double y = h * 0.75;
        for (int x = 0; x <= w; x += 20)
        {
            y = Math.Clamp(y + rng.Next(-12, 13), h * 0.68, h * 0.80);
            pts.Add(new Point(x, (int)y));
        }
        pts.Add(new Point(w, h)); pts.Add(new Point(0, h));
        var mask = new Mat(h, w, MatType.CV_8UC1, Scalar.All(0));
        Cv2.FillPoly(mask, new[] { pts }, Scalar.All(255));
        return mask;
    }

    /// <summary>Adds a Gaussian star of peak <paramref name="amp"/> and PSF sigma <paramref name="sig"/> at (x, y), to every channel.</summary>
    public static void DrawStar(Mat m, double x, double y, double amp, double sig)
    {
        if (m.Channels() == 1) { Splat(m, x, y, amp, sig); return; }
        var planes = m.Split();
        foreach (var p in planes) Splat(p, x, y, amp, sig);
        Cv2.Merge(planes, m);
        foreach (var p in planes) p.Dispose();
    }

    static void Splat(Mat gray, double x, double y, double amp, double sig)
    {
        int rad = (int)Math.Ceiling(3 * sig);
        int x0 = Math.Max(0, (int)x - rad), x1 = Math.Min(gray.Cols - 1, (int)x + rad);
        int y0 = Math.Max(0, (int)y - rad), y1 = Math.Min(gray.Rows - 1, (int)y + rad);
        double k = -0.5 / (sig * sig);
        for (int r = y0; r <= y1; r++)
        {
            for (int c = x0; c <= x1; c++)
            {
                double dx = c - x, dy = r - y;
                gray.Set(r, c, (float)Math.Min(1.0, gray.At<float>(r, c) + amp * Math.Exp(k * (dx * dx + dy * dy))));
            }
        }
    }
}

/// <summary>A catalogue star: radius and angle about the pole, peak amplitude and PSF sigma.</summary>
public sealed record SkyStar(double R, double A, double Amp, double Sigma);

/// <summary>A generated sequence with its ground truth. Owns the frames (until taken) and the landscape mask.</summary>
public sealed class Sky : IDisposable
{
    internal Sky(int n, double cx, double cy, double step, double exposure, IReadOnlyList<SkyStar> stars, Mat? land)
    { N = n; Cx = cx; Cy = cy; Step = step; Exposure = exposure; Stars = stars; Land = land; }

    public List<Mat> Frames { get; } = new();
    public IReadOnlyList<SkyStar> Stars { get; }
    /// <summary>255 = landscape (static ground below the ridge), or null without landscape.</summary>
    public Mat? Land { get; }
    public int N { get; }
    public double Cx { get; }
    public double Cy { get; }
    /// <summary>Rotation per frame interval, radians.</summary>
    public double Step { get; }
    /// <summary>Fraction of the interval the shutter is open.</summary>
    public double Exposure { get; }

    /// <summary>Position of <paramref name="s"/> in frame <paramref name="f"/> at fraction <paramref name="u"/> (0..1) of that frame's exposure.</summary>
    public (double X, double Y) Position(SkyStar s, double f, double u)
    {
        double t = s.A + (f - N / 2) * Step + Step * Exposure * u;
        return (Cx + s.R * Math.Cos(t), Cy + s.R * Math.Sin(t));
    }

    /// <summary>Brightness-weighted centre of the star's image in frame <paramref name="f"/> (mean of the exposure sub-steps).</summary>
    public (double X, double Y) Centre(SkyStar s, double f) => Position(s, f, 2.5 / 6);

    /// <summary>True sky motion from frame <paramref name="from"/> to frame <paramref name="to"/>: the rotation about the pole by (to − from)·Step.</summary>
    public (double X, double Y) Rotate(double x, double y, int from, int to)
    {
        double t = (to - from) * Step, c = Math.Cos(t), s = Math.Sin(t), dx = x - Cx, dy = y - Cy;
        return (Cx + c * dx - s * dy, Cy + s * dx + c * dy);
    }

    /// <summary>Distance the star moves between consecutive frames, px.</summary>
    public double Jump(SkyStar s) => s.R * Step;

    /// <summary>Hands the frames to the caller (they are no longer disposed with the sky).</summary>
    public List<Mat> TakeFrames() { var f = new List<Mat>(Frames); Frames.Clear(); return f; }

    public void Dispose()
    {
        foreach (var m in Frames) m.Dispose();
        Frames.Clear();
        Land?.Dispose();
    }
}