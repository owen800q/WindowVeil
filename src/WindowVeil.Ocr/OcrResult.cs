using WindowVeil.Geometry;

namespace WindowVeil.Ocr;

/// <summary>Outcome category of an OCR pass.</summary>
public enum OcrStatus
{
    /// <summary>Recognition ran. <see cref="OcrResult.Regions"/> may still be empty (no text in the image).</summary>
    Succeeded,

    /// <summary>The image (or the requested region of interest) cannot be recognized: empty, too large or outside the image.</summary>
    InvalidImage,

    /// <summary>The models could not be loaded, are corrupt, or do not belong together (for example dictionary/class mismatch).</summary>
    ModelError,

    /// <summary>Inference failed unexpectedly.</summary>
    Failed,
}

/// <summary>
/// One recognized text line.
/// </summary>
/// <param name="Text">Recognized text. Treat it as sensitive: never log it by default.</param>
/// <param name="Confidence">Recognition confidence in 0..1 (for PaddleOCR the mean CTC probability of the emitted characters).</param>
/// <param name="Polygon">
/// Quadrilateral around the text in <b>image-local pixel coordinates</b> of the recognized image (x right, y down, origin at the
/// top-left corner of the image). For PaddleOCR the points are ordered top-left, top-right, bottom-right, bottom-left
/// as seen in the text's reading direction.
/// </param>
/// <param name="BoundingBox">Axis-aligned bounding box of <paramref name="Polygon"/>, same coordinate space.</param>
public sealed record OcrTextRegion(string Text, float Confidence, IReadOnlyList<PointD> Polygon, RectD BoundingBox)
{
    /// <summary>Creates a region whose bounding box is computed from the polygon.</summary>
    public static OcrTextRegion FromPolygon(string text, float confidence, IReadOnlyList<PointD> polygon)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(polygon);
        return new OcrTextRegion(text, confidence, polygon, RectD.FromPoints(polygon));
    }

    /// <summary>Creates an axis-aligned region from a rectangle (image-local pixels).</summary>
    public static OcrTextRegion FromRect(string text, float confidence, RectD box) =>
        new(text, confidence, [new(box.X, box.Y), new(box.Right, box.Y), new(box.Right, box.Bottom), new(box.X, box.Bottom)], box);

    /// <summary>Returns a copy translated by (dx, dy) (for example from region-of-interest to full-image coordinates).</summary>
    public OcrTextRegion Offset(double dx, double dy) => this with
    {
        Polygon = Polygon.Select(p => new PointD(p.X + dx, p.Y + dy)).ToArray(),
        BoundingBox = BoundingBox with { X = BoundingBox.X + dx, Y = BoundingBox.Y + dy },
    };

    /// <summary>Diagnostic string without the recognized text (privacy).</summary>
    public override string ToString() => $"OcrTextRegion({Text.Length} chars, {Confidence:0.000}, {BoundingBox})";
}

/// <summary>
/// The result of <see cref="IOcrProvider.RecognizeAsync"/>. Failures are values, not exceptions; check <see cref="Status"/>.
/// </summary>
public sealed record OcrResult
{
    /// <summary>Outcome.</summary>
    public required OcrStatus Status { get; init; }

    /// <summary>Recognized lines in reading order (top-to-bottom, then left-to-right). Empty on failure.</summary>
    public IReadOnlyList<OcrTextRegion> Regions { get; init; } = [];

    /// <summary>Diagnostic message for failures. Never contains recognized text.</summary>
    public string? Error { get; init; }

    /// <summary>Exception behind <see cref="OcrStatus.ModelError"/> or <see cref="OcrStatus.Failed"/>, when there is one.</summary>
    public Exception? Exception { get; init; }

    /// <summary>Wall time spent in text detection (pre-processing, inference and post-processing).</summary>
    public TimeSpan DetectionTime { get; init; }

    /// <summary>Wall time spent cropping and recognizing the detected lines.</summary>
    public TimeSpan RecognitionTime { get; init; }

    /// <summary>Size of the recognized image.</summary>
    public PixelSize ImageSize { get; init; }

    /// <summary>Name of the provider that produced the result.</summary>
    public string? Provider { get; init; }

    /// <summary>True when <see cref="Status"/> is <see cref="OcrStatus.Succeeded"/>.</summary>
    public bool Succeeded => Status == OcrStatus.Succeeded;

    /// <summary>Creates a successful result.</summary>
    public static OcrResult Success(IReadOnlyList<OcrTextRegion> regions, PixelSize imageSize, TimeSpan detectionTime = default, TimeSpan recognitionTime = default, string? provider = null)
    {
        ArgumentNullException.ThrowIfNull(regions);
        return new OcrResult { Status = OcrStatus.Succeeded, Regions = regions, ImageSize = imageSize, DetectionTime = detectionTime, RecognitionTime = recognitionTime, Provider = provider };
    }

    /// <summary>Creates a failed result.</summary>
    public static OcrResult Failure(OcrStatus status, string error, PixelSize imageSize = default, Exception? exception = null, string? provider = null)
    {
        if (status == OcrStatus.Succeeded) throw new ArgumentException("Use Success for successful results.", nameof(status));
        ArgumentException.ThrowIfNullOrEmpty(error);
        return new OcrResult { Status = status, Error = error, ImageSize = imageSize, Exception = exception, Provider = provider };
    }

    /// <summary>Diagnostic string without recognized text.</summary>
    public override string ToString() => Succeeded
        ? $"OcrResult(Succeeded, {Regions.Count} regions, {ImageSize}, det {DetectionTime.TotalMilliseconds:0} ms, rec {RecognitionTime.TotalMilliseconds:0} ms)"
        : $"OcrResult({Status}: {Error})";
}
