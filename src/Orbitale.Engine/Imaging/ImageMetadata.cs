using ImageMagick;

namespace Orbitale.Engine.Imaging;

public sealed class ImageMetadata
{
    public byte[]? IccProfile { get; init; }
    public byte[]? Exif { get; init; }

    public static ImageMetadata FromFile(string path)
    {
        try
        {
            using var img = new MagickImage();
            img.Ping(path);
            return new ImageMetadata
            {
                IccProfile = img.GetColorProfile()?.ToByteArray(),
                Exif = img.GetExifProfile()?.ToByteArray(),
            };
        }
        catch (MagickException e)
        {
            throw new InvalidDataException($"Cannot read metadata '{Path.GetFileName(path)}': {e.Message}", e);
        }
    }
}
