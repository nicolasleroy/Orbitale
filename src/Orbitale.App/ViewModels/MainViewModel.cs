using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Media;
using Orbitale.App.Services;
using Orbitale.Engine.Blending;
using Orbitale.Engine.Frames;
using Orbitale.Engine.Imaging;
using Orbitale.Engine.Settings;
using Orbitale.Engine.Stacking;
using LineSegment = Orbitale.Engine.Trails.LineSegment;

namespace Orbitale.App.ViewModels;

/// <summary>
/// The main window's state: the run settings (sliders in percent, mapped to 0..1 in <see cref="StackSettings"/>), the
/// frame list, the live run and the session's commands. All image work stays in the engine and runs off the UI thread;
/// this class only orchestrates it. Every member is used on the UI thread; engine callbacks are marshalled to it.
/// </summary>
public partial class MainViewModel : ObservableObject
{
    static readonly string[] FrameExtensions = [".jpg", ".jpeg", ".tif", ".tiff"];
    const uint ThumbnailSize = 160;

    /// <summary>What every progress report contributes to the UI even when its preview is coalesced away.</summary>
    readonly record struct FrameStep(int FileIndex, int Trails, IReadOnlyList<LineSegment> RemovedTrails, string? Notice);

    readonly IFilePickers _pickers;
    readonly IImageSurface _surface;
    readonly IUiDispatcher _ui;
    readonly ThumbnailQueue<ImageSource> _thumbnails;
    readonly StackSession _session;
    readonly StackSettings _loadedStack;
    readonly NoticeLog _notices = new();
    readonly ResultReaders _resultReaders = new();
    FrameSequence? _sequence;

    // The current or last run.
    FrameSequence? _runSequence;
    bool _runRemovesTrails;
    FrameStateTracker? _tracker;
    CoalescingProgress<StackProgress, FrameStep>? _progress;
    StackProgress? _lastProgress;
    long _stopRequested;

    // What the image view shows: every request bumps the version, so a slow render never replaces a newer view.
    int _viewVersion;
    FrameItem? _requestedFrame;
    bool _loadingFrame;

    // Closing the window: whether the result was saved since its run ended, and a close waiting for the save in flight.
    bool _resultSaved = true;
    bool _closeAfterSave;
    bool _progressFailed;

    public MainViewModel(IFilePickers pickers, IImageSurface surface, IUiDispatcher ui, StackSession session, AppSettings settings)
    {
        _pickers = pickers;
        _surface = surface;
        _ui = ui;
        _thumbnails = new ThumbnailQueue<ImageSource>(
            (path, token) => ThumbnailLoader.LoadAsync(path, ThumbnailSize, token),
            (path, e) => Diagnostics.Log($"Thumbnail failed for {path}: {e.GetType().Name}: {e.Message}"));
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
    [NotifyPropertyChangedFor(nameof(CanEditSettings), nameof(CanUseLightenOptions), nameof(CanEditFrames), nameof(IsBackVisible))]
    [NotifyCanExecuteChangedFor(nameof(AddFramesCommand), nameof(AddFrameFilesCommand), nameof(RemoveFramesCommand))]
    public partial bool IsRunning { get; set; }

    /// <summary>True while a frame list is being validated (headers read off the UI thread).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEditFrames), nameof(IsBusy))]
    [NotifyCanExecuteChangedFor(nameof(AddFramesCommand), nameof(AddFrameFilesCommand), nameof(RemoveFramesCommand), nameof(StartCommand))]
    public partial bool IsLoadingFrames { get; set; }

    /// <summary>True while the result is written to disk (off the UI thread).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEditFrames), nameof(IsBusy))]
    [NotifyCanExecuteChangedFor(nameof(AddFramesCommand), nameof(AddFrameFilesCommand), nameof(RemoveFramesCommand), nameof(StartCommand), nameof(SaveCommand))]
    public partial bool IsSaving { get; set; }

    /// <summary>True while at least one full-resolution render reads the result (off the UI thread); renders can overlap.</summary>
    public bool IsRenderingResult => _resultReaders.InUse;

    public bool CanEditFrames => RunGate.CanEditFrames(_session.State, IsLoadingFrames, IsSaving);

    /// <summary>Something without a percentage is in progress (the status bar's indeterminate bar).</summary>
    public bool IsBusy => IsLoadingFrames || IsSaving || IsRenderingResult;

    [ObservableProperty]
    public partial string StatusText { get; set; }

    [ObservableProperty]
    public partial double ProgressPercent { get; set; }

    [ObservableProperty]
    public partial string Hint { get; set; } = "Add frames to begin";

    /// <summary>The image view's badge: the live count, the result, or the original frame shown.</summary>
    [ObservableProperty]
    public partial string BadgeText { get; set; } = "";

    /// <summary>An original frame is shown instead of the result.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBackVisible))]
    public partial bool IsViewingFrame { get; set; }

    public bool IsBackVisible => IsViewingFrame && !IsRunning && _session.Result is not null;

    /// <summary>The skipped frames of the last Add or Remove, one per line ("file — reason").</summary>
    [ObservableProperty]
    public partial string SkippedMessage { get; set; } = "";

    [ObservableProperty]
    public partial bool ShowSkipped { get; set; }

    [ObservableProperty]
    public partial string SkippedTitle { get; set; } = "";

    /// <summary>The run's latest notice (informational InfoBar), titled with the count when there were several.</summary>
    [ObservableProperty]
    public partial bool ShowNotice { get; set; }

    [ObservableProperty]
    public partial string NoticeTitle { get; set; } = "";

    [ObservableProperty]
    public partial string NoticeText { get; set; } = "";

    /// <summary>The outcome of a save or a failed run (success or error InfoBar).</summary>
    [ObservableProperty]
    public partial bool ShowMessage { get; set; }

    [ObservableProperty]
    public partial bool MessageIsError { get; set; }

    [ObservableProperty]
    public partial string MessageTitle { get; set; } = "";

    [ObservableProperty]
    public partial string MessageText { get; set; } = "";

    // ---- Commands ----

    [RelayCommand(CanExecute = nameof(CanEditFrames))]
    async Task AddFramesAsync()
    {
        var folder = await PickAsync(() => _pickers.PickFolderAsync(LastInputFolder), "Could not open the folder picker");
        if (folder is null) return;
        await AddFolderAsync(folder);
    }

    [RelayCommand(CanExecute = nameof(CanEditFrames))]
    async Task AddFrameFilesAsync()
    {
        var files = await PickAsync(() => _pickers.PickFramesAsync(LastInputFolder), "Could not open the file picker");
        if (files is null || files.Count == 0) return;
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

    bool CanStart() => RunGate.CanStart(_session.State, _sequence is not null, IsLoadingFrames, IsSaving || IsRenderingResult, _loadingFrame);

    /// <summary>
    /// Runs the stack from the first frame. Progress is coalesced (latest preview wins, every frame's state change is
    /// kept) and applied on the UI thread; at the end the full-resolution result replaces the preview.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanStart))]
    async Task StartAsync()
    {
        if (_sequence is null || !CanStart()) return;
        var seq = _sequence;
        _runSequence = seq;
        _viewVersion++;   // a frame or result still rendering must not replace the live view
        _requestedFrame = null;
        IsViewingFrame = false;
        _surface.Clear();   // drop the last result's full-resolution bitmap (~160 MB) before the run
        _tracker = new FrameStateTracker(Frames.ToList());
        _tracker.Reset();
        _lastProgress = null;
        _notices.Clear();
        _progressFailed = false;
        ShowNotice = false;
        ShowMessage = false;
        ProgressPercent = 0;
        StatusText = $"Starting · {seq.Files.Count} frames…";
        BadgeText = RunText.LiveBadge(0, seq.Files.Count);
        Hint = "Stacking…";

        _progress?.Close();
        var progress = new CoalescingProgress<StackProgress, FrameStep>(_ui,
            p => new FrameStep(p.FileIndex, p.TrailsInFrame, p.RemovedTrailsPreviewCoords, p.Notice), ApplyProgress);
        _progress = progress;
        var settings = ToStackSettings();
        _runRemovesTrails = settings.RemoveTrails;
        Diagnostics.Log($"Run started: {seq.Files.Count} frames, {settings}");
        try
        {
            await _session.StartAsync(seq, settings, progress);
        }
        catch (Exception e)
        {
            progress.Close();
            _tracker.Finish();
            Diagnostics.Log($"Run failed: {e}");
            StatusText = "The run failed";
            BadgeText = "";
            Hint = $"Press Start to stack {seq.Files.Count} frames";
            ShowError("The run failed", e.Message);
            return;
        }
        progress.Flush();   // the last frames' states, before the end of the run is shown
        progress.Close();
        await OnRunEndedAsync(seq);
    }

    [RelayCommand(CanExecute = nameof(CanStop))]
    void Stop()
    {
        _stopRequested = Stopwatch.GetTimestamp();
        Diagnostics.Log("Stop requested");
        _session.Stop();
    }

    bool CanStop() => RunGate.CanStop(_session.State);

    bool CanSave() => RunGate.CanSave(_session.State, _session.Result is not null, IsSaving);

    /// <summary>Writes the result (JPG or 16-bit TIFF, by the chosen type) off the UI thread.</summary>
    [RelayCommand(CanExecute = nameof(CanSave))]
    async Task SaveAsync()
    {
        var name = RunText.DefaultSaveName((_runSequence ?? _sequence)?.Files.FirstOrDefault(), LastOutputFormat);
        var path = await PickAsync(() => _pickers.PickSaveFileAsync(name, LastOutputFormat, LastOutputFolder), "Could not open the save dialog");
        // The picker is modal but the state may have changed meanwhile: re-check before touching the result.
        if (path is null || !CanSave() || _session.Result is not { } result) return;
        LastOutputFolder = Path.GetDirectoryName(path);
        var ext = Path.GetExtension(path).ToLowerInvariant();
        LastOutputFormat = ext is ".jpg" or ".jpeg" ? "jpg" : "tif";

        IsSaving = true;   // Start (which disposes the result), Add and Save stay disabled until the write is done
        var status = StatusText;
        StatusText = $"Saving {Path.GetFileName(path)}…";
        ShowMessage = false;
        var clock = Stopwatch.StartNew();
        try
        {
            await Task.Run(() => result.Save(path));
            Diagnostics.Log($"Saved {path} in {clock.ElapsedMilliseconds} ms");
            _resultSaved = true;
            MessageIsError = false;
            MessageTitle = "Saved";
            MessageText = path;
            ShowMessage = true;
        }
        catch (Exception e)
        {
            Diagnostics.Log($"Save failed for {path}: {e}");
            ShowError("Could not save", $"{path}: {e.Message}");
        }
        finally
        {
            StatusText = status;
            IsSaving = false;
            if (_closeAfterSave)
            {
                _closeAfterSave = false;
                SaveEnded?.Invoke();
            }
        }
    }

    // ---- Closing the window ----

    /// <summary>A result exists that was never saved since its run ended.</summary>
    public bool HasUnsavedResult => _session.Result is not null && !_resultSaved;

    /// <summary>What closing the window does now (see <see cref="RunGate.OnClose"/>).</summary>
    public CloseAction CloseAction => RunGate.OnClose(_session.State, IsSaving, HasUnsavedResult);

    /// <summary>Raised on the UI thread when a save that a close waits for has ended (successfully or not).</summary>
    public event Action? SaveEnded;

    /// <summary>The window waits for the save in flight before closing: say so, and raise <see cref="SaveEnded"/> when it ends.</summary>
    public void CloseWhenSaved()
    {
        _closeAfterSave = true;
        StatusText = RunText.FinishingSave;
    }

    /// <summary>Stops the run, if any, before the window closes.</summary>
    public void StopForClose()
    {
        if (CanStop()) Stop();
    }

    /// <summary>Runs a file dialog; a failure to open it is logged and shown, never thrown. Null when it failed.</summary>
    async Task<T?> PickAsync<T>(Func<Task<T>> pick, string title)
    {
        try
        {
            return await pick();
        }
        catch (Exception e)
        {
            Diagnostics.Log($"{title}: {e}");
            ShowError(title, e.Message);
            return default;
        }
    }

    /// <summary>Shows the original frame (full resolution, decoded off the UI thread); ignored during a run.</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    async Task ViewFrameAsync(FrameItem? item)
    {
        if (item is null || !RunGate.CanViewFrame(_session.State)) return;
        _requestedFrame = item;
        _viewVersion++;
        BadgeText = $"{item.FileName} · loading…";
        if (_loadingFrame) return;   // the running load picks up the latest request when it ends

        // One decode at a time (a 40 MP frame is ~0.5 GB as float): clicks while loading only keep the latest frame.
        // Start waits for it, for the same reason.
        _loadingFrame = true;
        StartCommand.NotifyCanExecuteChanged();
        try
        {
            while (_requestedFrame is { } next)
            {
                _requestedFrame = null;
                int version = _viewVersion;
                try
                {
                    var image = await Task.Run(() =>
                    {
                        using var frame = ImageReader.Read(next.Path);
                        return Preview.Render(frame, 0);
                    });
                    if (version != _viewVersion) continue;
                    _surface.Show(image, image.Width);
                    BadgeText = next.FileName;
                    IsViewingFrame = true;
                }
                catch (Exception e)
                {
                    Diagnostics.Log($"Cannot show {next.Path}: {e}");
                    if (version == _viewVersion)
                    {
                        BadgeText = "";
                        ShowError("Cannot show the frame", $"{next.FileName}: {e.Message}");
                    }
                }
            }
        }
        finally
        {
            _loadingFrame = false;
            StartCommand.NotifyCanExecuteChanged();
        }
    }

    [RelayCommand]
    Task BackToResultAsync() => ShowResultAsync();

    // ---- Thumbnails ----

    /// <summary>Loads the item's shell thumbnail; called by the strip when the item is realised (UI thread). Never throws.</summary>
    public Task RequestThumbnailAsync(FrameItem item) => _thumbnails.RequestAsync(item);

    /// <summary>Cancels the item's pending load; called when its container is recycled.</summary>
    public void CancelThumbnail(FrameItem item) => _thumbnails.Cancel(item);

    // ---- Settings snapshot ----

    /// <summary>The settings a run uses (comet and gap filling off outside Lighten).</summary>
    public StackSettings ToStackSettings() => StackSettingsMapper.ForRun(ToolbarSettings());

    /// <summary>The settings as the toolbar shows them (remembered as is, so switching modes loses nothing).</summary>
    public AppSettings ToAppSettings() => new(ToolbarSettings(), LastInputFolder, LastOutputFolder, LastOutputFormat);

    StackSettings ToolbarSettings() => StackSettingsMapper.FromPercent(_loadedStack, Mode, Comet, CometStrength, HeadAtStart,
        GapFill, GapFillStrength, RemoveTrails, TrailSensitivity);

    // ---- Internals ----

    /// <summary>
    /// Applies coalesced progress on the UI thread: every frame's state, then the latest preview. It runs from the
    /// dispatcher queue, where an exception would end the app mid-run: a failure is logged and shown once per run instead.
    /// </summary>
    void ApplyProgress(IReadOnlyList<FrameStep> steps, StackProgress latest)
    {
        try
        {
            ApplyProgressCore(steps, latest);
        }
        catch (Exception e)
        {
            if (_progressFailed) return;
            _progressFailed = true;
            Diagnostics.Log($"Progress display failed: {e}");
            ShowError("The live view failed", $"{e.Message} The run goes on; the result is shown when it ends.");
        }
    }

    void ApplyProgressCore(IReadOnlyList<FrameStep> steps, StackProgress latest)
    {
        if (_tracker is null || _runSequence is null) return;
        IFrameStatus? current = null;
        foreach (var step in steps)
        {
            current = _tracker.Advance(step.FileIndex, step.Trails) ?? current;
            if (_notices.Add(step.Notice))
            {
                NoticeTitle = _notices.Title;
                NoticeText = _notices.Latest!;
                ShowNotice = true;
            }
        }
        if (_tracker.Current is { } now) _surface.BringIntoView(now);
        else if (current is not null) _surface.BringIntoView(current);

        _lastProgress = latest;
        StatusText = RunText.Status(latest);
        ProgressPercent = RunText.Percent(latest.Processed, latest.Total);
        BadgeText = RunText.LiveBadge(latest.Processed, latest.Total);
        _surface.Show(latest.Preview, _runSequence.Width);
        // Every coalesced frame's trails share the preview's coordinates, so none is lost.
        var trails = steps.Count == 1 ? steps[0].RemovedTrails : steps.SelectMany(s => s.RemovedTrails).ToList();
        if (trails.Count > 0) _surface.FlashTrails(trails);
    }

    async Task OnRunEndedAsync(FrameSequence seq)
    {
        _tracker?.Finish();
        if (_session.Result is not { } result) return;
        if (_stopRequested != 0)
        {
            Diagnostics.Log($"Run stopped {Stopwatch.GetElapsedTime(_stopRequested).TotalMilliseconds:F0} ms after Stop, at {result.FramesProcessed} / {seq.Files.Count}");
            _stopRequested = 0;
        }
        else Diagnostics.Log($"Run finished: {result.FramesProcessed} frames");
        _resultSaved = false;
        if (result.TrailRemovalSkipped > 0) Diagnostics.Log($"Trail removal skipped on {result.TrailRemovalSkipped} frames: sky alignment failed");
        int trails = _lastProgress?.TrailsTotal ?? 0;
        StatusText = result.Completed
            ? RunText.Finished(result.FramesProcessed, trails, result.TrailRemovalSkipped,
                framesWithoutTrails: Frames.Count(f => f.State == FrameState.Done), trailRemovalOn: _runRemovesTrails)
            : RunText.Stopped(result.FramesProcessed, seq.Files.Count, result.TrailRemovalSkipped);
        ProgressPercent = RunText.Percent(result.FramesProcessed, seq.Files.Count);
        await ShowResultAsync();
    }

    /// <summary>Shows the result at full resolution (rendered off the UI thread; Start waits for it, as it disposes the result).</summary>
    async Task ShowResultAsync()
    {
        if (_session.Result is not { } result || IsRunning || _runSequence is not { } seq) return;
        int version = ++_viewVersion;
        _requestedFrame = null;
        var badge = RunText.ResultBadge(result.Completed, result.FramesProcessed, seq.Files.Count);
        BadgeText = $"{badge} · rendering full resolution…";
        BeginResultRead();
        var clock = Stopwatch.StartNew();
        try
        {
            var image = await Task.Run(result.RenderFull);
            if (version != _viewVersion) return;
            long rendered = clock.ElapsedMilliseconds;
            _surface.Show(image, image.Width);
            Diagnostics.Log($"Full-resolution view {image.Width} x {image.Height}: rendered in {rendered} ms, shown in {clock.ElapsedMilliseconds - rendered} ms");
            BadgeText = badge;
            IsViewingFrame = false;
        }
        catch (Exception e)
        {
            Diagnostics.Log($"Full-resolution view failed: {e}");
            if (version == _viewVersion) BadgeText = badge;   // the preview stays
        }
        finally
        {
            EndResultRead();
        }
    }

    void BeginResultRead()
    {
        _resultReaders.Begin();
        OnResultReadersChanged();
    }

    /// <summary>Called by every render that began, superseded or not: Start waits for the last one.</summary>
    void EndResultRead()
    {
        _resultReaders.End();
        OnResultReadersChanged();
    }

    void OnResultReadersChanged()
    {
        OnPropertyChanged(nameof(IsRenderingResult));
        OnPropertyChanged(nameof(IsBusy));
        StartCommand.NotifyCanExecuteChanged();
    }

    void ShowError(string title, string message)
    {
        MessageIsError = true;
        MessageTitle = title;
        MessageText = message;
        ShowMessage = true;
    }

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
            {
                if (existing.Remove(path, out var item))
                {
                    // A new list is a new sequence: the last run's states no longer apply.
                    if (item.State != FrameState.Skipped) item.State = FrameState.Pending;
                    item.TrailCount = 0;
                    Frames.Add(item);
                }
                else Frames.Add(new FrameItem(path));
            }
            foreach (var removed in existing.Values) _thumbnails.Cancel(removed);

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

    /// <summary>The session raises this on whichever thread changed the state: refresh on the UI thread.</summary>
    void OnSessionStateChanged()
    {
        if (_ui.HasThreadAccess) RefreshSessionState();
        else _ui.TryEnqueue(RefreshSessionState);
    }

    void RefreshSessionState()
    {
        IsRunning = RunGate.IsRunning(_session.State);
        OnPropertyChanged(nameof(CanEditFrames));
        OnPropertyChanged(nameof(IsBackVisible));
        StartCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
        SaveCommand.NotifyCanExecuteChanged();
        AddFramesCommand.NotifyCanExecuteChanged();
        AddFrameFilesCommand.NotifyCanExecuteChanged();
        RemoveFramesCommand.NotifyCanExecuteChanged();
    }
}
