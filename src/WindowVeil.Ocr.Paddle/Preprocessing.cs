namespace WindowVeil.Ocr.Paddle;

/// <summary>How the detection input is scaled (PaddleX <c>DetResizeForTest</c> <c>limit_type</c>).</summary>
public enum DetectionLimitType
{
    /// <summary>Upscale so the shorter side is at least the limit (<c>"min"</c>; official OCR pipeline default with limit 64).</summary>
    Min,

    /// <summary>Downscale so the longer side is at most the limit (<c>"max"</c>).</summary>
    Max,

    /// <summary>Scale so the longer side equals the limit (<c>"resize_long"</c>).</summary>
    ResizeLong,
}

/// <summary>Result of <see cref="DetectionPreprocessor.ComputeResize"/>.</summary>
/// <param name="Width">Network input width (multiple of 32).</param>
/// <param name="Height">Network input height (multiple of 32).</param>
/// <param name="PaddedWidth">Width after the official small-image padding (equal to the source width unless width + height &lt; 64).</param>
/// <param name="PaddedHeight">Height after padding.</param>
public readonly record struct DetectionResize(int Width, int Height, int PaddedWidth, int PaddedHeight);

/// <summary>Normalization constants (<c>NormalizeImage</c> in the detection inference.yml).</summary>
/// <param name="Mean">Per-channel mean, applied to channels in image order (B, G, R).</param>
/// <param name="Std">Per-channel standard deviation.</param>
/// <param name="Scale">Pixel scale, usually 1/255.</param>
public sealed record DetectionNormalization(double[] Mean, double[] Std, double Scale)
{
    /// <summary>The ImageNet constants used by PP-OCR detection models.</summary>
    public static DetectionNormalization ImageNet { get; } = new([0.485, 0.456, 0.406], [0.229, 0.224, 0.225], 1.0 / 255.0);
}

/// <summary>
/// Detection pre-processing (PaddleX <c>DetResizeForTest</c> type 0 + <c>NormalizeImage</c> + <c>ToCHWImage</c>).
/// </summary>
/// <remarks>
/// The official pipeline works on BGR images and applies <c>mean[0]</c>/<c>std[0]</c> to channel 0 (blue), even though the
/// constants are the ImageNet RGB values; this is reproduced as is. Normalization runs in float32: <c>x * (scale/std) +
/// (-mean/std)</c>, matching NumPy's float32 arithmetic.
/// </remarks>
public static class DetectionPreprocessor
{
    /// <summary>Computes the network input size for a source image (official <c>resize_image_type0</c>).</summary>
    public static DetectionResize ComputeResize(int width, int height, int limitSideLength, DetectionLimitType limitType, int maxSideLimit)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limitSideLength);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxSideLimit);
        int h = height, w = width;
        if (h + w < 64) { h = Math.Max(32, h); w = Math.Max(32, w); }
        double ratio = limitType switch
        {
            DetectionLimitType.Max => Math.Max(h, w) > limitSideLength ? (double)limitSideLength / (h > w ? h : w) : 1.0,
            DetectionLimitType.Min => Math.Min(h, w) < limitSideLength ? (double)limitSideLength / (h < w ? h : w) : 1.0,
            DetectionLimitType.ResizeLong => (double)limitSideLength / Math.Max(h, w),
            _ => throw new ArgumentOutOfRangeException(nameof(limitType)),
        };
        int rh = (int)(h * ratio), rw = (int)(w * ratio);
        if (Math.Max(rh, rw) > maxSideLimit)
        {
            ratio = (double)maxSideLimit / Math.Max(rh, rw);
            rh = (int)(rh * ratio);
            rw = (int)(rw * ratio);
        }
        rh = Math.Max((int)(Math.Round(rh / 32.0, MidpointRounding.ToEven) * 32), 32);
        rw = Math.Max((int)(Math.Round(rw / 32.0, MidpointRounding.ToEven) * 32), 32);
        return new DetectionResize(rw, rh, w, h);
    }

    /// <summary>Resizes and normalizes an image into a [1, 3, H, W] float32 tensor buffer.</summary>
    internal static float[] Prepare(BgrImage image, DetectionResize size, DetectionNormalization norm)
    {
        var padded = image.PadTo(size.PaddedWidth, size.PaddedHeight);
        var resized = size.Width == padded.Width && size.Height == padded.Height ? padded : ImageOps.ResizeLinear(padded, size.Width, size.Height);
        return Normalize(resized, norm);
    }

    /// <summary>HWC uint8 (BGR) → CHW float32 with per-channel <c>x * alpha + beta</c>.</summary>
    internal static float[] Normalize(BgrImage img, DetectionNormalization norm)
    {
        int plane = img.Width * img.Height;
        var tensor = new float[3 * plane];
        Span<float> alpha = stackalloc float[3];
        Span<float> beta = stackalloc float[3];
        for (int c = 0; c < 3; c++)
        {
            alpha[c] = (float)(norm.Scale / norm.Std[c]);
            beta[c] = (float)(-norm.Mean[c] / norm.Std[c]);
        }
        var d = img.Data;
        float a0 = alpha[0], a1 = alpha[1], a2 = alpha[2], b0 = beta[0], b1 = beta[1], b2 = beta[2];
        int w = img.Width;
        ImageOps.ForRows(img.Height, plane >= ImageOps.ParallelPixelThreshold, y =>
        {
            for (int i = y * w, end = i + w; i < end; i++)
            {
                // Two separate float32 operations (multiply, then add), like NumPy's in-place *= and +=.
                float v0 = d[i * 3] * a0, v1 = d[i * 3 + 1] * a1, v2 = d[i * 3 + 2] * a2;
                tensor[i] = v0 + b0;
                tensor[plane + i] = v1 + b1;
                tensor[2 * plane + i] = v2 + b2;
            }
        });
        return tensor;
    }
}

/// <summary>
/// Recognition pre-processing (PaddleX <c>OCRReisizeNormImg.resize</c> + <c>ToBatch</c>): every crop is resized to the model
/// height keeping its aspect ratio (width capped at <c>int(H × max(W0/H, w/h))</c> and at 3200), scaled to
/// <c>(x/255 − 0.5)/0.5</c> and zero-padded on the right; the batch is padded to its widest member.
/// </summary>
public static class RecognitionPreprocessor
{
    /// <summary>Official maximum recognition input width (<c>max_imgW</c>).</summary>
    public const int MaxWidth = 3200;

    /// <summary>Computes (resized width, padded width) for a crop of <paramref name="width"/> × <paramref name="height"/>.</summary>
    /// <param name="width">Crop width.</param>
    /// <param name="height">Crop height.</param>
    /// <param name="imageHeight">Model input height (48 for PP-OCRv6).</param>
    /// <param name="imageWidth">Nominal model input width from the config (320).</param>
    public static (int ResizedWidth, int PaddedWidth) ComputeWidths(int width, int height, int imageHeight, int imageWidth)
    {
        double maxWhRatio = Math.Max((double)imageWidth / imageHeight, width * 1.0 / height);
        int imgW = (int)(imageHeight * maxWhRatio);
        if (imgW > MaxWidth) return (MaxWidth, MaxWidth);
        double ratio = width / (double)height;
        int resizedW = Math.Ceiling(imageHeight * ratio) > imgW ? imgW : (int)Math.Ceiling(imageHeight * ratio);
        return (resizedW, imgW);
    }

    /// <summary>Builds the [N, 3, H, Wmax] batch tensor for the crops.</summary>
    internal static (float[] Data, int Width) PrepareBatch(IReadOnlyList<BgrImage> crops, int imageHeight, int imageWidth)
    {
        var resized = new List<(BgrImage Img, int PaddedWidth)>(crops.Count);
        foreach (var crop in crops)
        {
            var (rw, pw) = ComputeWidths(crop.Width, crop.Height, imageHeight, imageWidth);
            resized.Add((ImageOps.ResizeLinear(crop, rw, imageHeight), pw));
        }
        int batchWidth = resized.Max(r => r.PaddedWidth);
        int plane = imageHeight * batchWidth;
        var data = new float[crops.Count * 3 * plane];
        for (int n = 0; n < resized.Count; n++)
        {
            var img = resized[n].Img;
            int baseN = n * 3 * plane;
            for (int y = 0; y < imageHeight; y++)
            {
                for (int x = 0; x < img.Width; x++)
                {
                    int s = (y * img.Width + x) * 3;
                    for (int c = 0; c < 3; c++)
                    {
                        float v = img.Data[s + c] / 255f;
                        v -= 0.5f;
                        v /= 0.5f;
                        data[baseN + c * plane + y * batchWidth + x] = v;
                    }
                }
            }
        }
        return (data, batchWidth);
    }
}
