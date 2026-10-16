using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Orbitale.App.Services;
using Orbitale.App.ViewModels;
using Orbitale.Engine.Settings;
using Orbitale.Engine.Stacking;
using Windows.Storage.Pickers;
using Windows.System;
using WinRT.Interop;

namespace Orbitale.App;

public sealed partial class MainWindow : Window, IFilePickers
{
    static readonly string[] FrameTypes = [".jpg", ".jpeg", ".tif", ".tiff"];

    public MainWindow()
    {
        InitializeComponent();
        ViewModel = new MainViewModel(this, new StackSession(), SettingsStore.Load());

        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "Orbitale.ico"));
        AppWindow.TitleBar.PreferredTheme = TitleBarTheme.Dark;
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1400, 900));
        Closed += (_, _) => SettingsStore.Save(ViewModel.ToAppSettings());

        // A folder on the command line is added at startup ("Orbitale.exe D:\frames").
        var args = Environment.GetCommandLineArgs();
        if (args.Length > 1 && Directory.Exists(args[1]))
            _ = ViewModel.AddFolderAsync(args[1]);
    }

    public MainViewModel ViewModel { get; }

    nint Hwnd => WindowNative.GetWindowHandle(this);

    // ---- Thumbnail strip ----

    void Strip_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.Item is not FrameItem item) return;
        if (args.InRecycleQueue) ViewModel.CancelThumbnail(item);
        else _ = ViewModel.RequestThumbnailAsync(item);
    }

    void Strip_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Delete) return;
        RemoveSelected();
        e.Handled = true;
    }

    /// <summary>Right-clicking an unselected frame selects it alone, so "Remove from list" acts on what was clicked.</summary>
    void Strip_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is FrameItem item && !Strip.SelectedItems.Contains(item))
        {
            Strip.SelectedItems.Clear();
            Strip.SelectedItem = item;
        }
    }

    void RemoveSelected_Click(object sender, RoutedEventArgs e) => RemoveSelected();

    void RemoveSelected()
    {
        var items = Strip.SelectedItems.OfType<FrameItem>().ToList();
        if (ViewModel.RemoveFramesCommand.CanExecute(items)) ViewModel.RemoveFramesCommand.Execute(items);
    }

    // ---- IFilePickers (unpackaged: every picker needs the window handle) ----

    public async Task<string?> PickFolderAsync()
    {
        var picker = new FolderPicker { SettingsIdentifier = "frames", SuggestedStartLocation = PickerLocationId.PicturesLibrary };
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, Hwnd);
        var folder = await picker.PickSingleFolderAsync();
        return folder?.Path;
    }

    public async Task<IReadOnlyList<string>> PickFramesAsync()
    {
        var picker = new FileOpenPicker
        {
            SettingsIdentifier = "frames",
            SuggestedStartLocation = PickerLocationId.PicturesLibrary,
            ViewMode = PickerViewMode.Thumbnail,
        };
        foreach (var t in FrameTypes) picker.FileTypeFilter.Add(t);
        InitializeWithWindow.Initialize(picker, Hwnd);
        var files = await picker.PickMultipleFilesAsync();
        return files?.Select(f => f.Path).ToList() ?? [];
    }

    public async Task<string?> PickSaveFileAsync(string suggestedName, string format)
    {
        var picker = new FileSavePicker
        {
            SettingsIdentifier = "output",
            SuggestedStartLocation = PickerLocationId.PicturesLibrary,
            SuggestedFileName = Path.GetFileNameWithoutExtension(suggestedName),
        };
        // The first choice is the default type.
        var tiff = ("TIFF 16-bit", (IList<string>)[".tif", ".tiff"]);
        var jpg = ("JPEG", (IList<string>)[".jpg", ".jpeg"]);
        foreach (var (name, exts) in format == "jpg" ? [jpg, tiff] : new[] { tiff, jpg })
            picker.FileTypeChoices.Add(name, exts);
        InitializeWithWindow.Initialize(picker, Hwnd);
        var file = await picker.PickSaveFileAsync();
        return file?.Path;
    }
}
