using WindowVeil.Geometry;
using WindowVeil.Imaging;

namespace WindowVeil.Platform;

/// <summary>Where captured pixels come from.</summary>
public enum CaptureSource
{
    /// <summary>
    /// The target window's own content (for example <c>PrintWindow</c>). Independent overlay windows are not
    /// included, which prevents OCR from recognizing its own masks.
    /// </summary>
    Window,

    /// <summary>The composed desktop, including every visible window and overlay.</summary>
    Desktop,
}

/// <summary>A capture request. <see cref="Region"/> is in physical screen coordinates.</summary>
public sealed record CaptureRequest(PixelRect Region, CaptureSource Source)
{
    /// <summary>Target window; required for <see cref="CaptureSource.Window"/>.</summary>
    public TargetWindowInfo? Window { get; init; }
}

/// <summary>Reasons a capture can fail.</summary>
public enum CaptureError
{
    /// <summary>No error.</summary>
    None,

    /// <summary>The requested source is not supported by this backend or OS version.</summary>
    Unsupported,

    /// <summary>The window no longer exists or is minimized.</summary>
    WindowUnavailable,

    /// <summary>The OS refused the capture (protected content, secure desktop, display affinity).</summary>
    AccessDenied,

    /// <summary>The capture succeeded technically but returned no usable content (for example all black).</summary>
    EmptyContent,

    /// <summary>The region is empty or outside every monitor.</summary>
    InvalidRegion,

    /// <summary>An unexpected backend failure.</summary>
    Failed,
}

/// <summary>Captured pixels plus the geometry needed to map them back to the screen.</summary>
public sealed record CapturedFrame(BgraImage Image, PixelRect ScreenRegion, CaptureSource Source)
{
    /// <summary>Screen pixels per image pixel (1 unless the backend scaled the capture).</summary>
    public double Scale => Image.Width == 0 ? 1 : (double)ScreenRegion.Width / Image.Width;

    /// <summary>When the frame was captured (UTC).</summary>
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Maps an image-local rectangle to screen coordinates.</summary>
    public PixelRect ImageToScreen(RectD imageRect) => CoordinateConverter.ImageToScreen(imageRect, ScreenRegion.Location, Scale);
}

/// <summary>Result of a capture: either a frame or an explicit error.</summary>
public sealed record CaptureResult
{
    private CaptureResult(CapturedFrame? frame, CaptureError error, string? message)
    {
        Frame = frame; Error = error; Message = message;
    }

    /// <summary>The frame when successful.</summary>
    public CapturedFrame? Frame { get; }

    /// <summary>Error code (<see cref="CaptureError.None"/> on success).</summary>
    public CaptureError Error { get; }

    /// <summary>Human-readable diagnostic.</summary>
    public string? Message { get; }

    /// <summary>True when a frame is available.</summary>
    public bool Succeeded => Frame is not null;

    /// <summary>Creates a successful result.</summary>
    public static CaptureResult Success(CapturedFrame frame) => new(frame ?? throw new ArgumentNullException(nameof(frame)), CaptureError.None, null);

    /// <summary>Creates a failed result.</summary>
    public static CaptureResult Failure(CaptureError error, string message) => new(null, error, message);
}

/// <summary>Pluggable screen/window capture backend. Implementations must be thread-safe.</summary>
public interface IScreenCapture
{
    /// <summary>Whether the backend can serve the given source on this machine.</summary>
    bool Supports(CaptureSource source);

    /// <summary>Captures pixels. Never throws for expected failures; returns <see cref="CaptureResult.Failure"/> instead.</summary>
    ValueTask<CaptureResult> CaptureAsync(CaptureRequest request, CancellationToken cancellationToken = default);
}
