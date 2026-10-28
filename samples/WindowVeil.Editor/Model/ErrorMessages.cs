using System.IO;
using WindowVeil.Profiles;

namespace WindowVeil.Editor.Model;

/// <summary>Friendly explanations of WindowVeil error codes for the diagnostics panel and message boxes.</summary>
public static class ErrorMessages
{
    /// <summary>Explains an error code in one sentence.</summary>
    public static string Explain(WindowVeilErrorCode code) => code switch
    {
        WindowVeilErrorCode.TargetWindowNotFound => "The target window was not found. It may have been closed; refresh the window list.",
        WindowVeilErrorCode.InvalidWindowHandle => "The window no longer exists. Refresh the window list and select it again.",
        WindowVeilErrorCode.TargetProcessExited => "The target application exited.",
        WindowVeilErrorCode.UiAutomationUnavailable => "UI Automation is not available for this window (the application may run elevated or expose no accessibility tree). Use a rectangle locator instead.",
        WindowVeilErrorCode.ElementNotFound => "The element was not found. Check the locator in the inspector, or add a rectangle fallback.",
        WindowVeilErrorCode.OcrConfidenceTooLow => "OCR found the text but with too little confidence.",
        WindowVeilErrorCode.OcrUnavailable => "OCR is not configured in this editor; OCR locators cannot be resolved here.",
        WindowVeilErrorCode.CaptureUnavailable => "Screen capture is unavailable or was refused.",
        WindowVeilErrorCode.WpfDispatcherFailure => "The WPF overlay dispatcher failed.",
        WindowVeilErrorCode.OverlayCreationFailed => "The overlay window could not be created (a WPF factory may have thrown).",
        WindowVeilErrorCode.RendererNotFound => "No renderer can draw this appearance.",
        WindowVeilErrorCode.InvalidRule => "The rule is invalid.",
        WindowVeilErrorCode.InvalidState => "The operation is not possible in the current session state.",
        WindowVeilErrorCode.PlatformUnavailable => "A required Windows service is unavailable.",
        _ => "An unexpected error occurred.",
    };

    /// <summary>Describes an exception for the user, including the WindowVeil error code when there is one.</summary>
    public static string Describe(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (exception is AggregateException { InnerExceptions.Count: 1 } agg) exception = agg.InnerExceptions[0];
        return exception switch
        {
            ProfileException pe => $"{pe.Message} {string.Join(" ", pe.Diagnostics.Select(d => d.ToString()))}",
            WindowVeilException wv => $"[{wv.Code}] {wv.Message} — {Explain(wv.Code)}",
            OperationCanceledException => "The operation was cancelled.",
            TimeoutException => $"Timed out: {exception.Message}",
            UnauthorizedAccessException or IOException => $"File error: {exception.Message}",
            _ => $"{exception.GetType().Name}: {exception.Message}",
        };
    }
}
