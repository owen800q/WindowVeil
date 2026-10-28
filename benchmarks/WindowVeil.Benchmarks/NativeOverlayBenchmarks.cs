using BenchmarkDotNet.Attributes;
using WindowVeil.Appearance;
using WindowVeil.Native;
using WindowVeil.Rendering;

namespace WindowVeil.Benchmarks;

/// <summary>Creation, batched show and disposal of N native mask windows.</summary>
[MemoryDiagnoser]
public class NativeOverlayLifecycleBenchmarks
{
    private NativeOverlayRenderer _renderer = null!;

    /// <summary>Number of overlays.</summary>
    [Params(1, 10, 25, 50, 100)]
    public int Count { get; set; }

    /// <summary>Creates the renderer (and its message thread) once.</summary>
    [GlobalSetup]
    public void Setup() => _renderer = new NativeOverlayRenderer();

    /// <summary>Disposes the renderer.</summary>
    [GlobalCleanup]
    public void Cleanup() => _renderer.DisposeAsync().AsTask().GetAwaiter().GetResult();

    /// <summary>Create N windows, show them in one DeferWindowPos batch, destroy them.</summary>
    [Benchmark]
    public async Task CreateShowDispose()
    {
        var visuals = new IOverlayVisual[Count];
        var updates = new OverlayVisualUpdate[Count];
        for (int i = 0; i < Count; i++)
        {
            visuals[i] = await _renderer.CreateVisualAsync(new OverlayVisualRequest($"r{i}", MaskAppearance.Solid("#202020")));
            updates[i] = new OverlayVisualUpdate(visuals[i], Shared.Frame(Shared.Cell(i)));
        }
        await _renderer.ApplyAsync(updates);
        foreach (var v in visuals) await v.DisposeAsync();
    }
}

/// <summary>Moving N existing native overlays in one batch (the per-frame cost while a window is dragged).</summary>
[MemoryDiagnoser]
public class NativeOverlayRepositionBenchmarks
{
    private NativeOverlayRenderer _renderer = null!;
    private IOverlayVisual[] _visuals = [];
    private OverlayVisualUpdate[] _a = [], _b = [];
    private bool _flip;

    /// <summary>Number of overlays.</summary>
    [Params(1, 10, 25, 50, 100)]
    public int Count { get; set; }

    /// <summary>Creates and shows the overlays.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _renderer = new NativeOverlayRenderer();
        _visuals = new IOverlayVisual[Count];
        for (int i = 0; i < Count; i++)
            _visuals[i] = _renderer.CreateVisualAsync(new OverlayVisualRequest($"r{i}", MaskAppearance.Solid("#202020"))).AsTask().GetAwaiter().GetResult();
        _a = _visuals.Select((v, i) => new OverlayVisualUpdate(v, Shared.Frame(Shared.Cell(i)))).ToArray();
        _b = _visuals.Select((v, i) => new OverlayVisualUpdate(v, Shared.Frame(Shared.Cell(i, 7)))).ToArray();
        _renderer.ApplyAsync(_a).AsTask().GetAwaiter().GetResult();
    }

    /// <summary>Destroys everything.</summary>
    [GlobalCleanup]
    public void Cleanup() => _renderer.DisposeAsync().AsTask().GetAwaiter().GetResult();

    /// <summary>Moves every overlay by 7 px (alternating) in a single batch.</summary>
    [Benchmark]
    public ValueTask Reposition()
    {
        _flip = !_flip;
        return _renderer.ApplyAsync(_flip ? _b : _a);
    }
}
