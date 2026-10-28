using WindowVeil.Geometry;
using WindowVeil.Locators;

namespace WindowVeil.Editor.Model;

/// <summary>
/// Coordinate helpers for the picker and the rectangle tool. Everything WindowVeil stores is in PHYSICAL pixels; WPF
/// lays out in DIPs. Converting a cursor position with one DPI factor is wrong on mixed-DPI setups, so the editor reads
/// the cursor in physical pixels (GetCursorPos in a Per-Monitor-V2 process) and only uses DIPs for drawing feedback.
/// Pure logic, no WPF.
/// </summary>
public static class ScreenGeometry
{
    /// <summary>The normalized rectangle spanned by two corner points (order does not matter). Inclusive of both points.</summary>
    public static PixelRect FromCorners(PixelPoint a, PixelPoint b)
    {
        int left = Math.Min(a.X, b.X), top = Math.Min(a.Y, b.Y);
        int right = Math.Max(a.X, b.X), bottom = Math.Max(a.Y, b.Y);
        return PixelRect.FromLTRB(left, top, right + 1, bottom + 1);
    }

    /// <summary>Origin (physical screen pixels) of the given anchor.</summary>
    public static PixelPoint AnchorOrigin(PixelRect windowBounds, PixelRect clientBounds, WindowAnchor anchor) =>
        anchor == WindowAnchor.Client ? clientBounds.Location : windowBounds.Location;

    /// <summary>Converts a physical screen rectangle to one relative to the window or client origin.</summary>
    public static PixelRect ScreenToRelative(PixelRect screen, PixelRect windowBounds, PixelRect clientBounds, WindowAnchor anchor) =>
        CoordinateConverter.ScreenToRelative(screen, AnchorOrigin(windowBounds, clientBounds, anchor));

    /// <summary>Converts a window/client-relative rectangle to physical screen coordinates.</summary>
    public static PixelRect RelativeToScreen(PixelRect relative, PixelRect windowBounds, PixelRect clientBounds, WindowAnchor anchor) =>
        CoordinateConverter.RelativeToScreen(relative, AnchorOrigin(windowBounds, clientBounds, anchor));

    /// <summary>Re-expresses a relative rectangle for another anchor so it still covers the same screen area.</summary>
    public static PixelRect ChangeAnchor(PixelRect relative, WindowAnchor from, WindowAnchor to, PixelRect windowBounds, PixelRect clientBounds)
    {
        if (from == to) return relative;
        var screen = RelativeToScreen(relative, windowBounds, clientBounds, from);
        return ScreenToRelative(screen, windowBounds, clientBounds, to);
    }

    /// <summary>Physical pixels to DIPs for a given scale factor (1.0 = 96 DPI).</summary>
    public static RectD PhysicalToDip(PixelRect rect, double scale)
    {
        if (!(scale > 0)) scale = 1;
        return new RectD(rect.X / scale, rect.Y / scale, rect.Width / scale, rect.Height / scale);
    }

    /// <summary>DIPs to physical pixels (rounded outward so the rectangle never shrinks).</summary>
    public static PixelRect DipToPhysical(RectD rect, double scale)
    {
        if (!(scale > 0)) scale = 1;
        return PixelRect.FromRectDOutward(new RectD(rect.X * scale, rect.Y * scale, rect.Width * scale, rect.Height * scale));
    }

    /// <summary>Clamps a rectangle to a bounding rectangle; returns <see cref="PixelRect.Empty"/> when they do not overlap.</summary>
    public static PixelRect Clamp(PixelRect rect, PixelRect bounds)
    {
        var r = rect.Intersect(bounds);
        return r.IsEmpty ? PixelRect.Empty : r;
    }
}
