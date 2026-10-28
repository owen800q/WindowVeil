namespace WindowVeil.Capture;

/// <summary><see cref="WindowVeilBuilder"/> extensions for screen capture.</summary>
public static class CaptureWindowVeilBuilderExtensions
{
    /// <summary>Uses <see cref="GdiScreenCapture"/> as the capture backend (required by OCR locators).</summary>
    public static WindowVeilBuilder UseScreenCapture(this WindowVeilBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.UseCapture(new GdiScreenCapture());
    }
}
