using System.Collections.Concurrent;
using WindowVeil.Appearance;
using WindowVeil.Geometry;
using WindowVeil.Rendering;

namespace WindowVeil.UnitTests.Fakes;

/// <summary>A custom appearance kind that only <see cref="FakeRenderer"/> can draw (stands in for WPF content).</summary>
internal sealed record FakeContentAppearance(string Label) : OverlayAppearance
{
    public override string Kind => "fakeContent";

    /// <summary>Content size reported by <see cref="FakeVisual.MeasureAsync"/> (physical pixels at 100%).</summary>
    public PixelSize? ContentSize { get; init; }
}

/// <summary>Records every visual it creates and every frame it applies. Failures can be injected.</summary>
internal sealed class FakeRenderer(string name = "fake") : IOverlayRenderer
{
    private static long s_nextHandle = 0x900000;
    private readonly ConcurrentQueue<FakeVisual> _created = new();
    private int _disposed;

    public string Name { get; } = name;

    public Func<OverlayAppearance, bool> CanRenderPredicate { get; set; } = _ => true;

    /// <summary>When it returns true for a request, creation throws <see cref="WindowVeilException"/>.</summary>
    public Func<OverlayVisualRequest, bool>? FailCreate { get; set; }

    /// <summary>When true, <see cref="ApplyAsync"/> throws.</summary>
    public volatile bool FailApply;

    public int ApplyCalls;

    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    public IReadOnlyList<FakeVisual> Created => _created.ToArray();

    public IReadOnlyList<FakeVisual> Live => _created.Where(v => !v.IsDisposed).ToArray();

    public IReadOnlyList<FakeVisual> For(string ruleId) => _created.Where(v => v.RuleId == ruleId).ToArray();

    public FakeVisual? LiveFor(string ruleId) => _created.LastOrDefault(v => v.RuleId == ruleId && !v.IsDisposed);

    public bool CanRender(OverlayAppearance appearance) => CanRenderPredicate(appearance);

    public ValueTask<IOverlayVisual> CreateVisualAsync(OverlayVisualRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (FailCreate?.Invoke(request) == true)
            throw new WindowVeilException(WindowVeilErrorCode.OverlayCreationFailed, $"Injected creation failure for '{request.RuleId}'.");
        var v = new FakeVisual(request, Interlocked.Increment(ref s_nextHandle));
        _created.Enqueue(v);
        return ValueTask.FromResult<IOverlayVisual>(v);
    }

    public ValueTask ApplyAsync(IReadOnlyList<OverlayVisualUpdate> updates, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref ApplyCalls);
        if (FailApply) throw new InvalidOperationException("Injected apply failure.");
        foreach (var u in updates) ((FakeVisual)u.Visual).Apply(u.Frame);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _disposed, 1);
        return ValueTask.CompletedTask;
    }
}

internal sealed class FakeVisual(OverlayVisualRequest request, long handle) : IOverlayVisual
{
    private readonly object _gate = new();
    private readonly List<OverlayFrame> _frames = [];
    private OverlayFrame? _current;
    private int _disposed;

    public OverlayVisualRequest Request { get; } = request;

    public string RuleId => Request.RuleId;

    public nint Handle { get; } = (nint)handle;

    public OverlayFrame? CurrentFrame { get { lock (_gate) return _current; } }

    public IReadOnlyList<OverlayFrame> Frames { get { lock (_gate) return _frames.ToArray(); } }

    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    public bool IsShown => CurrentFrame is { Visible: true } && !IsDisposed;

    public int MeasureCalls;

    public void Apply(OverlayFrame frame)
    {
        lock (_gate)
        {
            _frames.Add(frame);
            _current = frame;
        }
    }

    public ValueTask<PixelSize?> MeasureAsync(DpiScale dpi, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref MeasureCalls);
        PixelSize? size = Request.Appearance is FakeContentAppearance { ContentSize: { } s }
            ? new PixelSize((int)Math.Round(s.Width * dpi.Scale), (int)Math.Round(s.Height * dpi.Scale))
            : null;
        return ValueTask.FromResult(size);
    }

    public ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _disposed, 1);
        return ValueTask.CompletedTask;
    }
}
