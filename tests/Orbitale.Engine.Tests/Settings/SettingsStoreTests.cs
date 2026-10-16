using Orbitale.Engine.Blending;
using Orbitale.Engine.Settings;
using Orbitale.Engine.Stacking;

namespace Orbitale.Engine.Tests.Settings;

public sealed class SettingsStoreTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "orbitale-settings-" + Guid.NewGuid().ToString("N"));

    string FilePath => Path.Combine(_dir, "sub", "settings.json");

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Round_trips_every_field()
    {
        var stack = new StackSettings(
            Mode: BlendMode.Darken,
            Comet: true,
            CometStrength: 0.25,
            HeadAtStart: true,
            GapFill: true,
            GapFillStrength: 0.75,
            RemoveTrails: false,
            TrailSensitivity: 0.9,
            NeighbourK: 3,
            PreviewMaxLongSide: 1024);
        var saved = new AppSettings(stack, @"D:\in", @"D:\out", "jpg");

        SettingsStore.Save(saved, FilePath);
        var loaded = SettingsStore.Load(FilePath);

        Assert.Equal(saved, loaded);
    }

    [Fact]
    public void Missing_file_gives_defaults()
    {
        var loaded = SettingsStore.Load(FilePath);

        Assert.Equal(SettingsStore.Defaults, loaded);
        Assert.Equal(new StackSettings(), loaded.Stack);
        Assert.Null(loaded.LastInputFolder);
        Assert.Null(loaded.LastOutputFolder);
        Assert.Equal("tif", loaded.LastOutputFormat);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("[1, 2, 3]")]
    [InlineData("{\"Stack\": {\"Mode\": \"Purple\"}}")]
    public void Corrupt_file_gives_defaults(string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, content);

        var loaded = SettingsStore.Load(FilePath);

        Assert.Equal(SettingsStore.Defaults, loaded);
    }

    [Fact]
    public void Partial_file_fills_missing_fields_with_defaults()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, "{\"LastInputFolder\": \"D:\\\\stars\"}");

        var loaded = SettingsStore.Load(FilePath);

        Assert.Equal(new StackSettings(), loaded.Stack);
        Assert.Equal(@"D:\stars", loaded.LastInputFolder);
        Assert.Equal("tif", loaded.LastOutputFormat);
    }

    [Fact]
    public void Null_and_out_of_range_values_are_repaired()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath,
            "{\"Stack\": {\"Mode\": 7, \"CometStrength\": 4.2, \"GapFillStrength\": -1, \"TrailSensitivity\": 0.3, " +
            "\"NeighbourK\": 0, \"PreviewMaxLongSide\": -5}, \"LastOutputFormat\": null}");

        var loaded = SettingsStore.Load(FilePath);

        var defaults = new StackSettings();
        Assert.Equal(defaults.Mode, loaded.Stack.Mode);
        Assert.Equal(1.0, loaded.Stack.CometStrength);
        Assert.Equal(0.0, loaded.Stack.GapFillStrength);
        Assert.Equal(0.3, loaded.Stack.TrailSensitivity);
        Assert.Equal(defaults.NeighbourK, loaded.Stack.NeighbourK);
        Assert.Equal(defaults.PreviewMaxLongSide, loaded.Stack.PreviewMaxLongSide);
        Assert.Equal("tif", loaded.LastOutputFormat);
    }

    [Fact]
    public void Null_stack_gives_default_stack()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, "{\"Stack\": null, \"LastOutputFolder\": \"E:\\\\x\"}");

        var loaded = SettingsStore.Load(FilePath);

        Assert.Equal(new StackSettings(), loaded.Stack);
        Assert.Equal(@"E:\x", loaded.LastOutputFolder);
    }

    [Fact]
    public void Save_to_an_unwritable_path_does_not_throw()
    {
        // A directory where the file should be: the write fails, the app must keep going.
        Directory.CreateDirectory(FilePath);

        SettingsStore.Save(SettingsStore.Defaults, FilePath);
    }

    [Fact]
    public void Default_path_is_under_local_app_data()
    {
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Orbitale", "settings.json");

        Assert.Equal(expected, SettingsStore.DefaultPath);
    }
}
