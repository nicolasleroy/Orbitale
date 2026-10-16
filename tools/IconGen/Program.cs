using ImageMagick;

// Renders assets/orbitale.svg into the app's icon files.
// Usage: dotnet run --project tools/IconGen [-- <repo root>]
var root = args.Length > 0 ? args[0] : FindRepoRoot(AppContext.BaseDirectory);
var svg = Path.Combine(root, "assets", "orbitale.svg");
var appAssets = Path.Combine(root, "src", "Orbitale.App", "Assets");
var previewDir = Path.Combine(root, "out", "icon");
Directory.CreateDirectory(appAssets);
Directory.CreateDirectory(previewDir);

int[] icoSizes = [16, 20, 24, 32, 48, 64, 256];
using var ico = new MagickImageCollection();
foreach (var size in icoSizes)
{
    var img = Render(svg, size);
    img.Write(Path.Combine(previewDir, $"orbitale-{size}.png"), MagickFormat.Png32);
    ico.Add(img);
}
ico.Write(Path.Combine(appAssets, "Orbitale.ico"), MagickFormat.Ico);

foreach (var (name, size) in new[] { ("Square44x44Logo.png", 44), ("Square150x150Logo.png", 150), ("StoreLogo.png", 50) })
{
    using var img = Render(svg, size);
    img.Write(Path.Combine(appAssets, name), MagickFormat.Png32);
}

Console.WriteLine($"Icon written to {appAssets}; previews in {previewDir}");

static MagickImage Render(string svg, int size)
{
    // Rasterise large, then downscale: crisper small sizes than rendering the 64-unit viewBox directly.
    var settings = new MagickReadSettings { Density = new Density(384), BackgroundColor = MagickColors.None };
    var img = new MagickImage(svg, settings);
    img.FilterType = FilterType.Lanczos;
    img.Resize(new MagickGeometry((uint)size, (uint)size) { IgnoreAspectRatio = true });
    img.Format = MagickFormat.Png32;
    return img;
}

static string FindRepoRoot(string start)
{
    for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
        if (File.Exists(Path.Combine(dir.FullName, "Orbitale.sln"))) return dir.FullName;
    throw new DirectoryNotFoundException("Orbitale.sln not found above " + start);
}
