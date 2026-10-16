using System.Diagnostics;
using System.Globalization;
using OpenCvSharp;
using Orbitale.Engine.Blending;
using Orbitale.Engine.Frames;
using Orbitale.Engine.Imaging;
using Orbitale.Engine.Stacking;

// Console harness: runs the stacking engine on a folder of frames and reports speed and memory.
const string Usage =
    "Orbitale.Cli <inputDir> <output.tif|.jpg> [--mode lighten|darken|average] [--comet 0..1] [--head-at-start] " +
    "[--gapfill 0..1] [--no-trails] [--sensitivity 0..1] [--limit N] [--segments out.csv]";
const int PreviewLongSide = 2560;

if (args.Length < 2) { Console.Error.WriteLine(Usage); return 2; }
string inputDir = args[0], output = Path.GetFullPath(args[1]);
var settings = new StackSettings();
int limit = int.MaxValue;
string? segmentsCsv = null;
try
{
    for (int a = 2; a < args.Length; a++)
    {
        string Next() => a + 1 < args.Length ? args[++a] : throw new ArgumentException($"{args[a]} needs a value");
        double Unit() { var v = double.Parse(Next(), CultureInfo.InvariantCulture); return v is >= 0 and <= 1 ? v : throw new ArgumentException($"{args[a - 1]} must be in 0..1"); }
        switch (args[a])
        {
            case "--mode": settings = settings with { Mode = Enum.Parse<BlendMode>(Next(), ignoreCase: true) }; break;
            case "--comet": settings = settings with { Comet = true, CometStrength = Unit() }; break;
            case "--head-at-start": settings = settings with { HeadAtStart = true }; break;
            case "--gapfill": settings = settings with { GapFill = true, GapFillStrength = Unit() }; break;
            case "--no-trails": settings = settings with { RemoveTrails = false }; break;
            case "--sensitivity": settings = settings with { TrailSensitivity = Unit() }; break;
            case "--limit": limit = int.Parse(Next(), CultureInfo.InvariantCulture); break;
            case "--segments": segmentsCsv = Path.GetFullPath(Next()); break;
            default: throw new ArgumentException($"unknown option {args[a]}");
        }
    }
}
catch (Exception e) when (e is ArgumentException or FormatException)
{
    Console.Error.WriteLine(e.Message);
    Console.Error.WriteLine(Usage);
    return 2;
}

string[] extensions = [".jpg", ".jpeg", ".tif", ".tiff"];
var paths = Directory.EnumerateFiles(inputDir)
    .Where(p => extensions.Contains(Path.GetExtension(p).ToLowerInvariant()))
    .ToList();
paths.Sort(FrameSequence.NaturalCompare);
var seq = FrameSequence.Build(paths.Take(limit));
foreach (var s in seq.Skipped) Console.WriteLine($"skipped {Path.GetFileName(s.Path)}: {s.Reason}");
Console.WriteLine($"{seq.Files.Count} frames {seq.Width}x{seq.Height}, {settings}");

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); Console.WriteLine("stopping…"); };

var timings = new List<FrameTimings>();
int trailsTotal = 0;
using var segmentsOut = segmentsCsv is null ? null : new StreamWriter(segmentsCsv);
segmentsOut?.WriteLine("file,x1,y1,x2,y2");   // removed trails in full-resolution px
var engine = new StackEngine { Timings = timings.Add };
var clock = Stopwatch.StartNew();
var last = TimeSpan.Zero;
var progress = new ConsoleProgress(p =>
{
    var now = clock.Elapsed;
    if (p.Notice is { } n) Console.WriteLine($"  notice: {n}");
    Console.WriteLine($"[{p.Processed}/{p.Total}] {Path.GetFileName(seq.Files[p.FileIndex])}: {p.TrailsInFrame} trails, {(now - last).TotalMilliseconds:F0} ms");
    last = now;
    trailsTotal = p.TrailsTotal;
    double toFull = (double)seq.Width / p.Preview.Width;   // preview -> full through pixel centres
    foreach (var g in p.RemovedTrailsPreviewCoords)
        segmentsOut?.WriteLine(string.Join(",", Path.GetFileName(seq.Files[p.FileIndex]),
            ((g.X1 + 0.5) * toFull - 0.5).ToString("F0", CultureInfo.InvariantCulture), ((g.Y1 + 0.5) * toFull - 0.5).ToString("F0", CultureInfo.InvariantCulture),
            ((g.X2 + 0.5) * toFull - 0.5).ToString("F0", CultureInfo.InvariantCulture), ((g.Y2 + 0.5) * toFull - 0.5).ToString("F0", CultureInfo.InvariantCulture)));
});

using var result = await engine.RunAsync(seq, settings, progress, cts.Token);
var runTime = clock.Elapsed;

result.Save(output);
var previewPath = Path.Combine(Path.GetDirectoryName(output)!, Path.GetFileNameWithoutExtension(output) + "_preview.jpg");
using (var small = new Mat())
{
    Cv2.Resize(result.Image, small, Preview.SizeFor(result.Image.Width, result.Image.Height, PreviewLongSide), 0, 0, InterpolationFlags.Area);
    ImageWriter.Save(small, previewPath, result.Metadata);
}
var total = clock.Elapsed;

Console.WriteLine();
Console.WriteLine($"{(result.Completed ? "completed" : "stopped")}: {result.FramesProcessed}/{seq.Files.Count} frames," +
                  $" run {runTime.TotalSeconds:F1} s ({runTime.TotalMilliseconds / Math.Max(1, result.FramesProcessed):F0} ms/frame), " +
                  $"total incl. save {total.TotalSeconds:F1} s, segments/frame: {(double)trailsTotal / Math.Max(1, result.FramesProcessed):F1}");
Console.WriteLine($"alignment failed (trail removal skipped): {result.TrailRemovalSkipped} frames");
if (timings.Count > 0)
{
    void Stage(string name, Func<FrameTimings, double> f)
    {
        var v = timings.Select(f).Order().ToArray();
        Console.WriteLine($"  {name,-11} mean {v.Average(),7:F0} ms   median {v[v.Length / 2],7:F0} ms   max {v[^1],7:F0} ms");
    }
    Stage("decode wait", t => t.DecodeMs);
    Stage("align", t => t.AlignMs);
    Stage("clean", t => t.CleanMs);
    Stage("gap fill", t => t.GapFillMs);
    Stage("accumulate", t => t.AccumulateMs);
    Stage("preview", t => t.PreviewMs);
}
using (var self = Process.GetCurrentProcess())
{
    self.Refresh();
    Console.WriteLine($"peak working set {self.PeakWorkingSet64 / (1024.0 * 1024 * 1024):F2} GB");
}
Console.WriteLine($"wrote {output}");
Console.WriteLine($"wrote {previewPath}");
return 0;

/// <summary>Reports synchronously on the engine thread (Progress&lt;T&gt; would post to the thread pool and reorder lines).</summary>
sealed class ConsoleProgress(Action<StackProgress> handler) : IProgress<StackProgress>
{
    public void Report(StackProgress value) => handler(value);
}
