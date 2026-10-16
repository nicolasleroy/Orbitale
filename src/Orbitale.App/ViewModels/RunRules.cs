using Orbitale.Engine.Blending;
using Orbitale.Engine.Stacking;

namespace Orbitale.App.ViewModels;

// UI-free rules of the main window (unit-tested without WinUI).

/// <summary>When each command is available.</summary>
public static class RunGate
{
    public static bool IsRunning(SessionState s) => s is SessionState.Running or SessionState.Stopping;

    /// <summary>
    /// Start needs frames, waits for a frame list being read, and never runs while the previous result is in use
    /// (saved or rendered off the UI thread): Start disposes it.
    /// </summary>
    public static bool CanStart(SessionState s, bool hasFrames, bool loadingFrames, bool resultBusy) =>
        s is SessionState.Idle or SessionState.Finished or SessionState.Stopped && hasFrames && !loadingFrames && !resultBusy;

    public static bool CanStop(SessionState s) => s == SessionState.Running;

    public static bool CanSave(SessionState s, bool hasResult, bool saving) =>
        s is SessionState.Finished or SessionState.Stopped && hasResult && !saving;

    public static bool CanEditFrames(SessionState s, bool loadingFrames, bool saving) => !IsRunning(s) && !loadingFrames && !saving;

    /// <summary>Clicking a thumbnail shows that original frame, except during a run.</summary>
    public static bool CanViewFrame(SessionState s) => !IsRunning(s);
}

/// <summary>The texts of the status bar and the image badge.</summary>
public static class RunText
{
    public static string Status(StackProgress p)
    {
        var text = $"{p.Processed} / {p.Total} · {Trails(p.TrailsTotal)}";
        return p.Eta is { } eta && eta > TimeSpan.Zero && p.Processed < p.Total ? $"{text} · ~{TimeLeft(eta)} left" : text;
    }

    public static string Finished(int frames, int trails) => $"Done · {Plural(frames, "frame")} · {Trails(trails)}";

    public static string Stopped(int processed, int total) => $"Stopped at {processed} / {total} · result kept";

    public static string LiveBadge(int processed, int total) => $"Live · frame {processed} / {total}";

    public static string ResultBadge(bool completed, int processed, int total) =>
        completed ? $"Result · {Plural(processed, "frame")}" : $"Stopped result · {processed} / {total} frames";

    public static double Percent(int processed, int total) => total <= 0 ? 0 : 100.0 * processed / total;

    /// <summary>"&lt;first frame name&gt;_orbitale.&lt;format&gt;".</summary>
    public static string DefaultSaveName(string? firstFrame, string format) =>
        (firstFrame is null ? "orbitale" : Path.GetFileNameWithoutExtension(firstFrame) + "_orbitale") + "." + format;

    static string Trails(int n) => $"{Plural(n, "trail")} removed";

    static string Plural(int n, string word) => n == 1 ? $"1 {word}" : $"{n} {word}s";

    static string TimeLeft(TimeSpan t)
    {
        if (t < TimeSpan.FromSeconds(59.5)) return $"{Math.Max(1, (int)Math.Ceiling(t.TotalSeconds))} s";
        int minutes = (int)Math.Round(t.TotalMinutes, MidpointRounding.AwayFromZero);
        return minutes < 60 ? $"{minutes} min" : $"{minutes / 60} h {minutes % 60} min";
    }
}

/// <summary>The run's notices: the latest one is shown, with a count when there were several.</summary>
public sealed class NoticeLog
{
    public string? Latest { get; private set; }

    public int Count { get; private set; }

    public bool HasNotice => Count > 0;

    public string Title => Count <= 1 ? "Notice" : $"{Count} notices · showing the latest";

    /// <summary>Records <paramref name="notice"/>; blank or null ones are ignored. Returns whether it was recorded.</summary>
    public bool Add(string? notice)
    {
        if (string.IsNullOrWhiteSpace(notice)) return false;
        Latest = notice;
        Count++;
        return true;
    }

    public void Clear()
    {
        Latest = null;
        Count = 0;
    }
}

/// <summary>The toolbar's settings (sliders in percent) as engine settings.</summary>
public static class StackSettingsMapper
{
    public static StackSettings FromPercent(StackSettings baseline, BlendMode mode, bool comet, double cometStrength, bool headAtStart,
        bool gapFill, double gapFillStrength, bool removeTrails, double trailSensitivity) => baseline with
    {
        Mode = mode,
        Comet = comet,
        CometStrength = cometStrength / 100,
        HeadAtStart = headAtStart,
        GapFill = gapFill,
        GapFillStrength = gapFillStrength / 100,
        RemoveTrails = removeTrails,
        TrailSensitivity = trailSensitivity / 100,
    };

    /// <summary>The settings a run uses: comet and gap filling only exist in Lighten (the strengths are kept).</summary>
    public static StackSettings ForRun(StackSettings s) =>
        s.Mode == BlendMode.Lighten ? s : s with { Comet = false, GapFill = false };
}

/// <summary>
/// Counts the background operations reading the session's result (full-resolution renders), so Start, which
/// disposes the result, stays disabled until every one of them has finished. UI thread only.
/// </summary>
public sealed class ResultReaders
{
    int _count;

    public bool InUse => _count > 0;

    public void Begin() => _count++;

    public void End() => _count = Math.Max(0, _count - 1);
}
