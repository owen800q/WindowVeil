using System.Windows;
using System.Windows.Controls;
using WindowVeil.Geometry;
using DpiScale = WindowVeil.Geometry.DpiScale;
using WindowVeil.Rendering;

namespace WindowVeil.Wpf.Hosting;

/// <summary>
/// One WPF overlay: the factory-created content inside a root <see cref="Grid"/>, hosted either in its own
/// <see cref="OverlayHostWindow"/> or in a shared <see cref="OverlayGroup"/>. WPF members are touched on the renderer's
/// dispatcher thread only; <see cref="Handle"/> and <see cref="CurrentFrame"/> may be read from any thread.
/// </summary>
internal sealed class WpfOverlayVisual : IOverlayVisual
{
    private OverlayFrame? _frame;
    private nint _handle;
    private int _disposed;

    public WpfOverlayVisual(WpfOverlayRenderer renderer, OverlayVisualRequest request, WpfAppearance appearance,
        WpfOverlayContext context, UIElement content, Grid root)
    {
        Renderer = renderer;
        Request = request;
        Appearance = appearance;
        Context = context;
        Content = content;
        Root = root;
    }

    public WpfOverlayRenderer Renderer { get; }

    public OverlayVisualRequest Request { get; }

    public WpfAppearance Appearance { get; }

    public WpfOverlayContext Context { get; }

    /// <summary>The element returned by the factory (dispatcher thread only).</summary>
    public UIElement Content { get; }

    /// <summary>The root container sized to the overlay bounds (dispatcher thread only).</summary>
    public Grid Root { get; }

    /// <summary>The dedicated host window in per-overlay mode (dispatcher thread only).</summary>
    public OverlayHostWindow? Host { get; set; }

    /// <summary>The shared host in grouped mode (dispatcher thread only).</summary>
    public OverlayGroup? Group { get; set; }

    /// <inheritdoc />
    public string RuleId => Request.RuleId;

    /// <inheritdoc />
    public nint Handle => Volatile.Read(ref _handle);

    /// <inheritdoc />
    public OverlayFrame? CurrentFrame => Volatile.Read(ref _frame);

    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    public void SetHandle(nint handle) => Volatile.Write(ref _handle, handle);

    public void SetFrame(OverlayFrame frame) => Volatile.Write(ref _frame, frame);

    /// <summary>Marks the visual disposed; returns false when it already was.</summary>
    public bool MarkDisposed() => Interlocked.Exchange(ref _disposed, 1) == 0;

    /// <summary>Whether the current frame shows the overlay.</summary>
    public bool IsShowing(out PixelRect bounds)
    {
        var f = CurrentFrame;
        bounds = f?.Bounds ?? PixelRect.Empty;
        return f is { Visible: true } && !bounds.IsEmpty;
    }

    /// <inheritdoc />
    public ValueTask<PixelSize?> MeasureAsync(DpiScale dpi, CancellationToken cancellationToken = default) =>
        Renderer.MeasureAsync(this, dpi, cancellationToken);

    /// <inheritdoc />
    public ValueTask DisposeAsync() => MarkDisposed() ? Renderer.DisposeVisualAsync(this) : ValueTask.CompletedTask;

    /// <inheritdoc />
    public override string ToString() => $"WPF overlay '{RuleId}' 0x{Handle:X}";
}

/// <summary>Identity of a shared host window in grouped mode.</summary>
internal readonly record struct OverlayGroupKey(nint Target, OverlayInputMode InputMode, OverlayFocusPolicy FocusPolicy, OverlayZOrder ZOrder);

/// <summary>A shared host window plus the overlays placed on its canvas (dispatcher thread only).</summary>
internal sealed class OverlayGroup(OverlayGroupKey key, OverlayHostWindow window, Canvas canvas)
{
    public OverlayGroupKey Key { get; } = key;

    public OverlayHostWindow Window { get; } = window;

    public Canvas Canvas { get; } = canvas;

    public List<WpfOverlayVisual> Members { get; } = [];
}
