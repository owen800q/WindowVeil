using System.Windows;
using System.Windows.Threading;
using WindowVeil.Geometry;
using DpiScale = WindowVeil.Geometry.DpiScale;
using WindowVeil.Rendering;

namespace WindowVeil.Wpf;

/// <summary>
/// Per-overlay information passed to a <see cref="WpfAppearance"/> factory: the rule, the dispatcher that owns the
/// content, and the current geometry. View models can keep a reference and react to
/// <see cref="TargetBoundsChanged"/>, which is raised on <see cref="Dispatcher"/> so bound properties can be updated
/// directly.
/// </summary>
/// <remarks>
/// Geometry properties are immutable snapshots and may be read from any thread. "DIP" values are physical pixels divided by
/// the target monitor's scale (<see cref="Dpi"/>), with the same virtual-screen origin; "in overlay" values are relative to
/// the overlay's top-left corner, i.e. the coordinate space of the content's root.
/// </remarks>
public sealed class WpfOverlayContext
{
    private OverlayFrame? _frame;

    internal WpfOverlayContext(string ruleId, Dispatcher dispatcher, OverlayVisualRequest request)
    {
        RuleId = ruleId;
        Dispatcher = dispatcher;
        InputMode = request.InputMode;
        FocusPolicy = request.FocusPolicy;
        ZOrder = request.ZOrder;
    }

    /// <summary>Id of the rule that owns the overlay.</summary>
    public string RuleId { get; }

    /// <summary>The dispatcher that owns the overlay content and host window. Events are raised on it.</summary>
    public Dispatcher Dispatcher { get; }

    /// <summary>Pointer behavior of the overlay.</summary>
    public OverlayInputMode InputMode { get; }

    /// <summary>Focus policy of the overlay.</summary>
    public OverlayFocusPolicy FocusPolicy { get; }

    /// <summary>Z-order strategy of the overlay.</summary>
    public OverlayZOrder ZOrder { get; }

    /// <summary>The last frame applied to the overlay, or null before the first update.</summary>
    public OverlayFrame? Frame => Volatile.Read(ref _frame);

    /// <summary>Whether the overlay is currently shown.</summary>
    public bool IsVisible => Frame is { Visible: true, Bounds.IsEmpty: false };

    /// <summary>DPI of the target's monitor (96 DPI before the first update).</summary>
    public DpiScale Dpi => Frame?.Dpi ?? DpiScale.Default;

    /// <summary>Target window handle (0 before the first update).</summary>
    public nint TargetWindow => Frame?.TargetWindow ?? 0;

    /// <summary>Target window bounds in physical screen pixels.</summary>
    public PixelRect TargetWindowBounds => Frame?.TargetWindowBounds ?? PixelRect.Empty;

    /// <summary>Overlay bounds in physical screen pixels.</summary>
    public PixelRect OverlayBounds => Frame?.Bounds ?? PixelRect.Empty;

    /// <summary>Overlay bounds in screen DIPs.</summary>
    public Rect OverlayBoundsDip => ToDip(OverlayBounds, Dpi);

    /// <summary>Resolved target rectangles in physical screen pixels.</summary>
    public IReadOnlyList<PixelRect> TargetRects => Frame?.TargetRects ?? [];

    /// <summary>Resolved target rectangles in screen DIPs.</summary>
    public IReadOnlyList<Rect> TargetRectsDip
    {
        get
        {
            var dpi = Dpi;
            return TargetRects.Select(r => ToDip(r, dpi)).ToArray();
        }
    }

    /// <summary>
    /// Resolved target rectangles relative to the overlay's top-left corner, in DIPs (the content's coordinate space).
    /// Useful for content that draws around or next to the target, for example a callout arrow.
    /// </summary>
    public IReadOnlyList<Rect> TargetRectsInOverlay
    {
        get
        {
            var frame = Frame;
            if (frame is null) return [];
            var origin = frame.Bounds.Location;
            return frame.TargetRects.Select(r => ToDip(r.Offset(-origin.X, -origin.Y), frame.Dpi)).ToArray();
        }
    }

    /// <summary>
    /// Raised on <see cref="Dispatcher"/> after the overlay geometry (bounds, target rectangles, DPI or visibility) changed
    /// and the new frame has been applied. Handler exceptions are logged and swallowed.
    /// </summary>
    public event EventHandler<WpfTargetBoundsChangedEventArgs>? TargetBoundsChanged;

    /// <summary>Stores the new frame and raises <see cref="TargetBoundsChanged"/> when the geometry changed. Dispatcher thread only.</summary>
    /// <returns>The exception thrown by a handler, if any.</returns>
    internal Exception? Update(OverlayFrame frame)
    {
        var previous = Interlocked.Exchange(ref _frame, frame);
        if (frame.IsEquivalentTo(previous)) return null;
        try
        {
            TargetBoundsChanged?.Invoke(this, new WpfTargetBoundsChangedEventArgs(previous, frame));
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    private static Rect ToDip(PixelRect r, DpiScale dpi)
    {
        if (r.IsEmpty) return Rect.Empty;
        var d = CoordinateConverter.ScreenToDip(r, dpi);
        return new Rect(d.X, d.Y, d.Width, d.Height);
    }
}

/// <summary>Arguments of <see cref="WpfOverlayContext.TargetBoundsChanged"/>.</summary>
public sealed class WpfTargetBoundsChangedEventArgs : EventArgs
{
    internal WpfTargetBoundsChangedEventArgs(OverlayFrame? previous, OverlayFrame current)
    {
        Previous = previous;
        Current = current;
    }

    /// <summary>The previously applied frame, or null for the first update.</summary>
    public OverlayFrame? Previous { get; }

    /// <summary>The frame that was just applied.</summary>
    public OverlayFrame Current { get; }
}
