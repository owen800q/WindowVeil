namespace WindowVeil;

/// <summary>Machine-readable error categories.</summary>
public enum WindowVeilErrorCode
{
    /// <summary>Unspecified failure.</summary>
    Unknown,

    /// <summary>No window matched the selector.</summary>
    TargetWindowNotFound,

    /// <summary>A window handle is invalid or was destroyed.</summary>
    InvalidWindowHandle,

    /// <summary>The target process exited.</summary>
    TargetProcessExited,

    /// <summary>UI Automation is unavailable for the target.</summary>
    UiAutomationUnavailable,

    /// <summary>The element could not be found.</summary>
    ElementNotFound,

    /// <summary>OCR found text but below the confidence threshold.</summary>
    OcrConfidenceTooLow,

    /// <summary>OCR is not configured or failed.</summary>
    OcrUnavailable,

    /// <summary>Screen capture is unavailable or was refused.</summary>
    CaptureUnavailable,

    /// <summary>The WPF dispatcher is unavailable or failed.</summary>
    WpfDispatcherFailure,

    /// <summary>An overlay window could not be created.</summary>
    OverlayCreationFailed,

    /// <summary>No renderer supports the appearance.</summary>
    RendererNotFound,

    /// <summary>The rule is invalid.</summary>
    InvalidRule,

    /// <summary>The operation is invalid in the current state.</summary>
    InvalidState,

    /// <summary>A required platform service is missing (for example running without the Windows packages).</summary>
    PlatformUnavailable,
}

/// <summary>Exception carrying a <see cref="WindowVeilErrorCode"/>.</summary>
public class WindowVeilException : Exception
{
    /// <summary>Creates an exception.</summary>
    public WindowVeilException(WindowVeilErrorCode code, string message, Exception? innerException = null)
        : base(message, innerException) => Code = code;

    /// <summary>Error category.</summary>
    public WindowVeilErrorCode Code { get; }
}
