using WindowVeil.Geometry;
using WindowVeil.Imaging;

namespace WindowVeil.RetroCapDemo.Evidence;

/// <summary>Region-level pixel comparisons used as visual assertions on real screenshots.</summary>
public static class ImageAnalysis
{
    /// <summary>Fraction (0..1) of pixels inside <paramref name="screenRect"/> whose color is within <paramref name="tolerance"/> of <paramref name="color"/>.</summary>
    public static double FractionNear(BgraImage image, PixelRect imageOrigin, PixelRect screenRect, OverlayColor color, int tolerance)
    {
        var r = ToImage(image, imageOrigin, screenRect);
        if (r.IsEmpty) return 0;
        long hits = 0;
        for (int y = r.Y; y < r.Bottom; y++)
            for (int x = r.X; x < r.Right; x++)
                if (image.GetPixel(x, y).MaxChannelDifference(color) <= tolerance) hits++;
        return (double)hits / r.Area;
    }

    /// <summary>Fraction (0..1) of pixels in the region that are equal (within tolerance) in both images.</summary>
    public static double Similarity(BgraImage a, BgraImage b, PixelRect imageOrigin, PixelRect screenRect, int tolerance)
    {
        var ra = ToImage(a, imageOrigin, screenRect);
        var rb = ToImage(b, imageOrigin, screenRect);
        if (ra.IsEmpty || ra != rb) return 0;
        long same = 0;
        for (int y = ra.Y; y < ra.Bottom; y++)
            for (int x = ra.X; x < ra.Right; x++)
                if (a.GetPixel(x, y).MaxChannelDifference(b.GetPixel(x, y)) <= tolerance) same++;
        return (double)same / ra.Area;
    }

    /// <summary>Number of distinct colors (quantized to 4 bits per channel) in a region — a cheap "has content" measure.</summary>
    public static int DistinctColors(BgraImage image, PixelRect imageOrigin, PixelRect screenRect)
    {
        var r = ToImage(image, imageOrigin, screenRect);
        var set = new HashSet<int>();
        for (int y = r.Y; y < r.Bottom; y++)
            for (int x = r.X; x < r.Right; x++)
            {
                var c = image.GetPixel(x, y);
                set.Add((c.R >> 4) << 8 | (c.G >> 4) << 4 | c.B >> 4);
            }
        return set.Count;
    }

    /// <summary>Converts a screen rectangle to image coordinates given the screen rectangle the image covers.</summary>
    public static PixelRect ToImage(BgraImage image, PixelRect imageOrigin, PixelRect screenRect) =>
        screenRect.Offset(-imageOrigin.X, -imageOrigin.Y).Intersect(new PixelRect(0, 0, image.Width, image.Height));
}
