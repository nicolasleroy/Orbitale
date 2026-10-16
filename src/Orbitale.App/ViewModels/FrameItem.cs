using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Media;
using Orbitale.App.Services;

namespace Orbitale.App.ViewModels;

public enum FrameState { Pending, Current, Done, DoneWithTrails, Skipped }

/// <summary>One frame of the sequence as shown in the thumbnail strip.</summary>
public partial class FrameItem : ObservableObject, IThumbnailSlot<ImageSource>
{
    public FrameItem(string path)
    {
        Path = path;
        FileName = System.IO.Path.GetFileName(path);
    }

    public string Path { get; }

    public string FileName { get; }

    [ObservableProperty]
    public partial FrameState State { get; set; }

    [ObservableProperty]
    public partial int TrailCount { get; set; }

    /// <summary>Why the frame was skipped (tooltip), when <see cref="State"/> is <see cref="FrameState.Skipped"/>.</summary>
    [ObservableProperty]
    public partial string? SkipReason { get; set; }

    /// <summary>Shell thumbnail, loaded lazily when the item scrolls into view.</summary>
    [ObservableProperty]
    public partial ImageSource? Thumbnail { get; set; }

    /// <summary>The list item's accessible name.</summary>
    public override string ToString() => FileName;
}
