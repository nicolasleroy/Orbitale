using ImageMagick;
using OpenCvSharp;

namespace Orbitale.Engine.Imaging;

public sealed record ImageHeader(int Width, int Height);

public static class ImageReader
{
    /// <summary>Reads an image as CV_32FC3, BGR, 0..1. EXIF orientation is applied by the decoder.</summary>
    public static Mat Read(string path)
    {
        Mat? raw = null;
        try
        {
            // Bytes-based decode: Cv2.ImRead fails on non-ASCII paths on Windows.
            var bytes = File.ReadAllBytes(path);
            raw = Cv2.ImDecode(bytes, ImreadModes.AnyDepth | ImreadModes.Color);
            if (raw.Empty())
                throw new InvalidDataException($"Cannot decode image '{Path.GetFileName(path)}'.");

            double scale = raw.Depth() switch
            {
                MatType.CV_8U => 1.0 / 255.0,
                MatType.CV_16U => 1.0 / 65535.0,
                _ => throw new InvalidDataException($"Unsupported pixel depth in '{Path.GetFileName(path)}'."),
            };
            var result = new Mat();
            raw.ConvertTo(result, MatType.CV_32FC3, scale);
            return result;
        }
        catch (Exception e) when (e is not InvalidDataException)
        {
            throw new InvalidDataException($"Cannot read image '{Path.GetFileName(path)}': {e.Message}", e);
        }
        finally
        {
            raw?.Dispose();
        }
    }

    /// <summary>Reads oriented dimensions without decoding pixels.</summary>
    public static ImageHeader ReadHeader(string path)
    {
        try
        {
            var info = new MagickImageInfo(path);
            int w = (int)info.Width, h = (int)info.Height;
            // Orientations 5..8 are the transposed/rotated-90 ones.
            return info.Orientation is Orientation.LeftTop or Orientation.RightTop
                or Orientation.RightBottom or Orientation.LeftBottom
                ? new ImageHeader(h, w)
                : new ImageHeader(w, h);
        }
        catch (MagickException e)
        {
            throw new InvalidDataException($"Cannot read image header '{Path.GetFileName(path)}': {e.Message}", e);
        }
    }
}
