using System.Runtime.InteropServices;
using OpenCvSharp;

namespace Orbitale.Engine.Imaging;

public sealed record PreviewImage(int Width, int Height, byte[] Bgra);

public static class Preview
{
    public static PreviewImage Render(Mat image32f, int maxLongSide)
    {
        int w = image32f.Width, h = image32f.Height;
        int longSide = Math.Max(w, h);
        using var scaled = new Mat();
        Mat src = image32f;
        if (maxLongSide > 0 && longSide > maxLongSide)
        {
            double f = (double)maxLongSide / longSide;
            int nw = Math.Max(1, (int)Math.Round(w * f, MidpointRounding.AwayFromZero)), nh = Math.Max(1, (int)Math.Round(h * f, MidpointRounding.AwayFromZero));
            Cv2.Resize(image32f, scaled, new Size(nw, nh), 0, 0, InterpolationFlags.Area);
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
}
