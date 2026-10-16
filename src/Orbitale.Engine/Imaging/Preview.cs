using System.Runtime.InteropServices;
using OpenCvSharp;

namespace Orbitale.Engine.Imaging;

public sealed record PreviewImage(int Width, int Height, byte[] Bgra);

public static class Preview
{
    public static PreviewImage Render(Mat image32f, int maxLongSide)
    {
        var size = SizeFor(image32f.Width, image32f.Height, maxLongSide);
        using var scaled = new Mat();
        Mat src = image32f;
        if (size != image32f.Size())
        {
            Cv2.Resize(image32f, scaled, size, 0, 0, InterpolationFlags.Area);
            src = scaled;
        }
        using var u8 = new Mat();
        src.ConvertTo(u8, MatType.CV_8UC3, 255.0);
        using var bgra = new Mat();
        Cv2.CvtColor(u8, bgra, ColorConversionCodes.BGR2BGRA);
        using var cont = bgra.Clone();
        var bytes = new byte[cont.Width * cont.Height * 4];
        Marshal.Copy(cont.Data, bytes, 0, bytes.Length);
        return new PreviewImage(cont.Width, cont.Height, bytes);
    }

    /// <summary>An opaque black preview of the size <see cref="Render"/> would produce for a <paramref name="width"/>×<paramref name="height"/> image.</summary>
    public static PreviewImage Blank(int width, int height, int maxLongSide)
    {
        var size = SizeFor(width, height, maxLongSide);
        var bytes = new byte[size.Width * size.Height * 4];
        for (int i = 3; i < bytes.Length; i += 4) bytes[i] = 255;
        return new PreviewImage(size.Width, size.Height, bytes);
    }

    /// <summary>The preview size: the long side scaled down to <paramref name="maxLongSide"/> (never up; ≤ 0 = full size).</summary>
    internal static Size SizeFor(int w, int h, int maxLongSide)
    {
        int longSide = Math.Max(w, h);
        if (maxLongSide <= 0 || longSide <= maxLongSide) return new Size(w, h);
        double f = (double)maxLongSide / longSide;
        return new Size(Math.Max(1, (int)Math.Round(w * f, MidpointRounding.AwayFromZero)), Math.Max(1, (int)Math.Round(h * f, MidpointRounding.AwayFromZero)));
    }
}
