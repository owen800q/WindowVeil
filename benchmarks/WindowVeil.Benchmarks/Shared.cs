using System.Diagnostics;
using WindowVeil.Geometry;
using WindowVeil.Rendering;

namespace WindowVeil.Benchmarks;

/// <summary>Helpers shared by the benchmarks and the resource probe.</summary>
internal static class Shared
{
    /// <summary>The overlay counts required by the performance plan.</summary>
    public static readonly int[] Counts = [1, 10, 25, 50, 100];

    /// <summary>A grid of small overlay rectangles on the primary monitor.</summary>
    public static PixelRect Cell(int i, int offset = 0) => new(100 + (i % 10) * 60 + offset, 100 + (i / 10) * 40, 50, 24);

    /// <summary>A visible frame not tied to a target window (z-order falls back to HWND_TOP).</summary>
    public static OverlayFrame Frame(PixelRect r) => new()
    {
        Visible = true, Bounds = r, Regions = [r], TargetRects = [r],
        TargetWindowBounds = new PixelRect(0, 0, 1920, 1080), MonitorBounds = new PixelRect(0, 0, 1920, 1080),
    };

    /// <summary>Repository root (walks up to WindowVeil.sln); BenchmarkDotNet runs benchmarks from a generated folder below it.</summary>
    public static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "WindowVeil.sln"))) return dir.FullName;
        for (var dir = new DirectoryInfo(Environment.CurrentDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "WindowVeil.sln"))) return dir.FullName;
        throw new InvalidOperationException("Run the benchmarks from inside the WindowVeil repository.");
    }

    /// <summary>Path of the built demo target executable.</summary>
    public static string DemoTargetExe()
    {
        if (Environment.GetEnvironmentVariable("WINDOWVEIL_DEMOTARGET") is { } env && File.Exists(env)) return env;
        var bin = Path.Combine(RepoRoot(), "samples", "WindowVeil.DemoTarget", "bin");
        var exe = Directory.Exists(bin)
            ? Directory.GetFiles(bin, "WindowVeil.DemoTarget.exe", SearchOption.AllDirectories).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault()
            : null;
        return exe ?? throw new FileNotFoundException("Build samples/WindowVeil.DemoTarget first (dotnet build -c Release).");
    }

    /// <summary>Starts the demo target and waits for its main window.</summary>
    public static (Process Process, nint Window) StartDemoTarget(string title = "WindowVeil Bench Target")
    {
        var p = Process.Start(new ProcessStartInfo(DemoTargetExe(), $"--left 200 --top 150 --title \"{title}\"") { UseShellExecute = false })!;
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(30))
        {
            p.Refresh();
            if (p.MainWindowHandle != 0 && p.MainWindowTitle == title) { Thread.Sleep(300); return (p, p.MainWindowHandle); }
            Thread.Sleep(50);
        }
        p.Kill();
        throw new TimeoutException("Demo target did not start.");
    }

    /// <summary>Directory with the OCR fixtures copied next to the benchmark binaries.</summary>
    public static string Fixture(string name)
    {
        var local = Path.Combine(AppContext.BaseDirectory, "Fixtures", name);
        return File.Exists(local) ? local : Path.Combine(RepoRoot(), "tests", "WindowVeil.OcrTests", "Fixtures", name);
    }

    /// <summary>PP-OCRv6 model directory.</summary>
    public static string ModelsDir() => Environment.GetEnvironmentVariable("WINDOWVEIL_OCR_MODELS") ?? Path.Combine(RepoRoot(), "models");
}
