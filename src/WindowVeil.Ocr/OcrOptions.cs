using WindowVeil.Geometry;
using WindowVeil.Imaging;

namespace WindowVeil.Ocr;

/// <summary>Per-call recognition options. Immutable; compare by value (used as part of cache keys).</summary>
public sealed record OcrOptions
{
    /// <summary>Default options.</summary>
    public static OcrOptions Default { get; } = new();

    /// <summary>
    /// Recognized lines with a confidence below this value are dropped by the provider. Default 0, which matches the
    /// official PaddleOCR pipeline default (<c>text_rec_score_thresh = 0.0</c>): every detected line is returned and callers
    /// such as <see cref="OcrTextMatcher"/> apply their own threshold, so they can tell "not found" from "found with low confidence".
    /// </summary>
    public float MinConfidence { get; init; }

    /// <summary>
    /// Optional region of interest in image-local pixels. Only this part of the image is recognized, but returned
    /// geometry is still relative to the full image. Fractional edges are rounded outward; the region is clipped to the image.
    /// </summary>
    public RectD? RegionOfInterest { get; init; }

    /// <summary>
    /// Upper bound on the number of pixels of the (cropped) input, guarding against accidental multi-monitor captures.
    /// Larger inputs return <see cref="OcrStatus.InvalidImage"/>. Default 40 megapixels.
    /// </summary>
    public long MaxImagePixels { get; init; } = 40_000_000;

    /// <summary>
    /// Validates <paramref name="image"/> against these options and returns the pixel rectangle to recognize, or an error.
    /// Providers call this before doing any work.
    /// </summary>
    /// <returns>The clipped region of interest (the whole image when none is set), or <see langword="null"/> with <paramref name="error"/> set.</returns>
    public PixelRect? Validate(BgraImage image, out string? error)
    {
        ArgumentNullException.ThrowIfNull(image);
        error = null;
        if (float.IsNaN(MinConfidence) || MinConfidence < 0 || MinConfidence > 1)
        {
            error = $"MinConfidence must be within 0..1 (got {MinConfidence}).";
            return null;
        }
        var full = new PixelRect(0, 0, image.Width, image.Height);
        var region = full;
        if (RegionOfInterest is { } roi)
        {
            if (double.IsNaN(roi.X) || double.IsNaN(roi.Y) || double.IsNaN(roi.Width) || double.IsNaN(roi.Height) || roi.IsEmpty)
            {
                error = $"The region of interest {roi} is empty or invalid.";
                return null;
            }
            region = PixelRect.FromRectDOutward(roi).Intersect(full);
            if (region.IsEmpty)
            {
                error = $"The region of interest {roi} does not intersect the {image.Width}x{image.Height} image.";
                return null;
            }
        }
        if (region.Area > MaxImagePixels)
        {
            error = $"The image region {region.Width}x{region.Height} exceeds the limit of {MaxImagePixels} pixels.";
            return null;
        }
        return region;
    }
}
