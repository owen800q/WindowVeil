using WindowVeil.Automation;

namespace WindowVeil;

/// <summary>One-call composition of the Windows desktop defaults.</summary>
public static class WindowsDesktopBuilderExtensions
{
    /// <summary>
    /// Registers everything needed to overlay external Windows applications:
    /// <list type="bullet">
    /// <item><c>Win32WindowSystem</c> and the native overlay renderer (masks, highlights, spotlights) — <c>UseNativeOverlays</c>;</item>
    /// <item>WinEvent-based tracking sharing the native message thread — <c>UseWinEventTracking</c>;</item>
    /// <item>UI Automation locators (AutomationId, Name, ControlType, ClassName, TextPattern) — <c>UseUiAutomation</c>;</item>
    /// <item>GDI / PrintWindow capture for image-based locators — <c>UseScreenCapture</c>.</item>
    /// </list>
    /// Add <c>UseWpfOverlays()</c> for WPF content and <c>UseOcr(...)</c> for OCR locators.
    /// </summary>
    public static WindowVeilBuilder UseWindowsDesktop(this WindowVeilBuilder builder, WindowsDesktopOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        options ??= new WindowsDesktopOptions();
        builder.UseNativeOverlays(options.NativeOverlays);
        if (options.UsePollingTracking) builder.UsePollingTracking();
        else builder.UseWinEventTracking();
        builder.UseUiAutomation(options.UiAutomation);
        if (builder.Capture is null) builder.UseScreenCapture();
        return builder;
    }
}

/// <summary>Options for <see cref="WindowsDesktopBuilderExtensions.UseWindowsDesktop"/>.</summary>
public sealed record WindowsDesktopOptions
{
    /// <summary>Native renderer options.</summary>
    public Native.NativeOverlayOptions? NativeOverlays { get; init; }

    /// <summary>UI Automation options.</summary>
    public UiAutomationOptions? UiAutomation { get; init; }

    /// <summary>Use polling instead of WinEvent hooks (for environments where hooks are unavailable).</summary>
    public bool UsePollingTracking { get; init; }
}
