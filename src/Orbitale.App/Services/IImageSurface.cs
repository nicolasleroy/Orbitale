using Orbitale.Engine.Imaging;
using Orbitale.Engine.Trails;

namespace Orbitale.App.Services;

/// <summary>What the view model asks of the window's image view and strip (implemented by the window).</summary>
public interface IImageSurface
{
    /// <summary>Shows a BGRA image laid out as an image <paramref name="sourceWidth"/> pixels wide (the full-resolution width).</summary>
    void Show(PreviewImage image, double sourceWidth);

    /// <summary>Briefly outlines removed trails, in pixels of the image last shown.</summary>
    void FlashTrails(IReadOnlyList<LineSegment> previewCoords);

    /// <summary>Scrolls the thumbnail strip so <paramref name="frame"/> is visible.</summary>
    void BringIntoView(object frame);
}
