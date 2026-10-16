using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Orbitale.App.ViewModels;
using Windows.UI;

namespace Orbitale.App.Controls;

/// <summary>Small view-only functions used from x:Bind.</summary>
public static class ViewFormat
{
    static readonly SolidColorBrush Pending = new(Color.FromArgb(0xFF, 0x5A, 0x60, 0x70));
    static readonly SolidColorBrush Done = new(Color.FromArgb(0xFF, 0x4C, 0xB8, 0x6A));
    static readonly SolidColorBrush Trails = new(Color.FromArgb(0xFF, 0xF0, 0xA8, 0x30));
    static readonly SolidColorBrush Skipped = new(Color.FromArgb(0xFF, 0xE0, 0x4F, 0x4F));

    public static Brush StateBrush(FrameState state) => state switch
    {
        FrameState.Current => (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"],
        FrameState.Done => Done,
        FrameState.DoneWithTrails => Trails,
        FrameState.Skipped => Skipped,
        _ => Pending,
    };

    public static Visibility TrailBadge(FrameState state) =>
        state == FrameState.DoneWithTrails ? Visibility.Visible : Visibility.Collapsed;

    public static string FrameTip(string fileName, FrameState state, string? skipReason) =>
        state == FrameState.Skipped && skipReason is not null ? $"{fileName}: {skipReason}" : fileName;

    public static string Percent(string label, double value) => $"{label}: {Math.Round(value)} %";
}
