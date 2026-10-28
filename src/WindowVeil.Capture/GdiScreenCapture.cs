using WindowVeil.Platform;

namespace WindowVeil.Capture;

/// <summary>
/// GDI capture backend.
/// <list type="bullet">
/// <item><see cref="CaptureSource.Desktop"/>: <c>BitBlt</c> of the screen DC with <c>CAPTUREBLT</c> — the composed desktop,
/// including layered windows such as WindowVeil overlays (unless they use
/// <c>WDA_EXCLUDEFROMCAPTURE</c>).</item>
/// <item><see cref="CaptureSource.Window"/>: <c>PrintWindow(PW_RENDERFULLCONTENT)</c> — only the target's own pixels, so OCR
/// never recognizes WindowVeil's masks. It never silently falls back to desktop capture: failures are reported as
/// <see cref="CaptureError.Failed"/>, <see cref="CaptureError.WindowUnavailable"/> (destroyed or minimized) or
/// <see cref="CaptureError.EmptyContent"/> (uniform/black output, typical of protected content).</item>
/// </list>
/// All coordinates are physical virtual-desktop pixels; capture runs in a per-monitor-v2 DPI context on a thread-pool
/// thread. Thread-safe and stateless.
/// </summary>
public sealed class GdiScreenCapture : IScreenCapture
{
    /// <inheritdoc />
    public bool Supports(CaptureSource source) => source is CaptureSource.Desktop or CaptureSource.Window;

    /// <inheritdoc />
    public ValueTask<CaptureResult> CaptureAsync(CaptureRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (cancellationToken.IsCancellationRequested) return ValueTask.FromCanceled<CaptureResult>(cancellationToken);
        return new ValueTask<CaptureResult>(Task.Run(() => Capture(request), cancellationToken));
    }

    /// <summary>Synchronous capture on the calling thread.</summary>
    public CaptureResult Capture(CaptureRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            return request.Source switch
            {
                CaptureSource.Desktop => GdiCapture.CaptureDesktop(request.Region),
                CaptureSource.Window => GdiCapture.CaptureWindow(request.Window, request.Region),
                _ => CaptureResult.Failure(CaptureError.Unsupported, $"Capture source {request.Source} is not supported by GDI capture."),
            };
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return CaptureResult.Failure(CaptureError.Failed, $"GDI capture failed: {ex.Message}");
        }
    }
}
