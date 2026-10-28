using WindowVeil.Imaging;

namespace WindowVeil.Ocr;

/// <summary>
/// An OCR engine: recognizes text lines in a <see cref="BgraImage"/>. Implementations must be thread-safe and should
/// keep expensive state (models, sessions) alive across calls. Expected failures (unreadable model, invalid image) are
/// reported through <see cref="OcrResult.Status"/>; only cancellation is signalled by throwing
/// <see cref="OperationCanceledException"/>.
/// </summary>
/// <remarks>
/// All geometry in the result is in <b>image-local pixel coordinates</b> of the image passed in: (0,0) is the top-left
/// corner of its top-left pixel, x grows to the right and y downwards. Mapping to screen coordinates is the caller's job
/// (see <see cref="OcrLocatorResolver"/>, which uses <c>CapturedFrame.ImageToScreen</c>). Implementations must not log
/// or persist recognized text.
/// </remarks>
public interface IOcrProvider
{
    /// <summary>Short provider name for diagnostics, e.g. <c>PaddleOCR PP-OCRv6_small</c>.</summary>
    string Name { get; }

    /// <summary>Recognizes the text lines in <paramref name="image"/>.</summary>
    /// <param name="image">Pixels to recognize. The image is only read, never modified or retained.</param>
    /// <param name="options">Recognition options; <see langword="null"/> uses <see cref="OcrOptions.Default"/>.</param>
    /// <param name="cancellationToken">Cancels the recognition; checked between pipeline stages.</param>
    /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
    ValueTask<OcrResult> RecognizeAsync(BgraImage image, OcrOptions? options = null, CancellationToken cancellationToken = default);
}
