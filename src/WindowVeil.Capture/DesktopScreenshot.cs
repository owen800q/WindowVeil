using WindowVeil.Geometry;
using WindowVeil.Imaging;

namespace WindowVeil.Capture;

/// <summary>
/// Screenshot helpers (used by evidence tooling and tests). Captures show the composed desktop, overlays included.
/// </summary>
public static class DesktopScreenshot
{
    /// <summary>Bounds of the virtual desktop (union of all monitors) in physical pixels.</summary>
    public static PixelRect VirtualScreenBounds => GdiCapture.GetVirtualScreenBounds();

    /// <summary>Captures every monitor.</summary>
    /// <exception cref="WindowVeilException"><see cref="WindowVeilErrorCode.CaptureUnavailable"/> when the capture fails.</exception>
    public static BgraImage CaptureVirtualScreen() => CaptureRegion(VirtualScreenBounds);

    /// <summary>Captures a region of the virtual desktop (physical pixels; clipped to the screen).</summary>
    /// <exception cref="WindowVeilException"><see cref="WindowVeilErrorCode.CaptureUnavailable"/> when the capture fails.</exception>
    public static BgraImage CaptureRegion(PixelRect region)
    {
        var result = GdiCapture.CaptureDesktop(region);
        return result.Frame?.Image
            ?? throw new WindowVeilException(WindowVeilErrorCode.CaptureUnavailable, $"Desktop capture failed ({result.Error}): {result.Message}");
    }

    /// <summary>Writes an image as PNG, creating the directory if needed.</summary>
    public static void SavePng(BgraImage image, string path)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        PngCodec.Save(image, path);
    }

    /// <summary>Captures every monitor and saves the result as PNG. Returns the image.</summary>
    public static BgraImage SaveVirtualScreenPng(string path)
    {
        var image = CaptureVirtualScreen();
        SavePng(image, path);
        return image;
    }
}
