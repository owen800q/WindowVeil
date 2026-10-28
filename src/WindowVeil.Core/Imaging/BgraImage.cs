using WindowVeil.Geometry;

namespace WindowVeil.Imaging;

/// <summary>
/// An owned, CPU-side 32-bit BGRA bitmap (8 bits per channel, byte order B, G, R, A).
/// This is the interchange format between capture backends and OCR providers, so neither needs
/// a dependency on WPF, GDI+ or an image library. The instance owns its pixel buffer.
/// </summary>
public sealed class BgraImage
{
    /// <summary>Creates an image over an existing buffer. The buffer is not copied; the image takes ownership.</summary>
    public BgraImage(int width, int height, byte[] pixels, int? stride = null)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width), width, "Width must be positive.");
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height), height, "Height must be positive.");
        int s = stride ?? width * 4;
        if (s < width * 4) throw new ArgumentOutOfRangeException(nameof(stride), s, "Stride is smaller than width * 4.");
        if (pixels.Length < (long)s * height) throw new ArgumentException("Pixel buffer is too small for the given dimensions.", nameof(pixels));
        Width = width;
        Height = height;
        Stride = s;
        Pixels = pixels;
    }

    /// <summary>Creates a zero-initialized (transparent black) image.</summary>
    public BgraImage(int width, int height) : this(width, height, Allocate(width, height)) { }

    // Validate before allocating so invalid sizes raise ArgumentOutOfRangeException rather than OverflowException.
    private static byte[] Allocate(int width, int height)
    {
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width), width, "Width must be positive.");
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height), height, "Height must be positive.");
        return new byte[checked(width * 4 * height)];
    }

    /// <summary>Width in pixels.</summary>
    public int Width { get; }

    /// <summary>Height in pixels.</summary>
    public int Height { get; }

    /// <summary>Bytes per row.</summary>
    public int Stride { get; }

    /// <summary>Raw pixel data, row-major, top-down.</summary>
    public byte[] Pixels { get; }

    /// <summary>Image size.</summary>
    public PixelSize Size => new(Width, Height);

    /// <summary>Reads one pixel.</summary>
    public OverlayColor GetPixel(int x, int y)
    {
        if ((uint)x >= (uint)Width || (uint)y >= (uint)Height) throw new ArgumentOutOfRangeException(nameof(x), "Pixel is outside the image.");
        int i = y * Stride + x * 4;
        return new OverlayColor(Pixels[i + 3], Pixels[i + 2], Pixels[i + 1], Pixels[i]);
    }

    /// <summary>Writes one pixel.</summary>
    public void SetPixel(int x, int y, OverlayColor color)
    {
        if ((uint)x >= (uint)Width || (uint)y >= (uint)Height) throw new ArgumentOutOfRangeException(nameof(x), "Pixel is outside the image.");
        int i = y * Stride + x * 4;
        Pixels[i] = color.B; Pixels[i + 1] = color.G; Pixels[i + 2] = color.R; Pixels[i + 3] = color.A;
    }

    /// <summary>Fills a rectangle (clipped to the image) with a color.</summary>
    public void FillRect(PixelRect rect, OverlayColor color)
    {
        var r = rect.Intersect(new PixelRect(0, 0, Width, Height));
        for (int y = r.Y; y < r.Bottom; y++)
            for (int x = r.X; x < r.Right; x++)
                SetPixel(x, y, color);
    }

    /// <summary>Copies a sub-rectangle into a new image. The rectangle is clipped to the image bounds.</summary>
    public BgraImage Crop(PixelRect rect)
    {
        var r = rect.Intersect(new PixelRect(0, 0, Width, Height));
        if (r.IsEmpty) throw new ArgumentException($"Crop rectangle {rect} does not intersect the {Width}x{Height} image.", nameof(rect));
        var result = new BgraImage(r.Width, r.Height);
        for (int y = 0; y < r.Height; y++)
            Buffer.BlockCopy(Pixels, (r.Y + y) * Stride + r.X * 4, result.Pixels, y * result.Stride, r.Width * 4);
        return result;
    }

    /// <summary>Copies another image into this one at the given position (clipped).</summary>
    public void Blit(BgraImage source, int destX, int destY)
    {
        ArgumentNullException.ThrowIfNull(source);
        var dest = new PixelRect(destX, destY, source.Width, source.Height).Intersect(new PixelRect(0, 0, Width, Height));
        for (int y = dest.Y; y < dest.Bottom; y++)
            Buffer.BlockCopy(source.Pixels, (y - destY) * source.Stride + (dest.X - destX) * 4, Pixels, y * Stride + dest.X * 4, dest.Width * 4);
    }

    /// <summary>Deep copy.</summary>
    public BgraImage Clone() => new(Width, Height, (byte[])Pixels.Clone(), Stride);
}
