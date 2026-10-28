using WindowVeil.Appearance;
using WindowVeil.Capture;
using WindowVeil.Geometry;
using WindowVeil.Imaging;
using WindowVeil.Native;
using WindowVeil.Platform;
using WindowVeil.Rendering;
using static WindowVeil.NativeTests.TestUtil;

namespace WindowVeil.NativeTests;

[Trait("Category", "Desktop")]
public sealed class GdiScreenCaptureTests : IDisposable
{
    private static readonly OverlayColor TargetColor = OverlayColor.FromRgb(0x20, 0x80, 0xC0);
    private static readonly OverlayColor MaskColor = OverlayColor.FromRgb(0xFF, 0x00, 0xFF);

    private readonly TestTargetWindow _target = TestTargetWindow.Create(new PixelRect(180, 150, 520, 380), TargetColor);
    private readonly GdiScreenCapture _capture = new();

    public void Dispose() => _target.Dispose();

    [Fact]
    public async Task DesktopCapture_ReturnsTheTargetsColor()
    {
        _target.BringToTop();
        var region = ClientRect(_target, 100, 100, 40, 30);
        bool ok = await WaitUntilAsync(() =>
        {
            var r = _capture.Capture(new CaptureRequest(region, CaptureSource.Desktop));
            return r.Succeeded && FractionMatching(r.Frame!.Image, TargetColor) > 0.95;
        });
        var result = await _capture.CaptureAsync(new CaptureRequest(region, CaptureSource.Desktop));

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(region, result.Frame!.ScreenRegion);
        Assert.Equal(new PixelSize(40, 30), result.Frame.Image.Size);
        Assert.Equal(255, result.Frame.Image.GetPixel(0, 0).A);
        Assert.True(ok, $"desktop pixels should be {TargetColor}, got {result.Frame.Image.GetPixel(20, 15)}");
    }

    [Fact]
    public async Task WindowCapture_ExcludesOverlays_DesktopCaptureIncludesThem()
    {
        _target.BringToTop();
        await using var renderer = new NativeOverlayRenderer();
        var mask = await CreateAsync(renderer, MaskAppearance.Solid(MaskColor));
        var masked = ClientRect(_target, 60, 60, 120, 60);
        await renderer.ApplyAsync([new OverlayVisualUpdate(mask, Frame(_target, masked))]);
        var inner = masked.Inflate(-10);
        var window = Snapshot(_target);

        // The desktop shows the mask (layered windows are captured thanks to CAPTUREBLT).
        await EventuallyAsync(() =>
        {
            var d = _capture.Capture(new CaptureRequest(inner, CaptureSource.Desktop));
            return d.Succeeded && FractionMatching(d.Frame!.Image, MaskColor) > 0.95;
        }, "desktop capture should contain the overlay color");

        // PrintWindow renders only the target: the mask is invisible to OCR.
        var w = await _capture.CaptureAsync(new CaptureRequest(inner, CaptureSource.Window) { Window = window });
        Assert.True(w.Succeeded, w.Message);
        Assert.Equal(CaptureSource.Window, w.Frame!.Source);
        Assert.Equal(inner, w.Frame.ScreenRegion);
        Assert.Equal(0, FractionMatching(w.Frame.Image, MaskColor));
        Assert.True(FractionMatching(w.Frame.Image, TargetColor) > 0.95, $"window capture should show the target color, got {w.Frame.Image.GetPixel(5, 5)}");
    }

    [Fact]
    public async Task WindowCapture_MapsScreenCoordinatesWithTheWindowRect()
    {
        // The 16x16 white marker sits at the client origin; a region straddling it must show it at the right offset.
        var client = Snapshot(_target).ClientBounds;
        var region = new PixelRect(client.X + 8, client.Y + 8, 16, 16);
        var r = await _capture.CaptureAsync(new CaptureRequest(region, CaptureSource.Window) { Window = Snapshot(_target) });
        Assert.True(r.Succeeded, r.Message);
        Assert.True(r.Frame!.Image.GetPixel(2, 2).MaxChannelDifference(OverlayColor.White) <= 3);
        Assert.True(r.Frame.Image.GetPixel(12, 12).MaxChannelDifference(TargetColor) <= 3);
    }

    [Fact]
    public async Task InvalidRegions_AndDestroyedWindows_AreReportedNotThrown()
    {
        var empty = await _capture.CaptureAsync(new CaptureRequest(PixelRect.Empty, CaptureSource.Desktop));
        Assert.Equal(CaptureError.InvalidRegion, empty.Error);

        var offscreen = await _capture.CaptureAsync(new CaptureRequest(new PixelRect(5_000_000, 5_000_000, 10, 10), CaptureSource.Desktop));
        Assert.Equal(CaptureError.InvalidRegion, offscreen.Error);

        var window = Snapshot(_target);
        var outside = await _capture.CaptureAsync(new CaptureRequest(window.Bounds.Offset(5000, 0), CaptureSource.Window) { Window = window });
        Assert.Equal(CaptureError.InvalidRegion, outside.Error);

        var noWindow = await _capture.CaptureAsync(new CaptureRequest(window.Bounds, CaptureSource.Window));
        Assert.Equal(CaptureError.WindowUnavailable, noWindow.Error);

        _target.Destroy();
        var destroyed = await _capture.CaptureAsync(new CaptureRequest(window.Bounds, CaptureSource.Window) { Window = window });
        Assert.False(destroyed.Succeeded);
        Assert.Equal(CaptureError.WindowUnavailable, destroyed.Error);
        Assert.False(string.IsNullOrEmpty(destroyed.Message));
    }

    [Fact]
    public async Task RepeatedCaptures_DoNotLeakGdiObjects()
    {
        var region = ClientRect(_target, 0, 0, 200, 100);
        var window = Snapshot(_target);
        await _capture.CaptureAsync(new CaptureRequest(region, CaptureSource.Desktop));
        var (_, gdiBefore) = NativeDiagnostics.GetGuiResourceCounts();
        for (int i = 0; i < 100; i++)
        {
            Assert.True((await _capture.CaptureAsync(new CaptureRequest(region, CaptureSource.Desktop))).Succeeded);
            Assert.True((await _capture.CaptureAsync(new CaptureRequest(region, CaptureSource.Window) { Window = window })).Succeeded);
        }
        var (_, gdiAfter) = NativeDiagnostics.GetGuiResourceCounts();
        Assert.InRange(gdiAfter - gdiBefore, -5, 5);
    }

    [Fact]
    public void DesktopScreenshot_CapturesTheVirtualScreen()
    {
        var bounds = DesktopScreenshot.VirtualScreenBounds;
        var image = DesktopScreenshot.CaptureVirtualScreen();
        Assert.Equal(bounds.Size, image.Size);
        var path = Path.Combine(Path.GetTempPath(), "windowveil-tests", Guid.NewGuid().ToString("n") + ".png");
        try
        {
            DesktopScreenshot.SavePng(image, path);
            Assert.Equal(image.Size, PngCodec.Load(path).Size);
        }
        finally { File.Delete(path); }
    }
}
