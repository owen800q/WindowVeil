using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using WindowVeil.Appearance;
using WindowVeil.Geometry;
using WindowVeil.Locators;
using WindowVeil.Native;
using WindowVeil.Platform;
using WindowVeil.Rendering;

namespace WindowVeil.Benchmarks;

/// <summary>
/// Measures what BenchmarkDotNet does not: USER/GDI handle counts, working set, disposal reliability, and the CPU
/// cost and follow latency of tracking a real external window that is being moved, for 1–100 overlays.
/// Writes a Markdown report.
/// </summary>
internal static partial class ResourceProbe
{
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(nint hWnd, nint after, int x, int y, int cx, int cy, uint flags);

    [LibraryImport("user32.dll")]
    private static partial nint SetThreadDpiAwarenessContext(nint context);

    public static async Task<int> RunAsync(string output)
    {
        SetThreadDpiAwarenessContext(-4); // physical pixels for SetWindowPos
        var sb = new StringBuilder();
        sb.AppendLine("# WindowVeil resource probe").AppendLine();
        sb.AppendLine($"- Machine: {Environment.MachineName}, {Environment.ProcessorCount} logical CPUs, {RuntimeInformation.OSDescription}, {RuntimeInformation.FrameworkDescription}");
        sb.AppendLine($"- Date (UTC): {DateTimeOffset.UtcNow:u}").AppendLine();

        sb.AppendLine("## Native overlays: creation, reposition, handles, disposal").AppendLine();
        sb.AppendLine("| Overlays | Create+show (ms) | Reposition batch (ms, mean of 50) | USER objects (+) | GDI objects (+) | Working set (+MB) | Leaked USER/GDI after dispose |");
        sb.AppendLine("|---:|---:|---:|---:|---:|---:|---|");
        await using (var renderer = new NativeOverlayRenderer())
        {
            await renderer.CreateVisualAsync(new OverlayVisualRequest("warmup", MaskAppearance.Solid("#000"))).AsTask().ContinueWith(t => t.Result.DisposeAsync().AsTask()).Unwrap();
            foreach (int n in Shared.Counts)
            {
                GC.Collect(); GC.WaitForPendingFinalizers();
                var (u0, g0) = NativeDiagnostics.GetGuiResourceCounts();
                long ws0 = Process.GetCurrentProcess().WorkingSet64;
                var sw = Stopwatch.StartNew();
                var visuals = new List<IOverlayVisual>();
                for (int i = 0; i < n; i++) visuals.Add(await renderer.CreateVisualAsync(new OverlayVisualRequest($"r{i}", MaskAppearance.Solid("#202020"))));
                await renderer.ApplyAsync(visuals.Select((v, i) => new OverlayVisualUpdate(v, Shared.Frame(Shared.Cell(i)))).ToArray());
                double create = sw.Elapsed.TotalMilliseconds;
                var (u1, g1) = NativeDiagnostics.GetGuiResourceCounts();
                long ws1 = Process.GetCurrentProcess().WorkingSet64;
                sw.Restart();
                for (int k = 0; k < 50; k++)
                    await renderer.ApplyAsync(visuals.Select((v, i) => new OverlayVisualUpdate(v, Shared.Frame(Shared.Cell(i, k % 2 * 5)))).ToArray());
                double move = sw.Elapsed.TotalMilliseconds / 50;
                foreach (var v in visuals) await v.DisposeAsync();
                GC.Collect(); GC.WaitForPendingFinalizers();
                var (u2, g2) = NativeDiagnostics.GetGuiResourceCounts();
                sb.AppendLine($"| {n} | {create:0.0} | {move:0.00} | {u1 - u0} | {g1 - g0} | {(ws1 - ws0) / 1048576.0:0.0} | {u2 - u0} / {g2 - g0} |");
            }
        }

        sb.AppendLine().AppendLine("## Tracking a moving external window").AppendLine();
        sb.AppendLine("The demo target (separate process) is moved 100 times with SetWindowPos (10 ms apart) while N window-relative masks follow it.").AppendLine();
        sb.AppendLine("| Overlays | Follow latency p50 (ms) | p95 (ms) | Process CPU during moves (ms) | CPU per move (ms) |");
        sb.AppendLine("|---:|---:|---:|---:|---:|");
        var (proc, hwnd) = Shared.StartDemoTarget();
        try
        {
            foreach (int n in Shared.Counts)
            {
                await using var engine = WindowVeilEngine.CreateBuilder().UseWindowsDesktop().Build();
                await using var session = await engine.AttachAsync(hwnd, new SessionOptions { ElementRefreshInterval = TimeSpan.Zero });
                for (int i = 0; i < n; i++)
                    session.AddRule(MaskRule.Create(ElementLocator.WindowRelative(10 + (i % 10) * 30, 10 + (i / 10) * 20, 24, 14), MaskAppearance.Solid("#202020"), id: $"m{i}"));
                await session.StartAsync();
                var origin = engine.WindowSystem.GetWindow(hwnd)!;
                var raw = NativeDiagnostics.GetWindowRect(hwnd);
                var latencies = new List<double>();
                var cpu0 = Process.GetCurrentProcess().TotalProcessorTime;
                for (int k = 1; k <= 100; k++)
                {
                    int dx = (k % 2 == 0 ? 0 : 40);
                    var sw = Stopwatch.StartNew();
                    SetWindowPos(hwnd, 0, raw.X + dx, raw.Y, 0, 0, 0x0001 | 0x0004 | 0x0010);
                    var expected = session.GetRuleStatus("m0").OverlayBounds.FirstOrDefault();
                    var want = new PixelRect(origin.ClientBounds.X + 10 + dx, origin.ClientBounds.Y + 10, 24, 14);
                    while (sw.Elapsed < TimeSpan.FromSeconds(1))
                    {
                        var s = session.GetRuleStatus("m0");
                        if (s.OverlayBounds.Count == 1 && s.OverlayBounds[0] == want && NativeDiagnostics.GetWindowRect(s.OverlayHandle) == want) break;
                        await Task.Yield();
                    }
                    latencies.Add(sw.Elapsed.TotalMilliseconds);
                    _ = expected;
                    await Task.Delay(10);
                }
                var cpu = (Process.GetCurrentProcess().TotalProcessorTime - cpu0).TotalMilliseconds;
                latencies.Sort();
                sb.AppendLine($"| {n} | {latencies[49]:0.0} | {latencies[94]:0.0} | {cpu:0} | {cpu / 100:0.00} |");
                SetWindowPos(hwnd, 0, raw.X, raw.Y, 0, 0, 0x0001 | 0x0004 | 0x0010);
            }
        }
        finally { proc.Kill(); proc.Dispose(); }

        sb.AppendLine().AppendLine($"Live overlay windows registered after the probe: {OverlayWindowRegistry.Count} (expected 0).");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        await File.WriteAllTextAsync(output, sb.ToString());
        Console.WriteLine(sb.ToString());
        return OverlayWindowRegistry.Count == 0 ? 0 : 1;
    }
}
