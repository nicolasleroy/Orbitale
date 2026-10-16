using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Orbitale.App.Services;
using Orbitale.App.ViewModels;
using Orbitale.Engine.Imaging;
using Orbitale.Engine.Settings;
using Orbitale.Engine.Stacking;
using Orbitale.Engine.Trails;
using Microsoft.Windows.Storage.Pickers;
using Windows.System;

namespace Orbitale.App;

public sealed partial class MainWindow : Window, IFilePickers, IImageSurface
{
    static readonly string[] FrameTypes = [".jpg", ".jpeg", ".tif", ".tiff"];

    public MainWindow()
    {
        InitializeComponent();
        ViewModel = new MainViewModel(this, this, new DispatcherQueueUi(DispatcherQueue), new StackSession(), SettingsStore.Load());

        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "Orbitale.ico"));
        AppWindow.TitleBar.PreferredTheme = TitleBarTheme.Dark;
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1400, 900));
        AppWindow.Closing += AppWindow_Closing;
        ViewModel.SaveEnded += () => _ = TryCloseAsync();
        Closed += (_, _) => SettingsStore.Save(ViewModel.ToAppSettings());

        // A folder on the command line is added at startup ("Orbitale.exe D:\frames").
        var args = Environment.GetCommandLineArgs();
        if (args.Length > 1 && Directory.Exists(args[1]))
            _ = ViewModel.AddFolderAsync(args[1]);
    }

    public MainViewModel ViewModel { get; }

    // ---- Closing ----

    bool _closeConfirmed, _closeDialogOpen;

    /// <summary>
    /// A save in flight is finished before the window closes (closing would lose the file and leave its temporary file
    /// behind); a run in progress or a result never saved is confirmed first.
    /// </summary>
    void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_closeConfirmed || ViewModel.CloseAction == CloseAction.Close) return;
        args.Cancel = true;
        _ = TryCloseAsync();
    }

    async Task TryCloseAsync()
    {
        if (_closeDialogOpen) return;
        try
        {
            var action = ViewModel.CloseAction;
            switch (action)
            {
                case CloseAction.Close:
                    break;
                case CloseAction.WaitForSave:
                    ViewModel.CloseWhenSaved();   // SaveEnded calls back here
                    return;
                default:
                    _closeDialogOpen = true;
                    var dialog = new ContentDialog
                    {
                        XamlRoot = Content.XamlRoot,
                        Title = RunText.ClosePrompt(action),
                        PrimaryButtonText = action == CloseAction.ConfirmStopRun ? "Stop and close" : "Close without saving",
                        CloseButtonText = "Cancel",
                        DefaultButton = ContentDialogButton.Close,
                    };
                    var answer = await dialog.ShowAsync();
                    _closeDialogOpen = false;
                    if (answer != ContentDialogResult.Primary) return;
                    // The state may have moved on while the dialog was open (a save started, the run ended): ask again.
                    if (ViewModel.CloseAction is CloseAction.WaitForSave) { ViewModel.CloseWhenSaved(); return; }
                    if (action == CloseAction.ConfirmStopRun) ViewModel.StopForClose();
                    break;
            }
            _closeConfirmed = true;
            Close();
        }
        catch (Exception e)
        {
            _closeDialogOpen = false;
            Diagnostics.Log($"Close failed: {e}");
        }
    }

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

    /// <summary>Clicking a thumbnail shows that original frame (the view model ignores it during a run).</summary>
    void Strip_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is FrameItem item) ViewModel.ViewFrameCommand.Execute(item);
    }

    void RemoveSelected_Click(object sender, RoutedEventArgs e) => RemoveSelected();

    void RemoveSelected()
    {
        var items = Strip.SelectedItems.OfType<FrameItem>().ToList();
        if (ViewModel.RemoveFramesCommand.CanExecute(items)) ViewModel.RemoveFramesCommand.Execute(items);
    }

    // ---- IImageSurface ----

    public void Show(PreviewImage image, double sourceWidth) => LiveView.Show(image, sourceWidth);

    public void Clear() => LiveView.Clear();

    public void FlashTrails(IReadOnlyList<LineSegment> previewCoords) => LiveView.FlashTrails(previewCoords);

    public void BringIntoView(object frame) => Strip.ScrollIntoView(frame);

    // ---- IFilePickers (Windows App SDK pickers: they take the window id and open in the remembered folder) ----

    /// <summary>The remembered folder, when it still exists (a missing one would make the picker fail).</summary>
    static string? Existing(string? folder) => !string.IsNullOrEmpty(folder) && Directory.Exists(folder) ? folder : null;

    public async Task<string?> PickFolderAsync(string? suggestedFolder)
    {
        var picker = new FolderPicker(AppWindow.Id) { SettingsIdentifier = "frames", SuggestedStartLocation = PickerLocationId.PicturesLibrary };
        if (Existing(suggestedFolder) is { } folder) picker.SuggestedFolder = folder;
        var result = await picker.PickSingleFolderAsync();
        return result?.Path;
    }

    public async Task<IReadOnlyList<string>> PickFramesAsync(string? suggestedFolder)
    {
        var picker = new FileOpenPicker(AppWindow.Id)
        {
            SettingsIdentifier = "frames",
            SuggestedStartLocation = PickerLocationId.PicturesLibrary,
            ViewMode = PickerViewMode.Thumbnail,
        };
        if (Existing(suggestedFolder) is { } folder) picker.SuggestedFolder = folder;
        foreach (var t in FrameTypes) picker.FileTypeFilter.Add(t);
        var files = await picker.PickMultipleFilesAsync();
        return files?.Select(f => f.Path).ToList() ?? [];
    }

    public async Task<string?> PickSaveFileAsync(string suggestedName, string format, string? suggestedFolder)
    {
        var picker = new FileSavePicker(AppWindow.Id)
        {
            SettingsIdentifier = "output",
            SuggestedStartLocation = PickerLocationId.PicturesLibrary,
            SuggestedFileName = Path.GetFileNameWithoutExtension(suggestedName),
        };
        if (Existing(suggestedFolder) is { } folder) picker.SuggestedFolder = folder;
        const string tiff = "TIFF 16-bit", jpg = "JPEG";
        picker.FileTypeChoices.Add(tiff, [".tif", ".tiff"]);
        picker.FileTypeChoices.Add(jpg, [".jpg", ".jpeg"]);
        // The last-used format is the default type, by its index in the choices as the picker lists them.
        int initial = picker.FileTypeChoices.Keys.ToList().IndexOf(format == "jpg" ? jpg : tiff);
        if (initial >= 0) picker.InitialFileTypeIndex = initial;
        var result = await picker.PickSaveFileAsync();
        return result?.Path;
    }
}
