using WindowVeil.Appearance;
using WindowVeil.Geometry;

namespace WindowVeil.Rendering;

/// <summary>
/// Creates and updates overlay visuals for the appearances it supports. Each renderer owns its threading model
/// (the native renderer uses a dedicated Win32 message thread, the WPF renderer a WPF dispatcher); callers may
/// invoke these methods from any thread.
/// </summary>
public interface IOverlayRenderer : IAsyncDisposable
{
    /// <summary>Display name for diagnostics.</summary>
    string Name { get; }

    /// <summary>Whether this renderer can draw the appearance.</summary>
    bool CanRender(OverlayAppearance appearance);

    /// <summary>Creates a hidden visual. Throws <see cref="WindowVeilException"/> with <see cref="WindowVeilErrorCode.OverlayCreationFailed"/> on failure.</summary>
    ValueTask<IOverlayVisual> CreateVisualAsync(OverlayVisualRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies a batch of updates. Renderers should apply them atomically where the platform allows
    /// (for example one <c>DeferWindowPos</c> batch) to avoid visible tearing between overlays.
    /// </summary>
    ValueTask ApplyAsync(IReadOnlyList<OverlayVisualUpdate> updates, CancellationToken cancellationToken = default);
}

/// <summary>A renderer-specific overlay instance (typically one host window or one element in a shared host).</summary>
public interface IOverlayVisual : IAsyncDisposable
{
    /// <summary>The rule that owns this visual.</summary>
    string RuleId { get; }

    /// <summary>Native handle of the host window, or 0 when not applicable.</summary>
    nint Handle { get; }

    /// <summary>Last applied frame (null before the first update).</summary>
    OverlayFrame? CurrentFrame { get; }

    /// <summary>
    /// Desired content size in physical pixels at the given DPI, for placements that size to content.
    /// Returns null when the visual has no intrinsic size (masks, highlights).
    /// </summary>
    ValueTask<PixelSize?> MeasureAsync(DpiScale dpi, CancellationToken cancellationToken = default);
}

/// <summary>Parameters for creating a visual.</summary>
public sealed record OverlayVisualRequest(string RuleId, OverlayAppearance Appearance)
{
    /// <summary>Pointer behavior.</summary>
    public OverlayInputMode InputMode { get; init; } = OverlayInputMode.PassThrough;

    /// <summary>Activation/focus behavior for interactive overlays.</summary>
    public OverlayFocusPolicy FocusPolicy { get; init; } = OverlayFocusPolicy.NoActivate;

    /// <summary>Z-order strategy.</summary>
    public OverlayZOrder ZOrder { get; init; } = OverlayZOrder.AboveTarget;
}

/// <summary>A visual plus the frame it should display.</summary>
public readonly record struct OverlayVisualUpdate(IOverlayVisual Visual, OverlayFrame Frame);

/// <summary>
/// Everything a renderer needs to draw one overlay for one moment in time. All rectangles are physical screen pixels.
/// </summary>
public sealed record OverlayFrame
{
    /// <summary>A frame that hides the overlay.</summary>
    public static OverlayFrame Hidden { get; } = new() { Visible = false };

    /// <summary>Whether the overlay should be shown.</summary>
    public bool Visible { get; init; }

    /// <summary>Host bounds (union of <see cref="Regions"/>).</summary>
    public PixelRect Bounds { get; init; }

    /// <summary>Individual overlay rectangles (several for multi-line text masks).</summary>
    public IReadOnlyList<PixelRect> Regions { get; init; } = [];

    /// <summary>The resolved target rectangles (before placement), used by spotlight and highlight.</summary>
    public IReadOnlyList<PixelRect> TargetRects { get; init; } = [];

    /// <summary>The target window handle, used for z-order.</summary>
    public nint TargetWindow { get; init; }

    /// <summary>The target window bounds.</summary>
    public PixelRect TargetWindowBounds { get; init; }

    /// <summary>The bounds of the monitor hosting the target.</summary>
    public PixelRect MonitorBounds { get; init; }

    /// <summary>DPI of the target's monitor.</summary>
    public DpiScale Dpi { get; init; } = DpiScale.Default;

    /// <summary>Whether the target window is in the foreground.</summary>
    public bool TargetIsForeground { get; init; }

    /// <summary>Creates a visible frame for the given regions.</summary>
    public static OverlayFrame Show(IReadOnlyList<PixelRect> regions, IReadOnlyList<PixelRect> targets, Platform.TargetWindowInfo window)
    {
        ArgumentNullException.ThrowIfNull(regions);
        ArgumentNullException.ThrowIfNull(window);
        return new OverlayFrame
        {
            Visible = regions.Count > 0,
            Bounds = PixelRect.UnionAll(regions),
            Regions = regions,
            TargetRects = targets,
            TargetWindow = window.Handle,
            TargetWindowBounds = window.Bounds,
            MonitorBounds = window.MonitorBounds,
            Dpi = window.Dpi,
            TargetIsForeground = window.IsForeground,
        };
    }

    /// <summary>Structural comparison used to skip redundant native calls.</summary>
    public bool IsEquivalentTo(OverlayFrame? other) =>
        other is not null && Visible == other.Visible && Bounds == other.Bounds && TargetWindow == other.TargetWindow &&
        Dpi == other.Dpi && TargetWindowBounds == other.TargetWindowBounds && TargetIsForeground == other.TargetIsForeground &&
        Regions.SequenceEqual(other.Regions) && TargetRects.SequenceEqual(other.TargetRects);
}

/// <summary>Pointer input behavior of an overlay.</summary>
public enum OverlayInputMode
{
    /// <summary>Visual only; mouse input goes to the window underneath (the target application).</summary>
    PassThrough,

    /// <summary>The overlay receives mouse input. Clicks are <b>not</b> forwarded to the target application.</summary>
    Interactive,
}

/// <summary>Whether an interactive overlay may take activation and keyboard focus.</summary>
public enum OverlayFocusPolicy
{
    /// <summary>Never activates; the target keeps foreground and keyboard focus (buttons still receive clicks).</summary>
    NoActivate,

    /// <summary>Activates when clicked so controls such as text boxes can receive keyboard input.</summary>
    ActivateOnClick,
}

/// <summary>Z-order strategy for overlay windows.</summary>
public enum OverlayZOrder
{
    /// <summary>
    /// Kept directly above the target window, so unrelated windows in front of the target also cover the overlay (default).
    /// </summary>
    AboveTarget,

    /// <summary>Always on top of every non-topmost window. Use sparingly: it can cover unrelated applications.</summary>
    Topmost,
}
