using System.Text.Json;
using System.Text.Json.Nodes;
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
            var node = JsonNode.Parse(File.ReadAllText(path ?? DefaultPath));   // parsed once, also read by MigrateComet
            var loaded = node.Deserialize<AppSettings>(Options);
            return loaded is null ? Defaults : Repair(MigrateComet(loaded, node));
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
        var (fadeIn, fadeOut) = CometFade.Normalize(st.CometFadeIn, st.CometFadeOut);
        st = st with
        {
            Mode = Enum.IsDefined(st.Mode) ? st.Mode : d.Mode,
            CometFadeIn = fadeIn,
            CometFadeOut = fadeOut,
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

    /// <summary>
    /// Settings saved before the comet fade: a strength (a tail that decayed to 1 % over the last 10–100 % of the
    /// sequence) and a head at the end or the start. A fade cannot reproduce a short tail that is dark before it starts,
    /// so they become the classic comet with the same head: (1, 1), or (0, 0) with the head at the start; the strength is
    /// dropped. Files that already have a fade, or no comet settings at all, are left as they are.
    /// </summary>
    static AppSettings MigrateComet(AppSettings s, JsonNode? json)
    {
        if (s.Stack is null || json?["Stack"] is not JsonObject stack) return s;
        if (stack.ContainsKey(nameof(StackSettings.CometFadeIn)) || stack.ContainsKey(nameof(StackSettings.CometFadeOut))) return s;
        bool headAtStart = stack["HeadAtStart"] is JsonValue hv && hv.TryGetValue(out bool h) && h;
        if (!stack.ContainsKey("CometStrength") && !headAtStart) return s;
        double head = headAtStart ? 0 : 1;
        return s with { Stack = s.Stack with { CometFadeIn = head, CometFadeOut = head } };
    }

    static double Unit(double v, double fallback) => double.IsFinite(v) ? Math.Clamp(v, 0, 1) : fallback;
}
