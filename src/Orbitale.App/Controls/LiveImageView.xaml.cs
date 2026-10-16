using System.Runtime.InteropServices.WindowsRuntime;
using System.Windows.Input;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Orbitale.Engine.Imaging;
using LineSegment = Orbitale.Engine.Trails.LineSegment;
using Windows.Foundation;
using Windows.UI;

namespace Orbitale.App.Controls;

/// <summary>
/// The image area: a zoomable, pannable view of a BGRA image (the live preview, the full-resolution result or an
/// original frame), a badge, and a fading amber outline of the trails removed from the last frame.
/// The image is laid out at its <b>source</b> size (the full-resolution width given to <see cref="Show"/>), so a low
/// resolution preview and the full result occupy the same space and zoom 1 is true 100 % (one source pixel per screen
/// pixel) whatever is shown. Double-click toggles between zoom-to-fit and 100 %.
/// </summary>
public sealed partial class LiveImageView : UserControl
{
    const double FlashOpacity = 0.8, FlashSeconds = 1.0, FlashThickness = 3;
    static readonly SolidColorBrush Amber = new(Color.FromArgb(0xFF, 0xF0, 0xA8, 0x30));

    public static readonly DependencyProperty HintProperty = DependencyProperty.Register(
        nameof(Hint), typeof(string), typeof(LiveImageView), new PropertyMetadata(""));

    public static readonly DependencyProperty BadgeTextProperty = DependencyProperty.Register(
        nameof(BadgeText), typeof(string), typeof(LiveImageView),
        new PropertyMetadata("", (d, _) => ((LiveImageView)d).UpdateBadge()));

    public static readonly DependencyProperty BackCommandProperty = DependencyProperty.Register(
        nameof(BackCommand), typeof(ICommand), typeof(LiveImageView), new PropertyMetadata(null));

    public static readonly DependencyProperty IsBackVisibleProperty = DependencyProperty.Register(
        nameof(IsBackVisible), typeof(bool), typeof(LiveImageView),
        new PropertyMetadata(false, (d, _) => ((LiveImageView)d).UpdateBadge()));

    readonly List<(Line Line, long Started)> _flashes = [];
    readonly DispatcherQueueTimer _fadeTimer;
    WriteableBitmap? _bitmap;
    double _surfaceWidth, _surfaceHeight;   // DIPs
    double _dipsPerPixel;                    // DIPs per pixel of the image shown (maps overlay coordinates)
    bool _fit = true;

    public LiveImageView()
    {
        InitializeComponent();
        _fadeTimer = DispatcherQueue.CreateTimer();
        _fadeTimer.Interval = TimeSpan.FromMilliseconds(33);
        _fadeTimer.Tick += (_, _) => FadeFlashes();
    }

    public string Hint
    {
        get => (string)GetValue(HintProperty);
        set => SetValue(HintProperty, value);
    }

    public string BadgeText
    {
        get => (string)GetValue(BadgeTextProperty);
        set => SetValue(BadgeTextProperty, value);
    }

    public ICommand? BackCommand
    {
        get => (ICommand?)GetValue(BackCommandProperty);
        set => SetValue(BackCommandProperty, value);
    }

    public bool IsBackVisible
    {
        get => (bool)GetValue(IsBackVisibleProperty);
        set => SetValue(IsBackVisibleProperty, value);
    }

    /// <summary>
    /// Shows <paramref name="img"/>, laid out as an image <paramref name="sourceWidth"/> pixels wide. The bitmap is reused
    /// while the image size is unchanged (only the pixels are copied). The view fits the window when the layout size
    /// changes, and otherwise keeps its zoom and position.
    /// </summary>
    public void Show(PreviewImage img, double sourceWidth)
    {
        if (_bitmap is null || _bitmap.PixelWidth != img.Width || _bitmap.PixelHeight != img.Height)
        {
            Picture.Source = null;   // let the old bitmap go before the new one is allocated
            _bitmap = new WriteableBitmap(img.Width, img.Height);
            Picture.Source = _bitmap;
        }
        using (var pixels = _bitmap.PixelBuffer.AsStream())
            pixels.Write(img.Bgra, 0, Math.Min(img.Bgra.Length, (int)pixels.Length));
        _bitmap.Invalidate();

        double scale = XamlRoot?.RasterizationScale ?? 1;
        double width = sourceWidth / scale, height = width * img.Height / img.Width;
        _dipsPerPixel = width / img.Width;
        HintText.Visibility = Visibility.Collapsed;
        if (Math.Abs(width - _surfaceWidth) > 0.5 || Math.Abs(height - _surfaceHeight) > 0.5)
        {
            _surfaceWidth = width;
            _surfaceHeight = height;
            Surface.Width = width;
            Surface.Height = height;
            ClearFlashes();
            Scroller.UpdateLayout();
            ZoomToFit();
        }
    }

    /// <summary>Removes the image and releases its bitmap, so a full-resolution one does not outlive its use; the hint shows again.</summary>
    public void Clear()
    {
        Picture.Source = null;
        _bitmap = null;
        _surfaceWidth = _surfaceHeight = 0;   // the next image fits the view
        ClearFlashes();
        HintText.Visibility = Visibility.Visible;
    }

    /// <summary>Outlines <paramref name="previewCoords"/> (pixels of the image last shown) in amber, fading out over a second.</summary>
    public void FlashTrails(IReadOnlyList<LineSegment> previewCoords)
    {
        if (_bitmap is null || previewCoords.Count == 0) return;
        double k = _dipsPerPixel, thickness = FlashThickness / Scroller.ZoomFactor;   // 3 screen pixels at any zoom
        long now = Environment.TickCount64;
        foreach (var s in previewCoords)
        {
            var line = new Line
            {
                X1 = (s.X1 + 0.5) * k, Y1 = (s.Y1 + 0.5) * k, X2 = (s.X2 + 0.5) * k, Y2 = (s.Y2 + 0.5) * k,
                Stroke = Amber,
                StrokeThickness = thickness,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Opacity = FlashOpacity,
            };
            Overlay.Children.Add(line);
            _flashes.Add((line, now));
        }
        if (!_fadeTimer.IsRunning) _fadeTimer.Start();
    }

    public void ZoomToFit()
    {
        _fit = true;
        if (FitZoom() is not { } z) return;
        Scroller.ChangeView(0, 0, z, true);
    }

    /// <summary>True 100 %: one image pixel per screen pixel, around the centre of the view.</summary>
    public void ZoomTo100() => ZoomAround(1f, null);

    float? FitZoom()
    {
        if (_surfaceWidth <= 0 || Scroller.ViewportWidth <= 0 || Scroller.ViewportHeight <= 0) return null;
        double z = Math.Min(Scroller.ViewportWidth / _surfaceWidth, Scroller.ViewportHeight / _surfaceHeight);
        return (float)Math.Clamp(z, Scroller.MinZoomFactor, Scroller.MaxZoomFactor);
    }

    /// <summary>Zooms to <paramref name="zoom"/> keeping <paramref name="at"/> (surface coordinates; default: the view's centre) in the middle.</summary>
    void ZoomAround(float zoom, Point? at)
    {
        if (_surfaceWidth <= 0) return;
        _fit = false;
        double current = Scroller.ZoomFactor;
        var centre = at ?? new Point(
            (Scroller.HorizontalOffset + Scroller.ViewportWidth / 2) / current,
            (Scroller.VerticalOffset + Scroller.ViewportHeight / 2) / current);
        double cx = Math.Clamp(centre.X, 0, _surfaceWidth), cy = Math.Clamp(centre.Y, 0, _surfaceHeight);
        Scroller.ChangeView(cx * zoom - Scroller.ViewportWidth / 2, cy * zoom - Scroller.ViewportHeight / 2, zoom, true);
    }

    void Scroller_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (_bitmap is null) return;
        if (Math.Abs(Scroller.ZoomFactor - 1) < 0.01) ZoomToFit();
        else ZoomAround(1f, e.GetPosition(Surface));
        e.Handled = true;
    }

    void Scroller_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_fit) ZoomToFit();
    }

    void Scroller_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        // A zoom by the user (Ctrl+wheel, pinch) leaves fit mode, so resizing the window no longer refits.
        if (!e.IsIntermediate && _fit && FitZoom() is { } fit && Math.Abs(Scroller.ZoomFactor - fit) > 0.01) _fit = false;
    }

    void FadeFlashes()
    {
        long now = Environment.TickCount64;
        for (int i = _flashes.Count - 1; i >= 0; i--)
        {
            var (line, started) = _flashes[i];
            double t = (now - started) / 1000.0 / FlashSeconds;
            if (t >= 1)
            {
                Overlay.Children.Remove(line);
                _flashes.RemoveAt(i);
            }
            else line.Opacity = FlashOpacity * (1 - t);
        }
        if (_flashes.Count == 0) _fadeTimer.Stop();
    }

    void ClearFlashes()
    {
        _flashes.Clear();
        Overlay.Children.Clear();
        _fadeTimer.Stop();
    }

    void UpdateBadge()
    {
        Badge.Visibility = string.IsNullOrEmpty(BadgeText) ? Visibility.Collapsed : Visibility.Visible;
        BackButton.Visibility = IsBackVisible ? Visibility.Visible : Visibility.Collapsed;
    }
}
