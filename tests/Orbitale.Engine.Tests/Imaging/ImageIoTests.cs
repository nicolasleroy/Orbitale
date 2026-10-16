using ImageMagick;
using OpenCvSharp;
using Orbitale.Engine.Imaging;

namespace Orbitale.Engine.Tests.Imaging;

public class ImageIoTests
{
    static string Sample(int n) => Path.Combine(AppContext.BaseDirectory, "samples", $"{n}.jpg");
    static string Tmp(string name) { var d = Path.Combine(Path.GetTempPath(), "orbitale-tests", "Été 2026"); Directory.CreateDirectory(d); return Path.Combine(d, name); }

    [Fact] public void ReadsJpgAsFloatBgr()
    {
        using var m = ImageReader.Read(Sample(1));
        Assert.Equal(MatType.CV_32FC3, m.Type());
        Assert.Equal((2000, 1333), (m.Width, m.Height));
        m.MinMaxLoc(out double min, out double max); Assert.InRange(min, 0, 1); Assert.InRange(max, 0, 1);
    }

    [Fact] public void ReadsAndWritesNonAsciiPath()
    {
        var p = Tmp("frame é.jpg"); File.Copy(Sample(1), p, true);
        using var m = ImageReader.Read(p);
        var outp = Tmp("résultat.tif"); ImageWriter.Save(m, outp, ImageMetadata.FromFile(p));
        Assert.True(File.Exists(outp));
    }

    [Fact] public void Tiff16RoundTripKeepsDepthAndAdobeRgbProfile()
    {
        var src = Tmp("in16.tif");
        using (var img = new MagickImage(Sample(1))) { img.Depth = 16; img.SetProfile(ColorProfiles.AdobeRGB1998); img.Write(src); }
        using var m = ImageReader.Read(src);
        var meta = ImageMetadata.FromFile(src);
        var dst = Tmp("out16.tif"); ImageWriter.Save(m, dst, meta);
        using var back = new MagickImage(dst);
        Assert.Equal(16u, back.Depth);
        Assert.Equal(meta.IccProfile, back.GetColorProfile()!.ToByteArray());
    }

    [Fact] public void JpgToTiff16KeepsFirstFrameIccAndExif()
    {
        var meta = ImageMetadata.FromFile(Sample(1));
        using var m = ImageReader.Read(Sample(1));
        var dst = Tmp("out.tif"); ImageWriter.Save(m, dst, meta);
        using var back = new MagickImage(dst);
        Assert.Equal(16u, back.Depth);
        if (meta.IccProfile is not null) Assert.Equal(meta.IccProfile, back.GetColorProfile()!.ToByteArray());
        Assert.Equal(Orientation.TopLeft, back.Orientation);
    }

    [Fact] public void PreviewFitsLongSide()
    {
        using var m = ImageReader.Read(Sample(1));
        var p = Preview.Render(m, 1000);
        Assert.Equal((1000, 667), (p.Width, p.Height));
        Assert.Equal(1000 * 667 * 4, p.Bgra.Length);
    }

    [Fact] public void UnknownExtensionThrows()
    { using var m = new Mat(4, 4, MatType.CV_32FC3, Scalar.All(0.5)); Assert.Throws<NotSupportedException>(() => ImageWriter.Save(m, Tmp("x.bmp"), new ImageMetadata())); }
}
