using WindowVeil.Appearance;
using WindowVeil.Geometry;
using WindowVeil.Rendering;

namespace WindowVeil.Native;

/// <summary>
/// One piece of an overlay window region: <see cref="Outer"/> minus every rectangle in <see cref="Holes"/>.
/// Coordinates are relative to the overlay window's top-left corner (physical pixels).
/// </summary>
/// <param name="Outer">Covered rectangle.</param>
/// <param name="Holes">Rectangles cut out of <paramref name="Outer"/>.</param>
public readonly record struct OverlayRegionShape(PixelRect Outer, IReadOnlyList<PixelRect> Holes)
{
    /// <summary>Structural equality (records compare lists by reference).</summary>
    public bool SameAs(OverlayRegionShape other) => Outer == other.Outer && Holes.SequenceEqual(other.Holes);
}

/// <summary>
/// The native window geometry derived from an appearance and a frame: where the overlay window goes and which parts
/// of it are covered. Pure computation, independent of Win32, so it can be unit tested anywhere.
/// </summary>
public sealed record OverlayLayout
{
    /// <summary>A hidden layout.</summary>
    public static OverlayLayout Hidden { get; } = new() { Visible = false };

    /// <summary>Whether the overlay window is shown.</summary>
    public bool Visible { get; init; }

    /// <summary>Window bounds in physical screen pixels.</summary>
    public PixelRect Bounds { get; init; }

    /// <summary>
    /// Window region as a union of shapes (window-relative), or null when the whole window rectangle is covered.
    /// </summary>
    public IReadOnlyList<OverlayRegionShape>? Shapes { get; init; }

    /// <summary>Whether two shape lists describe the same region.</summary>
    public static bool SameShapes(IReadOnlyList<OverlayRegionShape>? a, IReadOnlyList<OverlayRegionShape>? b)
    {
        if (a is null || b is null) return a is null && b is null;
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++) if (!a[i].SameAs(b[i])) return false;
        return true;
    }

    /// <summary>Computes the layout for a native appearance.</summary>
    /// <exception cref="ArgumentException">The appearance is not a mask, highlight or spotlight.</exception>
    public static OverlayLayout Compute(OverlayAppearance appearance, OverlayFrame frame)
    {
        ArgumentNullException.ThrowIfNull(appearance);
        ArgumentNullException.ThrowIfNull(frame);
        if (!frame.Visible) return Hidden;
        return appearance switch
        {
            MaskAppearance => ComputeMask(frame),
            HighlightAppearance h => ComputeHighlight(h, frame),
            SpotlightAppearance s => ComputeSpotlight(s, frame),
            _ => throw new ArgumentException($"'{appearance.Kind}' appearances are not rendered natively.", nameof(appearance)),
        };
    }

    private static OverlayLayout ComputeMask(OverlayFrame frame)
    {
        var regions = frame.Regions.Where(r => !r.IsEmpty).ToArray();
        var bounds = regions.Length > 0 ? PixelRect.UnionAll(regions) : frame.Bounds;
        if (bounds.IsEmpty) return Hidden;
        // A single rectangle covers the whole window: no region needed (cheapest for the compositor).
        IReadOnlyList<OverlayRegionShape>? shapes = regions.Length <= 1 || regions.All(r => r == bounds)
            ? null
            : regions.Select(r => new OverlayRegionShape(Relative(r, bounds), [])).ToArray();
        return new OverlayLayout { Visible = true, Bounds = bounds, Shapes = shapes };
    }

    private static OverlayLayout ComputeHighlight(HighlightAppearance h, OverlayFrame frame)
    {
        int t = Math.Max(1, CoordinateConverter.DipToPhysicalRounded(h.Thickness, frame.Dpi));
        var rects = frame.Regions.Where(r => !r.IsEmpty).ToArray();
        if (rects.Length == 0 && !frame.Bounds.IsEmpty) rects = [frame.Bounds];
        if (rects.Length == 0) return Hidden;
        var bounds = PixelRect.UnionAll(rects.Select(r => r.Inflate(t)));
        // Each rectangle becomes a ring: the border is drawn outside the target so the target stays fully visible.
        var shapes = rects.Select(r => new OverlayRegionShape(Relative(r.Inflate(t), bounds), [Relative(r, bounds)])).ToArray();
        return new OverlayLayout { Visible = true, Bounds = bounds, Shapes = shapes };
    }

    private static OverlayLayout ComputeSpotlight(SpotlightAppearance s, OverlayFrame frame)
    {
        var area = s.Scope == SpotlightScope.Monitor && !frame.MonitorBounds.IsEmpty ? frame.MonitorBounds : frame.TargetWindowBounds;
        if (area.IsEmpty) return Hidden;
        IEnumerable<PixelRect> holes = frame.TargetRects.Count > 0 ? frame.TargetRects : frame.Regions;
        var rel = holes.Where(r => !r.IsEmpty && r.IntersectsWith(area)).Select(r => Relative(r, area)).ToArray();
        return new OverlayLayout
        {
            Visible = true,
            Bounds = area,
            Shapes = [new OverlayRegionShape(new PixelRect(0, 0, area.Width, area.Height), rel)],
        };
    }

    private static PixelRect Relative(PixelRect r, PixelRect origin) => CoordinateConverter.ScreenToRelative(r, origin.Location);
}
