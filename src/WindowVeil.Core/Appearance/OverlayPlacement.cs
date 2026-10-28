using WindowVeil.Geometry;

namespace WindowVeil.Appearance;

/// <summary>Where an overlay is placed relative to its resolved target.</summary>
public enum PlacementMode
{
    /// <summary>Exactly over the target (plus padding).</summary>
    Cover,

    /// <summary>To the right of the target.</summary>
    Right,

    /// <summary>To the left of the target.</summary>
    Left,

    /// <summary>Above the target.</summary>
    Above,

    /// <summary>Below the target.</summary>
    Below,

    /// <summary>Centered on the target with its own size.</summary>
    Center,
}

/// <summary>Alignment along the axis perpendicular to the placement direction.</summary>
public enum PlacementAlignment
{
    /// <summary>Top or left edges aligned.</summary>
    Start,

    /// <summary>Centered.</summary>
    Center,

    /// <summary>Bottom or right edges aligned.</summary>
    End,
}

/// <summary>How a placement handles targets that resolve to several rectangles (for example multi-line text).</summary>
public enum MultiRectMode
{
    /// <summary>Each rectangle is covered separately (masks only cover the matched text, not the gaps).</summary>
    Each,

    /// <summary>One overlay covers the union of all rectangles.</summary>
    Union,
}

/// <summary>
/// Placement relative to the target. Offsets, padding and sizes are in DIPs and are converted with the target
/// monitor's DPI, so the same rule looks the same at 100% and 200% scaling.
/// </summary>
public sealed record OverlayPlacement
{
    /// <summary>Placement mode.</summary>
    public PlacementMode Mode { get; init; } = PlacementMode.Cover;

    /// <summary>Horizontal offset in DIPs (positive = right).</summary>
    public double OffsetX { get; init; }

    /// <summary>Vertical offset in DIPs (positive = down).</summary>
    public double OffsetY { get; init; }

    /// <summary>Grows the target rectangle before placement (DIPs). Use it to cover focus rectangles or glyphs.</summary>
    public Insets Padding { get; init; }

    /// <summary>Explicit overlay width in DIPs for non-cover modes; when null, the content's desired width is used.</summary>
    public double? Width { get; init; }

    /// <summary>Explicit overlay height in DIPs for non-cover modes; when null, the content's desired height is used.</summary>
    public double? Height { get; init; }

    /// <summary>Cross-axis alignment for adjacent placements.</summary>
    public PlacementAlignment Alignment { get; init; } = PlacementAlignment.Center;

    /// <summary>Multi-rectangle handling. Default <see cref="MultiRectMode.Each"/> for cover, union otherwise.</summary>
    public MultiRectMode MultiRect { get; init; } = MultiRectMode.Each;

    /// <summary>Exactly covers the target.</summary>
    public static OverlayPlacement CoverTarget { get; } = new();

    /// <summary>Covers the target grown by the given padding (DIPs).</summary>
    public static OverlayPlacement Cover(Insets padding) => new() { Padding = padding };

    /// <summary>Covers the target grown uniformly (DIPs).</summary>
    public static OverlayPlacement Cover(double padding) => new() { Padding = Insets.Uniform(padding) };

    /// <summary>To the right of the target, vertically centered.</summary>
    public static OverlayPlacement Right(double offsetX = 8, double offsetY = 0) => new() { Mode = PlacementMode.Right, OffsetX = offsetX, OffsetY = offsetY, MultiRect = MultiRectMode.Union };

    /// <summary>To the left of the target, vertically centered.</summary>
    public static OverlayPlacement Left(double offsetX = -8, double offsetY = 0) => new() { Mode = PlacementMode.Left, OffsetX = offsetX, OffsetY = offsetY, MultiRect = MultiRectMode.Union };

    /// <summary>Above the target, horizontally aligned to its left edge.</summary>
    public static OverlayPlacement Above(double offsetX = 0, double offsetY = -8) => new() { Mode = PlacementMode.Above, OffsetX = offsetX, OffsetY = offsetY, Alignment = PlacementAlignment.Start, MultiRect = MultiRectMode.Union };

    /// <summary>Below the target, horizontally aligned to its left edge.</summary>
    public static OverlayPlacement Below(double offsetX = 0, double offsetY = 8) => new() { Mode = PlacementMode.Below, OffsetX = offsetX, OffsetY = offsetY, Alignment = PlacementAlignment.Start, MultiRect = MultiRectMode.Union };

    /// <summary>Centered on the target.</summary>
    public static OverlayPlacement Centered(double? width = null, double? height = null) => new() { Mode = PlacementMode.Center, Width = width, Height = height, MultiRect = MultiRectMode.Union };

    /// <summary>True when the overlay size is derived from the target rather than its content.</summary>
    public bool SizesToTarget => Mode == PlacementMode.Cover;
}

/// <summary>Pure placement math shared by every renderer.</summary>
public static class PlacementCalculator
{
    /// <summary>
    /// Computes overlay rectangles (physical screen pixels) for the resolved target rectangles.
    /// </summary>
    /// <param name="targets">Resolved target rectangles (physical screen pixels).</param>
    /// <param name="placement">Placement.</param>
    /// <param name="dpi">DPI of the target's monitor.</param>
    /// <param name="contentSize">Desired content size in physical pixels, used by non-cover modes when no explicit size is set.</param>
    public static IReadOnlyList<PixelRect> Calculate(IReadOnlyList<PixelRect> targets, OverlayPlacement placement, DpiScale dpi, PixelSize? contentSize = null)
    {
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(placement);
        var valid = targets.Where(t => !t.IsEmpty).ToArray();
        if (valid.Length == 0) return [];
        IEnumerable<PixelRect> sources = placement.MultiRect == MultiRectMode.Union || valid.Length == 1 ? [PixelRect.UnionAll(valid)] : valid;
        return sources.Select(t => Calculate(t, placement, dpi, contentSize)).Where(r => !r.IsEmpty).ToArray();
    }

    /// <summary>Computes the overlay rectangle for a single target rectangle.</summary>
    public static PixelRect Calculate(PixelRect target, OverlayPlacement placement, DpiScale dpi, PixelSize? contentSize = null)
    {
        ArgumentNullException.ThrowIfNull(placement);
        var (pl, pt, pr, pb) = CoordinateConverter.InsetsToPhysical(placement.Padding, dpi);
        var t = target.Inflate(pl, pt, pr, pb);
        int dx = CoordinateConverter.DipToPhysicalRounded(placement.OffsetX, dpi);
        int dy = CoordinateConverter.DipToPhysicalRounded(placement.OffsetY, dpi);
        if (placement.Mode == PlacementMode.Cover) return t.Offset(dx, dy);

        int w = placement.Width is { } pw ? CoordinateConverter.DipToPhysicalRounded(pw, dpi) : contentSize?.Width ?? t.Width;
        int h = placement.Height is { } ph ? CoordinateConverter.DipToPhysicalRounded(ph, dpi) : contentSize?.Height ?? t.Height;
        w = Math.Max(1, w); h = Math.Max(1, h);

        int AlignX() => placement.Alignment switch
        {
            PlacementAlignment.Start => t.X,
            PlacementAlignment.End => t.Right - w,
            _ => t.X + (t.Width - w) / 2,
        };
        int AlignY() => placement.Alignment switch
        {
            PlacementAlignment.Start => t.Y,
            PlacementAlignment.End => t.Bottom - h,
            _ => t.Y + (t.Height - h) / 2,
        };

        var r = placement.Mode switch
        {
            PlacementMode.Right => new PixelRect(t.Right, AlignY(), w, h),
            PlacementMode.Left => new PixelRect(t.X - w, AlignY(), w, h),
            PlacementMode.Above => new PixelRect(AlignX(), t.Y - h, w, h),
            PlacementMode.Below => new PixelRect(AlignX(), t.Bottom, w, h),
            _ => new PixelRect(t.X + (t.Width - w) / 2, t.Y + (t.Height - h) / 2, w, h),
        };
        return r.Offset(dx, dy);
    }
}
