namespace WindowVeil.Ocr;

/// <summary>Options for <see cref="OcrLocatorResolver"/>.</summary>
public sealed record OcrLocatorOptions
{
    /// <summary>
    /// Maximum number of OCR inferences running at the same time across every locator served by the resolver. Default 1:
    /// OCR is CPU-heavy and the provider already parallelizes inside one inference.
    /// </summary>
    public int MaxConcurrentRecognitions { get; init; } = 1;

    /// <summary>
    /// Maximum number of requests waiting for an inference slot. Requests beyond this keep their previous geometry
    /// (<c>Cached</c>) or fail with <c>Unavailable</c> ("OCR is busy"). Default 32.
    /// </summary>
    public int MaxQueuedRecognitions { get; init; } = 32;

    /// <summary>
    /// Number of OCR results kept in the content cache, keyed by an XxHash64 of the captured pixels plus image size and
    /// recognition options. An unchanged window then costs one capture and one hash. Default 16; 0 disables the cache.
    /// </summary>
    public int ResultCacheCapacity { get; init; } = 16;

    /// <summary>
    /// Whether a desktop capture may be used when the backend cannot capture the target window by itself. Default false.
    /// </summary>
    /// <remarks>
    /// A desktop capture contains everything composed on screen, including WindowVeil's own overlay windows. OCR would then
    /// read the masks instead of the text underneath, lose the match, remove the mask, find the text again on the next pass,
    /// and so on: a feedback loop that makes masks flicker and briefly exposes the protected text. WindowVeil cannot remove
    /// its overlays from such a capture after the fact (the <c>OverlayWindowRegistry</c> knows the overlay handles, but DWM has
    /// already composed their pixels into the image), so the safe default is to report <c>Unavailable</c> instead.
    /// Enable this only when overlays are excluded from capture by other means (for example
    /// <c>WDA_EXCLUDEFROMCAPTURE</c>) or when the located text is never covered by an overlay.
    /// </remarks>
    public bool AllowDesktopCaptureFallback { get; init; }

    /// <summary>
    /// Whether resolution messages may include recognized text. Default false: messages contain only the query pattern,
    /// counts and confidences, because recognized text is often exactly the sensitive data being masked.
    /// </summary>
    public bool IncludeRecognizedTextInDiagnostics { get; init; }

    /// <summary>Options passed to the provider for every inference.</summary>
    public OcrOptions Recognition { get; init; } = OcrOptions.Default;

    /// <summary>Text matching options (phrase joining, geometry).</summary>
    public OcrTextMatcherOptions Matching { get; init; } = new();

    /// <summary>Whether <see cref="OcrWindowVeilBuilderExtensions.UseOcr"/> registers the provider for disposal with the engine. Default true.</summary>
    public bool DisposeProviderWithEngine { get; init; } = true;

    internal void Validate()
    {
        if (MaxConcurrentRecognitions < 1) throw new ArgumentOutOfRangeException(nameof(MaxConcurrentRecognitions), MaxConcurrentRecognitions, "Must be at least 1.");
        if (MaxQueuedRecognitions < 0) throw new ArgumentOutOfRangeException(nameof(MaxQueuedRecognitions), MaxQueuedRecognitions, "Must not be negative.");
        if (ResultCacheCapacity < 0) throw new ArgumentOutOfRangeException(nameof(ResultCacheCapacity), ResultCacheCapacity, "Must not be negative.");
        ArgumentNullException.ThrowIfNull(Recognition);
        ArgumentNullException.ThrowIfNull(Matching);
    }
}
