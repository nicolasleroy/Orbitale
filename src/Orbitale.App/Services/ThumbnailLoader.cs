using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;
using Windows.Storage.FileProperties;

namespace Orbitale.App.Services;

/// <summary>
/// Shell thumbnails (Windows uses the JPEG's embedded preview, so a 40 MP frame is never decoded here).
/// Must be called on the UI thread: <see cref="BitmapImage"/> is a XAML object. Scheduling and error handling are
/// <see cref="ThumbnailQueue{TImage}"/>'s job.
/// </summary>
public static class ThumbnailLoader
{
    public static async Task<ImageSource?> LoadAsync(string path, uint size, CancellationToken token)
    {
        var file = await StorageFile.GetFileFromPathAsync(path).AsTask(token);
        using var thumb = await file.GetThumbnailAsync(ThumbnailMode.SingleItem, size, ThumbnailOptions.ResizeThumbnail).AsTask(token);
        if (thumb is null) return null;
        token.ThrowIfCancellationRequested();
        var bmp = new BitmapImage();
        await bmp.SetSourceAsync(thumb).AsTask(token);
        return bmp;
    }
}
