using System.Text.Json;
using System.Text.Json.Serialization;
using Orbitale.Engine.Blending;
using Orbitale.Engine.Stacking;

namespace Orbitale.Engine.Settings;

/// <summary>What the app remembers between launches: the last run's settings and the last folders and output format.</summary>
public sealed record AppSettings(
    StackSettings Stack,
    string? LastInputFolder,
    string? LastOutputFolder,
    string LastOutputFormat = "tif");

/// <summary>
/// Loads and saves <see cref="AppSettings"/> as JSON, by default in <c>%LOCALAPPDATA%\Orbitale\settings.json</c>.
/// Loading never throws: a missing, unreadable or corrupt file gives <see cref="Defaults"/>, and out-of-range values
/// are repaired. Saving never throws either: settings are a convenience and must not crash the app.
/// </summary>
public static class SettingsStore
{
    static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: true) },
    };

    public static AppSettings Defaults { get; } = new(new StackSettings(), null, null);

    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Orbitale", "settings.json");

    public static AppSettings Load(string? path = null)
    {
        try
        {
            var json = File.ReadAllText(path ?? DefaultPath);
            var loaded = JsonSerializer.Deserialize<AppSettings>(json, Options);
            return loaded is null ? Defaults : Repair(loaded);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException
                                      or NotSupportedException or ArgumentException)
        {
            return Defaults;
        }
    }

    public static void Save(AppSettings s, string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(s, Options));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Best effort: the next launch falls back to the previous file or the defaults.
        }
    }

    const int MaxNeighbourK = 3, MinPreviewLongSide = 512, MaxPreviewLongSide = 4096;

    static AppSettings Repair(AppSettings s)
    {
        var d = new StackSettings();
        var st = s.Stack ?? d;
        st = st with
        {
            Mode = Enum.IsDefined(st.Mode) ? st.Mode : d.Mode,
            CometStrength = Unit(st.CometStrength, d.CometStrength),
            GapFillStrength = Unit(st.GapFillStrength, d.GapFillStrength),
            TrailSensitivity = Unit(st.TrailSensitivity, d.TrailSensitivity),
            // Non-positive values are nonsense (the default); others are clamped. K > 3 would ask the temporal median for
            // more than its 6 frames.
            NeighbourK = st.NeighbourK >= 1 ? Math.Min(st.NeighbourK, MaxNeighbourK) : d.NeighbourK,
            PreviewMaxLongSide = st.PreviewMaxLongSide > 0 ? Math.Clamp(st.PreviewMaxLongSide, MinPreviewLongSide, MaxPreviewLongSide) : d.PreviewMaxLongSide,
        };
        return s with
        {
            Stack = st,
            LastOutputFormat = string.IsNullOrWhiteSpace(s.LastOutputFormat) ? Defaults.LastOutputFormat : s.LastOutputFormat,
        };
    }

    static double Unit(double v, double fallback) => double.IsFinite(v) ? Math.Clamp(v, 0, 1) : fallback;
}
