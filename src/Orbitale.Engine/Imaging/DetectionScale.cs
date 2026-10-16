using OpenCvSharp;

namespace Orbitale.Engine.Imaging;

/// <summary>
/// The working scale of the detectors (trails, stars): frames whose long side exceeds <see cref="LongSide"/> px are
/// area-downscaled to it, smaller frames are used as they are. Full-resolution coordinates map from detection
/// coordinates through pixel centres: full = (det + 0.5)·s − 0.5.
/// </summary>
internal static class DetectionScale
{
    /// <summary>The scale the prototype was validated at.</summary>
    public const int LongSide = 2000;

    /// <summary>Detection-scale size for a full-resolution frame of <paramref name="full"/> (unchanged when the long side is ≤ <see cref="LongSide"/>).</summary>
    public static Size For(Size full)
    {
        int fullLong = Math.Max(full.Width, full.Height);
        return fullLong > LongSide
            ? new Size((int)Math.Round((double)full.Width * LongSide / fullLong), (int)Math.Round((double)full.Height * LongSide / fullLong))
            : full;
    }

    /// <summary>Gray CV_32FC1 copy of a BGR frame at <paramref name="detSize"/> (area downscale first when the sizes differ).</summary>
    public static Mat ToGray(Mat bgr, Size detSize)
    {
        var gray = new Mat();
        if (bgr.Size() == detSize)
        {
            Cv2.CvtColor(bgr, gray, ColorConversionCodes.BGR2GRAY);
            return gray;
        }
        using var small = new Mat();
        Cv2.Resize(bgr, small, detSize, 0, 0, InterpolationFlags.Area);
        Cv2.CvtColor(small, gray, ColorConversionCodes.BGR2GRAY);
        return gray;
    }
}
