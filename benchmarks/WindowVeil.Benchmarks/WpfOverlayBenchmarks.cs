using System.Windows.Controls;
using BenchmarkDotNet.Attributes;
using WindowVeil.Geometry;
using WindowVeil.Rendering;
using WindowVeil.Wpf;

namespace WindowVeil.Benchmarks;

/// <summary>WPF host creation and layout overhead.</summary>
[MemoryDiagnoser]
public class WpfOverlayBenchmarks
{
    private WpfOverlayRenderer _renderer = null!;
    private IOverlayVisual _measured = null!;

    /// <summary>Number of overlays.</summary>
    [Params(1, 10, 25, 50, 100)]
    public int Count { get; set; }

    /// <summary>Hosting mode.</summary>
    [Params(WpfHostingMode.PerOverlay, WpfHostingMode.Grouped)]
    public WpfHostingMode Mode { get; set; }

    private static WpfAppearance Button() => WpfAppearance.Create(() => new Button { Content = "Custom Action", Width = 140, Height = 36 });

    /// <summary>Creates the renderer and its dispatcher thread once.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _renderer = new WpfOverlayRenderer(new WpfOverlayOptions { HostingMode = Mode });
        _measured = _renderer.CreateVisualAsync(new OverlayVisualRequest("measure", Button())).AsTask().GetAwaiter().GetResult();
    }

    /// <summary>Shuts the dispatcher down.</summary>
    [GlobalCleanup]
    public void Cleanup() => _renderer.DisposeAsync().AsTask().GetAwaiter().GetResult();

    /// <summary>Create N WPF overlays (factory on the dispatcher), show them, dispose them.</summary>
    [Benchmark]
    public async Task CreateShowDispose()
    {
        var visuals = new IOverlayVisual[Count];
        var updates = new OverlayVisualUpdate[Count];
        for (int i = 0; i < Count; i++)
        {
            visuals[i] = await _renderer.CreateVisualAsync(new OverlayVisualRequest($"r{i}", Button()));
            updates[i] = new OverlayVisualUpdate(visuals[i], Shared.Frame(Shared.Cell(i) with { Width = 140, Height = 36 }));
        }
        await _renderer.ApplyAsync(updates);
        foreach (var v in visuals) await v.DisposeAsync();
    }

    /// <summary>Measure pass used by content-sized placements (one dispatcher round trip).</summary>
    [Benchmark]
    public ValueTask<PixelSize?> MeasureLayout() => _measured.MeasureAsync(DpiScale.FromPercent(150));
}
