using System.Runtime.InteropServices.WindowsRuntime;
using System.Windows.Input;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Shapes;
using Orbitale.Engine.Imaging;
using LineSegment = Orbitale.Engine.Trails.LineSegment;
using Windows.Foundation;
using VirtualKeyModifiers = Windows.System.VirtualKeyModifiers;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace Orbitale.App.Controls;

/// <summary>
/// The image area: a zoomable, pannable view of a BGRA image (the live preview, the full-resolution result or an
/// original frame), a badge, and a fading amber outline of the trails removed from the last frame.
/// The image is laid out at its <b>source</b> size (the full-resolution width given to <see cref="Show"/>), so a low
/// resolution preview and the full result occupy the same space and zoom 1 is true 100 % (one source pixel per screen
/// pixel) whatever is shown. Double-click toggles between zoom-to-fit and 100 %.
/// A click on the image (mouse or pen) opens a loupe that shows the image at 100 % around that point, like Adobe
/// Bridge's. It opens once the double-click time has passed (so a double-click only zooms), or as soon as the press
/// turns into a drag. Dragging on the image or dragging the loupe itself moves it, a click elsewhere on the image moves
/// it there, and its close button (or Escape) closes it. Scrolling or zooming the view carries it along with the image.
/// </summary>
public sealed partial class LiveImageView : UserControl
{
    const double FlashOpacity = 0.8, FlashSeconds = 1.0, FlashThickness = 3;
    const double DragThreshold = 4;   // DIPs a press must move to count as a drag of the loupe's point
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
    enum LoupeDrag { None, Tip, Body }
    LoupeDrag _drag;                         // what the pointer drags: the magnified point (on the image) or the loupe
    Point _tip, _grab;                       // the loupe's sharp corner, and where the loupe was grabbed from it (view DIPs)
    Point _at;                               // the magnified point (surface DIPs), which the loupe follows when the view moves
    Point? _pendingAt;                       // a click that opens the loupe unless it turns out to be a double-click (surface DIPs)
    readonly DispatcherQueueTimer _loupeTimer;
    bool _left, _up;                         // the side of its sharp corner the loupe hangs on

    public LiveImageView()
    {
        InitializeComponent();
        _fadeTimer = DispatcherQueue.CreateTimer();
        _fadeTimer.Interval = TimeSpan.FromMilliseconds(33);
        _fadeTimer.Tick += (_, _) => FadeFlashes();
        _loupeTimer = DispatcherQueue.CreateTimer();
        _loupeTimer.Interval = TimeSpan.FromMilliseconds(new UISettings().DoubleClickTime);
        _loupeTimer.IsRepeating = false;
        _loupeTimer.Tick += (_, _) => OpenPendingLoupe();
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
            Picture.Source = LoupePicture.Source = null;   // let the old bitmap go before the new one is allocated
            _bitmap = new WriteableBitmap(img.Width, img.Height);
            Picture.Source = LoupePicture.Source = _bitmap;   // the loupe shows the same bitmap: live updates show there too
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
            LoupePicture.Width = width;   // at 100 %: one image pixel per screen pixel
            LoupePicture.Height = height;
            ClearFlashes();
            HideLoupe();
            Scroller.UpdateLayout();
            ZoomToFit();
        }
    }

    /// <summary>Removes the image and releases its bitmap, so a full-resolution one does not outlive its use; the hint shows again.</summary>
    public void Clear()
    {
        HideLoupe();
        Picture.Source = null;
        LoupePicture.Source = null;
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
        HideLoupe();
        if (Math.Abs(Scroller.ZoomFactor - 1) < 0.01) ZoomToFit();
        else ZoomAround(1f, e.GetPosition(Surface));
        e.Handled = true;
    }

    void Scroller_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_fit) ZoomToFit();
        FollowImage();   // a resize moves the image (re-centred, or refitted) without a view change
    }

    void Scroller_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        FollowImage();
        // A zoom by the user (Ctrl+wheel, pinch) leaves fit mode, so resizing the window no longer refits.
        if (!e.IsIntermediate && _fit && FitZoom() is { } fit && Math.Abs(Scroller.ZoomFactor - fit) > 0.01) _fit = false;
    }

    void Picture_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_bitmap is null || e.Pointer.PointerDeviceType == PointerDeviceType.Touch) return;   // touch pans
        if (!e.GetCurrentPoint(Picture).Properties.IsLeftButtonPressed) return;
        if (Picture.CapturePointer(e.Pointer)) _drag = LoupeDrag.Tip;
        if (Loupe.Visibility == Visibility.Visible) PlaceLoupe(e.GetCurrentPoint(LoupeLayer).Position, keepSide: false);
        else
        {
            // Wait out the double-click time: a double-click zooms and must not flash the loupe open and shut. The point
            // is kept on the image, so a view that moves meanwhile still opens the loupe on what was clicked.
            _pendingAt = e.GetCurrentPoint(Surface).Position;
            _loupeTimer.Stop();
            _loupeTimer.Start();
        }
        e.Handled = true;
    }

    void Picture_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_drag != LoupeDrag.Tip) return;
        var p = e.GetCurrentPoint(LoupeLayer).Position;
        if (_pendingAt is { } pending)
        {
            var start = ToView(pending);
            if (Math.Abs(p.X - start.X) < DragThreshold && Math.Abs(p.Y - start.Y) < DragThreshold) return;
            CancelPendingLoupe();   // a drag is not a double-click: open now
        }
        PlaceLoupe(p, keepSide: false);
    }

    void OpenPendingLoupe()
    {
        _loupeTimer.Stop();
        if (_pendingAt is not { } at) return;
        _pendingAt = null;
        var tip = ToView(at);
        if (InView(tip)) PlaceLoupe(tip, keepSide: false);   // else the clicked point has scrolled away
    }

    void CancelPendingLoupe()
    {
        _loupeTimer.Stop();
        _pendingAt = null;
    }

    /// <summary>A surface point (DIPs) in the loupe layer's (view) coordinates.</summary>
    Point ToView(Point surface) => Surface.TransformToVisual(LoupeLayer).TransformPoint(surface);

    bool InView(Point tip) => tip.X >= 0 && tip.Y >= 0 && tip.X <= LoupeLayer.ActualWidth && tip.Y <= LoupeLayer.ActualHeight;

    /// <summary>After the view moved under it (scroll, zoom, resize), puts the loupe back on its point of the image.</summary>
    void FollowImage()
    {
        if (Loupe.Visibility != Visibility.Visible || _drag != LoupeDrag.None || _bitmap is null) return;
        var tip = ToView(_at);
        if (InView(tip)) PlaceLoupe(tip, keepSide: false);
        else HideLoupe();   // scrolled out of view
    }

    void Picture_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_drag != LoupeDrag.Tip) return;
        _drag = LoupeDrag.None;
        Picture.ReleasePointerCapture(e.Pointer);
    }

    /// <summary>Grabbing the loupe drags it: its sharp corner moves with the pointer and the view inside follows.</summary>
    void Loupe_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        e.Handled = true;
        if (!e.GetCurrentPoint(Loupe).Properties.IsLeftButtonPressed || !Loupe.CapturePointer(e.Pointer)) return;
        var p = e.GetCurrentPoint(LoupeLayer).Position;
        _grab = new Point(p.X - _tip.X, p.Y - _tip.Y);
        _drag = LoupeDrag.Body;
    }

    void Loupe_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_drag != LoupeDrag.Body) return;
        var p = e.GetCurrentPoint(LoupeLayer).Position;
        PlaceLoupe(new Point(p.X - _grab.X, p.Y - _grab.Y), keepSide: true);   // no flip under the hand
    }

    void Loupe_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_drag != LoupeDrag.Body) return;
        _drag = LoupeDrag.None;
        Loupe.ReleasePointerCapture(e.Pointer);
    }

    /// <summary>
    /// The loupe lies above the scroll viewer, not inside it, so it passes the wheel on: Ctrl+wheel zooms around the
    /// pointer, Shift+wheel or a horizontal wheel scrolls sideways, the wheel scrolls up and down.
    /// </summary>
    void Loupe_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        e.Handled = true;
        if (_bitmap is null) return;
        var pt = e.GetCurrentPoint(Scroller);
        int delta = pt.Properties.MouseWheelDelta;
        if (e.KeyModifiers.HasFlag(VirtualKeyModifiers.Control))
        {
            float zoom = Math.Clamp(Scroller.ZoomFactor * (float)Math.Pow(1.1, delta / 120.0), Scroller.MinZoomFactor, Scroller.MaxZoomFactor);
            var at = e.GetCurrentPoint(Surface).Position;   // stays under the pointer
            _fit = false;
            Scroller.ChangeView(at.X * zoom - pt.Position.X, at.Y * zoom - pt.Position.Y, zoom, true);
        }
        else if (pt.Properties.IsHorizontalMouseWheel) Scroller.ChangeView(Scroller.HorizontalOffset + delta, null, null);
        else if (e.KeyModifiers.HasFlag(VirtualKeyModifiers.Shift)) Scroller.ChangeView(Scroller.HorizontalOffset - delta, null, null);
        else Scroller.ChangeView(null, Scroller.VerticalOffset - delta, null);
    }

    void LoupeClose_Click(object sender, RoutedEventArgs e) => HideLoupe();

    void Escape_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (Loupe.Visibility != Visibility.Visible && _pendingAt is null) return;
        HideLoupe();   // also drops a loupe that is about to open
        args.Handled = true;
    }

    /// <summary>
    /// Puts the loupe's sharp corner at <paramref name="tip"/> (view coordinates) and shows the image at 100 % from that
    /// point. The loupe hangs below and to the right of its corner, flipping to the other side near the view's edges;
    /// with <paramref name="keepSide"/> it keeps its side and the corner is held where the loupe stays in view.
    /// </summary>
    void PlaceLoupe(Point tip, bool keepSide)
    {
        if (_bitmap is null || _surfaceWidth <= 0) return;
        double size = Loupe.Width, inner = size - Loupe.BorderThickness.Left - Loupe.BorderThickness.Right;
        double viewW = LoupeLayer.ActualWidth, viewH = LoupeLayer.ActualHeight;
        if (keepSide)
        {
            double minX = _left ? size : 0, maxX = Math.Max(minX, _left ? viewW : viewW - size);
            double minY = _up ? size : 0, maxY = Math.Max(minY, _up ? viewH : viewH - size);
            tip = new Point(Math.Clamp(tip.X, minX, maxX), Math.Clamp(tip.Y, minY, maxY));
        }
        else
        {
            _left = tip.X + size > viewW && tip.X - size >= 0;
            _up = tip.Y + size > viewH && tip.Y - size >= 0;
        }
        // The sharp corner never leaves the visible part of the image: dragged past an edge, the loupe sticks to it.
        var toView = Surface.TransformToVisual(LoupeLayer);
        Point topLeft = toView.TransformPoint(new Point(0, 0)), bottomRight = toView.TransformPoint(new Point(_surfaceWidth, _surfaceHeight));
        double x0 = Math.Max(topLeft.X, 0), x1 = Math.Min(bottomRight.X, viewW);
        double y0 = Math.Max(topLeft.Y, 0), y1 = Math.Min(bottomRight.Y, viewH);
        if (x1 > x0 && y1 > y0) tip = new Point(Math.Clamp(tip.X, x0, x1), Math.Clamp(tip.Y, y0, y1));
        _tip = tip;
        var at = LoupeLayer.TransformToVisual(Surface).TransformPoint(tip);
        double x = Math.Clamp(at.X, 0, _surfaceWidth), y = Math.Clamp(at.Y, 0, _surfaceHeight);
        _at = new Point(x, y);

        Canvas.SetLeft(Loupe, Snap(_left ? tip.X - size : tip.X));
        Canvas.SetTop(Loupe, Snap(_up ? tip.Y - size : tip.Y));
        const double r = 24;
        Loupe.CornerRadius = new CornerRadius(
            !_left && !_up ? 0 : r, _left && !_up ? 0 : r, _left && _up ? 0 : r, !_left && _up ? 0 : r);
        // The close button takes the corner diagonally opposite the sharp one, the label the corner beside it.
        LoupeClose.HorizontalAlignment = _left ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        LoupeClose.VerticalAlignment = _up ? VerticalAlignment.Top : VerticalAlignment.Bottom;
        LoupeLabel.HorizontalAlignment = _left ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        LoupeLabel.VerticalAlignment = _up ? VerticalAlignment.Top : VerticalAlignment.Bottom;

        // The magnified point sits at the loupe's centre, except near the image's edges: there the view stops at the
        // edge, so the loupe never shows the void beyond the image. (LoupeCanvas is clipped to `inner` in the XAML.)
        Canvas.SetLeft(LoupePicture, Snap(inner / 2 - InsideImage(x, _surfaceWidth, inner)));
        Canvas.SetTop(LoupePicture, Snap(inner / 2 - InsideImage(y, _surfaceHeight, inner)));
        Loupe.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// Moves a centre coordinate so a window <paramref name="window"/> wide around it stays within an image
    /// <paramref name="length"/> long (an image smaller than the window is centred).
    /// </summary>
    static double InsideImage(double centre, double length, double window) =>
        length <= window ? length / 2 : Math.Clamp(centre, window / 2, length - window / 2);

    /// <summary>Rounds a DIP offset to a whole physical pixel, so 100 % maps each image pixel onto one screen pixel.</summary>
    double Snap(double dips)
    {
        double scale = XamlRoot?.RasterizationScale ?? 1;
        return Math.Round(dips * scale) / scale;
    }

    void HideLoupe()
    {
        CancelPendingLoupe();
        Loupe.Visibility = Visibility.Collapsed;
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
