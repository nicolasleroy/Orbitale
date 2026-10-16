using OpenCvSharp;
using Orbitale.Engine.Imaging;

namespace Orbitale.Engine.GapFill;

/// <summary>
/// Closes the gaps between consecutive images of each star: for every star matched between the previous and the
/// current frame, an anti-aliased segment from its previous to its current position is drawn onto the current frame
/// with max, before blending. Unmatched stars (e.g. one that set behind the landscape) draw nothing.
/// </summary>
public sealed class GapFiller
{
    /// <param name="strength">0..1; sets the matching radius r = 2 + 10·strength detection px (the largest gap bridged).</param>
    public GapFiller(double strength)
    {
        if (!(strength >= 0 && strength <= 1)) throw new ArgumentOutOfRangeException(nameof(strength), "Strength must be in 0..1.");
        RadiusDetectPx = 2 + 10 * strength;
    }

    /// <summary>Matching radius, detection-scale px.</summary>
    public double RadiusDetectPx { get; }

    /// <summary>
    /// Draws the segments onto <paramref name="currentFrame"/> (CV_32FC3) in place. The result equals max(frame, layer),
    /// where the layer holds each segment as an anti-aliased capsule of width max(1, round(1.6·mean radius)), with
    /// colour = per-channel min of the pair's colours scaled by pixel coverage. Only each segment's bounding box is
    /// touched; nothing is allocated at full resolution.
    /// </summary>
    public void Apply(Mat currentFrame, IReadOnlyList<Star> prevStars, IReadOnlyList<Star> curStars)
    {
        ArgumentNullException.ThrowIfNull(currentFrame);
        ArgumentNullException.ThrowIfNull(prevStars);
        ArgumentNullException.ThrowIfNull(curStars);
        if (currentFrame.Type() != MatType.CV_32FC3)
            throw new ArgumentException($"Frame is {currentFrame.Type()}, expected CV_32FC3.", nameof(currentFrame));

        var size = currentFrame.Size();
        var det = DetectionScale.For(size);
        double scale = Math.Sqrt((double)size.Width / det.Width * size.Height / det.Height);
        foreach (var (a, b) in StarMatcher.Match(prevStars, curStars, RadiusDetectPx, scale))
        {
            int thickness = Math.Max(1, (int)Math.Round(1.6 * (a.Radius + b.Radius) / 2));
            var color = new Vec3f(Math.Min(a.Color.Item0, b.Color.Item0), Math.Min(a.Color.Item1, b.Color.Item1), Math.Min(a.Color.Item2, b.Color.Item2));
            DrawMax(currentFrame, a.X, a.Y, b.X, b.Y, thickness, color);
        }
    }

    /// <summary>
    /// frame = max(frame, color·coverage) for an anti-aliased capsule of width <paramref name="thickness"/> around the
    /// segment (x1, y1)–(x2, y2): coverage = clamp(thickness/2 + 0.5 − distance, 0, 1), a one-pixel linear edge centred
    /// on the capsule's border, so the drawn width (FWHM) equals the thickness.
    /// </summary>
    static unsafe void DrawMax(Mat frame, double x1, double y1, double x2, double y2, int thickness, Vec3f color)
    {
        double half = thickness / 2.0, reach = half + 0.5;
        int c0 = Math.Max(0, (int)Math.Floor(Math.Min(x1, x2) - reach)), c1 = Math.Min(frame.Cols - 1, (int)Math.Ceiling(Math.Max(x1, x2) + reach));
        int r0 = Math.Max(0, (int)Math.Floor(Math.Min(y1, y2) - reach)), r1 = Math.Min(frame.Rows - 1, (int)Math.Ceiling(Math.Max(y1, y2) + reach));
        double dx = x2 - x1, dy = y2 - y1, l2 = dx * dx + dy * dy;
        for (int r = r0; r <= r1; r++)
        {
            float* f = (float*)frame.Ptr(r);
            for (int c = c0; c <= c1; c++)
            {
                double t = l2 == 0 ? 0 : Math.Clamp(((c - x1) * dx + (r - y1) * dy) / l2, 0, 1);
                double px = x1 + t * dx - c, py = y1 + t * dy - r;
                double coverage = reach - Math.Sqrt(px * px + py * py);
                if (coverage <= 0) continue;
                float k = (float)Math.Min(1, coverage);
                int i = 3 * c;
                f[i] = Math.Max(f[i], color.Item0 * k);
                f[i + 1] = Math.Max(f[i + 1], color.Item1 * k);
                f[i + 2] = Math.Max(f[i + 2], color.Item2 * k);
            }
        }
    }
}
