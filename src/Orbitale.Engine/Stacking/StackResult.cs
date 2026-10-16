using OpenCvSharp;
using Orbitale.Engine.Imaging;

namespace Orbitale.Engine.Stacking;

/// <summary>The stacked image of a finished or stopped run (owned; disposed with the result).</summary>
public sealed class StackResult : IDisposable
{
    internal StackResult(Mat image, bool completed, int framesProcessed, ImageMetadata metadata, int trailRemovalSkipped = 0)
    {
        Image = image; Completed = completed; FramesProcessed = framesProcessed; Metadata = metadata;
        TrailRemovalSkipped = trailRemovalSkipped;
    }

    /// <summary>CV_32FC3, BGR, 0..1, at full input resolution.</summary>
    public Mat Image { get; }

    /// <summary>False when the run was stopped: <see cref="Image"/> then holds the partial stack.</summary>
    public bool Completed { get; }

    public int FramesProcessed { get; }

    /// <summary>
    /// Frames left as they were by trail removal because no neighbour's sky could be aligned with theirs (clouds, too few
    /// stars, a long lens or interval). Only frames downscaled for detection (long side over 2000 px) are skipped: the
    /// unaligned detection, validated at that scale only, damages star trails at full resolution.
    /// </summary>
    public int TrailRemovalSkipped { get; }

    /// <summary>ICC profile and EXIF of the sequence's first frame, written with <see cref="Save"/>.</summary>
    public ImageMetadata Metadata { get; }

    /// <summary>Writes the image as JPG (8-bit, q95) or 16-bit TIFF, by extension.</summary>
    public void Save(string path) => ImageWriter.Save(Image, path, Metadata);

    /// <summary>Full-resolution BGRA rendering for 100 % zoom.</summary>
    public PreviewImage RenderFull() => Preview.Render(Image, 0);

    public void Dispose() => Image.Dispose();
}
