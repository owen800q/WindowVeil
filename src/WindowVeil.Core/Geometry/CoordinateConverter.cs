namespace WindowVeil.Geometry;

/// <summary>
/// Central place for every coordinate-space conversion used by WindowVeil.
/// <list type="bullet">
/// <item><b>Screen</b>: physical pixels on the virtual desktop (default space, may be negative).</item>
/// <item><b>Window</b>: physical pixels relative to the target window's outer top-left corner.</item>
/// <item><b>Client</b>: physical pixels relative to the target window's client-area top-left corner.</item>
/// <item><b>DIP</b>: WPF device-independent pixels (1/96 inch), relative to the same origin.</item>
/// <item><b>Image</b>: pixels in a captured bitmap; mapped to screen by the capture origin and scale.</item>
/// </list>
/// Conversions never happen implicitly: each method names its source and destination space.
/// </summary>
public static class CoordinateConverter
{
    /// <summary>Converts a physical length to DIPs.</summary>
    public static double PhysicalToDip(double physical, DpiScale dpi) => physical / dpi.Scale;

    /// <summary>Converts a DIP length to physical pixels (not rounded).</summary>
    public static double DipToPhysical(double dip, DpiScale dpi) => dip * dpi.Scale;

    /// <summary>Converts a DIP length to whole physical pixels, rounding to nearest.</summary>
    public static int DipToPhysicalRounded(double dip, DpiScale dpi) => (int)Math.Round(dip * dpi.Scale, MidpointRounding.AwayFromZero);

    /// <summary>Converts a physical screen rectangle to DIPs (same origin, scaled).</summary>
    public static RectD ScreenToDip(PixelRect rect, DpiScale dpi) =>
        new(rect.X / dpi.Scale, rect.Y / dpi.Scale, rect.Width / dpi.Scale, rect.Height / dpi.Scale);

    /// <summary>Converts a DIP rectangle to physical pixels, rounding outward so content is never clipped.</summary>
    public static PixelRect DipToScreen(RectD rect, DpiScale dpi) =>
        PixelRect.FromRectDOutward(new RectD(rect.X * dpi.Scale, rect.Y * dpi.Scale, rect.Width * dpi.Scale, rect.Height * dpi.Scale));

    /// <summary>Converts a rectangle relative to <paramref name="origin"/> (window or client space) to screen space.</summary>
    public static PixelRect RelativeToScreen(PixelRect relative, PixelPoint origin) => relative.Offset(origin.X, origin.Y);

    /// <summary>Converts a screen rectangle to a rectangle relative to <paramref name="origin"/>.</summary>
    public static PixelRect ScreenToRelative(PixelRect screen, PixelPoint origin) => screen.Offset(-origin.X, -origin.Y);

    /// <summary>
    /// Maps a normalized rectangle (0..1 on each axis) onto <paramref name="reference"/> (screen space).
    /// </summary>
    public static PixelRect NormalizedToScreen(RectD normalized, PixelRect reference)
    {
        var r = new RectD(
            reference.X + normalized.X * reference.Width,
            reference.Y + normalized.Y * reference.Height,
            normalized.Width * reference.Width,
            normalized.Height * reference.Height);
        return new PixelRect((int)Math.Round(r.X), (int)Math.Round(r.Y), (int)Math.Round(r.Width), (int)Math.Round(r.Height));
    }

    /// <summary>
    /// Maps an image-local point to screen space. <paramref name="imageOrigin"/> is the screen position of image pixel (0,0);
    /// <paramref name="screenPixelsPerImagePixel"/> is 1 unless the capture was scaled.
    /// </summary>
    public static PointD ImageToScreen(PointD imagePoint, PixelPoint imageOrigin, double screenPixelsPerImagePixel = 1.0) =>
        new(imageOrigin.X + imagePoint.X * screenPixelsPerImagePixel, imageOrigin.Y + imagePoint.Y * screenPixelsPerImagePixel);

    /// <summary>Maps an image-local rectangle to a screen rectangle, rounding outward.</summary>
    public static PixelRect ImageToScreen(RectD imageRect, PixelPoint imageOrigin, double screenPixelsPerImagePixel = 1.0)
    {
        var tl = ImageToScreen(new PointD(imageRect.X, imageRect.Y), imageOrigin, screenPixelsPerImagePixel);
        return PixelRect.FromRectDOutward(new RectD(tl.X, tl.Y, imageRect.Width * screenPixelsPerImagePixel, imageRect.Height * screenPixelsPerImagePixel));
    }

    /// <summary>Converts DIP insets to physical pixel insets (rounded to nearest).</summary>
    public static (int Left, int Top, int Right, int Bottom) InsetsToPhysical(Insets insets, DpiScale dpi) =>
        (DipToPhysicalRounded(insets.Left, dpi), DipToPhysicalRounded(insets.Top, dpi),
         DipToPhysicalRounded(insets.Right, dpi), DipToPhysicalRounded(insets.Bottom, dpi));
}
