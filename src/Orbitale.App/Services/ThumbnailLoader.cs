using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;
using Windows.Storage.FileProperties;

namespace Orbitale.App.Services;

/// <summary>
/// Shell thumbnails (Windows uses the JPEG's embedded preview, so a 40 MP frame is never decoded here).
/// Must be called on the UI thread: <see cref="BitmapImage"/> is a XAML object.
/// </summary>
public sealed class ThumbnailLoader
{
    readonly SemaphoreSlim _gate = new(4);

    public async Task<ImageSource?> LoadAsync(string path, uint size)
    {
        await _gate.WaitAsync();
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            using var thumb = await file.GetThumbnailAsync(ThumbnailMode.SingleItem, size, ThumbnailOptions.ResizeThumbnail);
            if (thumb is null) return null;
            var bmp = new BitmapImage();
            await bmp.SetSourceAsync(thumb);
            return bmp;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException
                                      or System.Runtime.InteropServices.COMException)
        {
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }
}
