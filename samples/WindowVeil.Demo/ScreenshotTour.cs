using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WindowVeil.Capture;
using WindowVeil.Geometry;
using WindowVeil.Imaging;
using WindowVeil.Native;
using WindowVeil.Platform;
using WindowVeil.Rendering;

namespace WindowVeil.Demo;

/// <summary>
/// Automated, real before/after screenshots of the bundled demo target: launches the target as a separate process,
/// captures the composed desktop, applies the demo scenarios through the public WindowVeil API, captures again and
/// writes a labelled side-by-side comparison. Used by the "Screenshots" workflow.
/// </summary>
internal static class ScreenshotTour
{
    private const string TargetTitle = "WindowVeil Demo Target";

    public static async Task<int> RunAsync(string outputDir)
    {
        Directory.CreateDirectory(outputDir);
        var log = new StringBuilder();
        void Log(string s) { log.AppendLine(s); Console.WriteLine(s); }
        Process? target = null;
        try
        {
            target = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--demo-target --left 40 --top 40") { UseShellExecute = false })!;
            await using var engine = WindowVeilEngine.CreateBuilder().UseWindowsDesktop().UseWpfOverlays().Build();
            // Attach to the process just started, not to any window whose title happens to match (an interactive demo may be open).
            var session = await engine.AttachAsync(WindowSelector.ByProcessId(target.Id).WithTitle(TargetTitle), new SessionOptions { AttachTimeout = TimeSpan.FromSeconds(30) });
            var hwnd = session.Target!.Handle;
            await Task.Delay(1500); // first WPF render of the target
            var window = engine.WindowSystem.GetWindow(hwnd)!;
            var region = window.Bounds.Inflate(40, 60, 40, 40).Intersect(engine.WindowSystem.VirtualScreenBounds);
            Log($"Target {window}; capture region {region}");

            var before = await StableCaptureAsync(region);
            PngCodec.Save(before, Path.Combine(outputDir, "demo-before.png"));

            // All scenarios except the spotlight (it darkens everything), then the spotlight on its own.
            var noop = new RelayCommand(_ => { });
            var scenarios = DemoScenarios.Create(("Hide all masks", noop), ("Show all", noop), ("Say hi", noop));
            var ocr = scenarios.Where(s => s.RequiresOcr).Select(s => s.Id).ToHashSet();
            foreach (var s in scenarios.Where(s => s.Id != "apply-spotlight" && !ocr.Contains(s.Id))) session.AddRule(s.CreateRule());
            await session.StartAsync();
            if (!await WaitActiveAsync(session, Log)) return Fail("Not every overlay became Active; refusing to save an 'after' screenshot.");
            var after = await StableCaptureAsync(region);
            PngCodec.Save(after, Path.Combine(outputDir, "demo-after.png"));

            foreach (var r in session.Rules.ToArray()) session.RemoveRule(r.Id);
            session.AddRule(scenarios.First(s => s.Id == "apply-spotlight").CreateRule());
            session.AddRule(scenarios.First(s => s.Id == "startup-highlight").CreateRule());
            await session.RefreshAsync();
            if (!await WaitActiveAsync(session, Log)) return Fail("The spotlight scenario did not become Active.");
            var spot = await StableCaptureAsync(region);
            PngCodec.Save(spot, Path.Combine(outputDir, "demo-spotlight.png"));

            await session.DisposeAsync();
            var restored = await StableCaptureAsync(region);
            PngCodec.Save(restored, Path.Combine(outputDir, "demo-restored.png"));

            Compose(before, after, "Before: the demo target as its own process", "After: the same window with WindowVeil overlays",
                Path.Combine(outputDir, "demo-comparison.png"));
            Log($"Live overlay windows after disposal: {OverlayWindowRegistry.Count}");
            return OverlayWindowRegistry.Count == 0 ? 0 : Fail("Overlay windows leaked after the session was disposed.");
        }
        catch (Exception ex)
        {
            return Fail(ex.ToString());
        }
        finally
        {
            // Killing can fail in several ways (already exited, access denied while exiting); the log must still be written.
            try { if (target is { HasExited: false }) target.Kill(); }
            catch (Exception ex) { Log("Could not stop the target: " + ex.Message); }
            target?.Dispose();
            File.WriteAllText(Path.Combine(outputDir, "demo-log.txt"), log.ToString());
        }

        int Fail(string reason)
        {
            Log("FAILED: " + reason);
            try { PngCodec.Save(DesktopScreenshot.CaptureVirtualScreen(), Path.Combine(outputDir, "demo-failure.png")); }
            catch (Exception ex) { Log("Could not save the failure screenshot: " + ex.Message); }
            return 1;
        }
    }

    /// <summary>Waits until every rule is Active; returns false (after logging every status) if any is not.</summary>
    private static async Task<bool> WaitActiveAsync(OverlaySession session, Action<string> log)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(20) && session.RuleStatuses.Any(s => s.State != RuleState.Active)) await Task.Delay(100);
        await Task.Delay(500);
        var statuses = session.RuleStatuses;
        foreach (var s in statuses) log("  " + s);
        return statuses.Count > 0 && statuses.All(s => s.State == RuleState.Active);
    }

    private static async Task<BgraImage> StableCaptureAsync(PixelRect region)
    {
        NativeDiagnostics.DwmFlush();
        var prev = DesktopScreenshot.CaptureRegion(region);
        for (int i = 0; i < 60; i++)
        {
            await Task.Delay(50);
            NativeDiagnostics.DwmFlush();
            var cur = DesktopScreenshot.CaptureRegion(region);
            if (cur.Pixels.AsSpan().SequenceEqual(prev.Pixels)) return cur;
            prev = cur;
        }
        return prev; // animated content (the pulsing badge) never settles completely
    }

    private static void Compose(BgraImage left, BgraImage right, string leftLabel, string rightLabel, string path)
    {
        const int margin = 20, header = 40, gap = 20;
        int w = margin * 2 + left.Width + right.Width + gap, h = header + Math.Max(left.Height, right.Height) + margin;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0xF4, 0xF5, 0xF7)), null, new Rect(0, 0, w, h));
            var face = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
            void Text(string t, double x) => dc.DrawText(new FormattedText(t, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, 15, Brushes.Black, 1.0), new Point(x, 12));
            Text(leftLabel, margin);
            Text(rightLabel, margin + left.Width + gap);
            dc.DrawImage(ToBitmap(left), new Rect(margin, header, left.Width, left.Height));
            dc.DrawImage(ToBitmap(right), new Rect(margin + left.Width + gap, header, right.Width, right.Height));
        }
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(path);
        enc.Save(fs);
    }

    private static BitmapSource ToBitmap(BgraImage img)
    {
        var b = BitmapSource.Create(img.Width, img.Height, 96, 96, PixelFormats.Bgra32, null, img.Pixels, img.Stride);
        b.Freeze();
        return b;
    }
}
