using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Orbitale.App.Services;
using Orbitale.Engine.Blending;
using Orbitale.Engine.Frames;
using Orbitale.Engine.Settings;
using Orbitale.Engine.Stacking;

namespace Orbitale.App.ViewModels;

/// <summary>
/// The main window's state: the run settings (sliders in percent, mapped to 0..1 in <see cref="StackSettings"/>), the
/// frame list and the session's commands. All image work stays in the engine; this class only orchestrates it.
/// </summary>
public partial class MainViewModel : ObservableObject
{
    static readonly string[] FrameExtensions = [".jpg", ".jpeg", ".tif", ".tiff"];
    const uint ThumbnailSize = 160;

    readonly IFilePickers _pickers;
    readonly ThumbnailLoader _thumbnails;
    readonly StackSession _session;
    readonly StackSettings _loadedStack;
    FrameSequence? _sequence;

    public MainViewModel(IFilePickers pickers, ThumbnailLoader thumbnails, StackSession session, AppSettings settings)
    {
        _pickers = pickers;
        _thumbnails = thumbnails;
        _session = session;
        _session.StateChanged += OnSessionStateChanged;

        var s = settings.Stack;
        _loadedStack = s;
        ModeIndex = (int)s.Mode;
        Comet = s.Comet;
        CometStrength = s.CometStrength * 100;
        HeadAtStart = s.HeadAtStart;
        GapFill = s.GapFill;
        GapFillStrength = s.GapFillStrength * 100;
        RemoveTrails = s.RemoveTrails;
        TrailSensitivity = s.TrailSensitivity * 100;
        LastInputFolder = settings.LastInputFolder;
        LastOutputFolder = settings.LastOutputFolder;
        LastOutputFormat = settings.LastOutputFormat;
        StatusText = "Add frames to begin";
    }

    // ---- Settings (mirroring StackSettings) ----

    /// <summary>Index into Lighten / Darken / Average (the <see cref="BlendMode"/> order).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLighten), nameof(CanUseLightenOptions))]
    public partial int ModeIndex { get; set; }

    [ObservableProperty]
    public partial bool Comet { get; set; }

    /// <summary>0–100 %.</summary>
    [ObservableProperty]
    public partial double CometStrength { get; set; }

    [ObservableProperty]
    public partial bool HeadAtStart { get; set; }

    [ObservableProperty]
    public partial bool GapFill { get; set; }

    /// <summary>0–100 %.</summary>
    [ObservableProperty]
    public partial double GapFillStrength { get; set; }

    [ObservableProperty]
    public partial bool RemoveTrails { get; set; }

    /// <summary>0–100 %.</summary>
    [ObservableProperty]
    public partial double TrailSensitivity { get; set; }

    public BlendMode Mode => Enum.IsDefined((BlendMode)ModeIndex) ? (BlendMode)ModeIndex : BlendMode.Lighten;

    public bool IsLighten => Mode == BlendMode.Lighten;

    /// <summary>Comet and gap filling only apply to Lighten, and settings are frozen during a run.</summary>
    public bool CanUseLightenOptions => IsLighten && !IsRunning;

    public bool CanEditSettings => !IsRunning;

    public string? LastInputFolder { get; private set; }

    public string? LastOutputFolder { get; private set; }

    public string LastOutputFormat { get; private set; }

    // ---- Frames and status ----

    public ObservableCollection<FrameItem> Frames { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEditSettings), nameof(CanUseLightenOptions), nameof(CanEditFrames))]
    [NotifyCanExecuteChangedFor(nameof(AddFramesCommand), nameof(AddFrameFilesCommand), nameof(RemoveFramesCommand))]
    public partial bool IsRunning { get; set; }

    /// <summary>True while a frame list is being validated (headers read off the UI thread).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEditFrames))]
    [NotifyCanExecuteChangedFor(nameof(AddFramesCommand), nameof(AddFrameFilesCommand), nameof(RemoveFramesCommand), nameof(StartCommand))]
    public partial bool IsLoadingFrames { get; set; }

    public bool CanEditFrames => !IsRunning && !IsLoadingFrames;

    [ObservableProperty]
    public partial string StatusText { get; set; }

    [ObservableProperty]
    public partial double ProgressPercent { get; set; }

    [ObservableProperty]
    public partial string Hint { get; set; } = "Add frames to begin";

    /// <summary>The skipped frames of the last Add or Remove, one per line ("file — reason").</summary>
    [ObservableProperty]
    public partial string SkippedMessage { get; set; } = "";

    [ObservableProperty]
    public partial bool ShowSkipped { get; set; }

    [ObservableProperty]
    public partial string SkippedTitle { get; set; } = "";

    // ---- Commands ----

    [RelayCommand(CanExecute = nameof(CanEditFrames))]
    async Task AddFramesAsync()
    {
        var folder = await _pickers.PickFolderAsync();
        if (folder is null) return;
        await AddFolderAsync(folder);
    }

    [RelayCommand(CanExecute = nameof(CanEditFrames))]
    async Task AddFrameFilesAsync()
    {
        var files = await _pickers.PickFramesAsync();
        if (files.Count == 0) return;
        LastInputFolder = Path.GetDirectoryName(files[0]);
        await RebuildAsync(Frames.Select(f => f.Path).Concat(files));
    }

    /// <summary>Adds every JPG/TIFF directly inside <paramref name="folder"/> (also used for a folder given on the command line).</summary>
    public async Task AddFolderAsync(string folder)
    {
        if (!CanEditFrames) return;
        List<string> files;
        try
        {
            files = Directory.EnumerateFiles(folder)
                .Where(p => FrameExtensions.Contains(Path.GetExtension(p), StringComparer.OrdinalIgnoreCase))
                .ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            StatusText = $"Cannot read {folder}: {e.Message}";
            return;
        }
        LastInputFolder = folder;
        if (files.Count == 0)
        {
            StatusText = $"No JPG or TIFF frames in {folder}";
            return;
        }
        await RebuildAsync(Frames.Select(f => f.Path).Concat(files));
    }

    [RelayCommand(CanExecute = nameof(CanEditFrames))]
    async Task RemoveFramesAsync(IList<FrameItem>? items)
    {
        if (items is null || items.Count == 0) return;
        var removed = items.Select(i => i.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        await RebuildAsync(Frames.Select(f => f.Path).Where(p => !removed.Contains(p)));
    }

    bool CanStart() => _session.CanStart && _sequence is not null && !IsLoadingFrames;

    /// <summary>Runs the stack. The live run (progress, preview, frame states) is wired together with the live image view.</summary>
    [RelayCommand(CanExecute = nameof(CanStart))]
    Task StartAsync() => Task.CompletedTask;

    bool CanStop() => _session.CanStop;

    [RelayCommand(CanExecute = nameof(CanStop))]
    void Stop() => _session.Stop();

    bool CanSave() => _session.CanSave;

    [RelayCommand(CanExecute = nameof(CanSave))]
    async Task SaveAsync()
    {
        var first = _sequence?.Files.FirstOrDefault();
        var name = (first is null ? "orbitale" : Path.GetFileNameWithoutExtension(first) + "_orbitale") + "." + LastOutputFormat;
        var path = await _pickers.PickSaveFileAsync(name, LastOutputFormat);
        if (path is null) return;
        LastOutputFolder = Path.GetDirectoryName(path);
        var ext = Path.GetExtension(path).ToLowerInvariant();
        LastOutputFormat = ext is ".jpg" or ".jpeg" ? "jpg" : "tif";
        // Writing the result is wired with the live run.
    }

    // ---- Thumbnails ----

    /// <summary>Loads the item's shell thumbnail once; called by the strip when the item is realised (UI thread).</summary>
    public async void EnsureThumbnail(FrameItem item)
    {
        if (item.ThumbnailRequested) return;
        item.ThumbnailRequested = true;
        item.Thumbnail = await _thumbnails.LoadAsync(item.Path, ThumbnailSize);
    }

    // ---- Settings snapshot ----

    public StackSettings ToStackSettings() => _loadedStack with
    {
        Mode = Mode,
        Comet = Comet,
        CometStrength = CometStrength / 100,
        HeadAtStart = HeadAtStart,
        GapFill = GapFill,
        GapFillStrength = GapFillStrength / 100,
        RemoveTrails = RemoveTrails,
        TrailSensitivity = TrailSensitivity / 100,
    };

    public AppSettings ToAppSettings() => new(ToStackSettings(), LastInputFolder, LastOutputFolder, LastOutputFormat);

    // ---- Internals ----

    /// <summary>Validates <paramref name="paths"/> with <see cref="FrameSequence.Build"/> off the UI thread and refreshes the strip.</summary>
    async Task RebuildAsync(IEnumerable<string> paths)
    {
        var distinct = paths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        IsLoadingFrames = true;
        StatusText = $"Reading {distinct.Count} frames…";
        try
        {
            FrameSequence? seq = null;
            string? error = null;
            if (distinct.Count > 0)
            {
                try { seq = await Task.Run(() => FrameSequence.Build(distinct)); }
                catch (InvalidOperationException e) { error = e.Message; }
            }

            _sequence = seq;
            var existing = Frames.ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);
            Frames.Clear();
            foreach (var path in seq?.Files ?? [])
                Frames.Add(existing.TryGetValue(path, out var item) ? item : new FrameItem(path));

            var skipped = seq?.Skipped ?? [];
            SkippedTitle = skipped.Count == 1 ? "1 file was skipped" : $"{skipped.Count} files were skipped";
            SkippedMessage = string.Join(Environment.NewLine, skipped.Select(s => $"{Path.GetFileName(s.Path)} — {s.Reason}"));
            ShowSkipped = skipped.Count > 0;

            if (seq is null)
            {
                StatusText = error is null ? "Add frames to begin" : $"{error}.";
                Hint = "Add frames to begin";
            }
            else
            {
                StatusText = $"{seq.Files.Count} frames · {seq.Width} × {seq.Height}";
                Hint = $"Press Start to stack {seq.Files.Count} frames";
            }
        }
        finally
        {
            IsLoadingFrames = false;
        }
    }

    void OnSessionStateChanged()
    {
        IsRunning = _session.State is SessionState.Running or SessionState.Stopping;
        StartCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
        SaveCommand.NotifyCanExecuteChanged();
    }
}
