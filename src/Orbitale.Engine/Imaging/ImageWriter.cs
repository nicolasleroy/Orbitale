using System.Runtime.InteropServices;
using ImageMagick;
using OpenCvSharp;

namespace Orbitale.Engine.Imaging;

public static class ImageWriter
{
    public static void Save(Mat image32f, string path, ImageMetadata meta)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        bool tiff;
        switch (ext)
        {
            case ".jpg": case ".jpeg": tiff = false; break;
            case ".tif": case ".tiff": tiff = true; break;
            default: throw new NotSupportedException($"Unsupported output format '{ext}'.");
        }

        int w = image32f.Width, h = image32f.Height;
        using var converted = new Mat();
        // ConvertTo saturates on the integer conversion.
        image32f.ConvertTo(converted, tiff ? MatType.CV_16UC3 : MatType.CV_8UC3, tiff ? 65535.0 : 255.0);
        using var cont = converted.Clone();
        var bytes = new byte[w * h * 3 * (tiff ? 2 : 1)];
        Marshal.Copy(cont.Data, bytes, 0, bytes.Length);

        var settings = new PixelReadSettings((uint)w, (uint)h, tiff ? StorageType.Short : StorageType.Char, "BGR");
        using var img = new MagickImage();
        img.ReadPixels(bytes, settings);

        if (meta.IccProfile is { } icc) img.SetProfile(new ColorProfile(icc));
        if (meta.Exif is { } exifBytes)
        {
            var exif = new ExifProfile(exifBytes);
            exif.SetValue(ExifTag.Orientation, (ushort)1);
            img.SetProfile(exif);
        }
        img.Orientation = Orientation.TopLeft;

        if (tiff)
        {
            img.Depth = 16;
            img.Settings.Compression = CompressionMethod.Zip;
        }
        else
        {
            img.Depth = 8;
            img.Quality = 95;
        }

        var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var tmp = Path.Combine(dir, $".{Guid.NewGuid():N}{ext}");
        try
        {
            img.Write(tmp);
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }
}
