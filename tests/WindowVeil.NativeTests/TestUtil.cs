using System.Diagnostics;
using WindowVeil.Appearance;
using WindowVeil.Geometry;
using WindowVeil.Imaging;
using WindowVeil.Native;
using WindowVeil.Platform;
using WindowVeil.Rendering;

// Every test drives the real desktop (z-order, foreground, screen pixels): never run them concurrently.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace WindowVeil.NativeTests;

internal static class TestUtil
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Polls <paramref name="condition"/> (with DwmFlush between attempts) until true or the timeout elapses.</summary>
    public static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan? timeout = null)
    {
        var sw = Stopwatch.StartNew();
        var limit = timeout ?? DefaultTimeout;
        while (true)
        {
            if (condition()) return true;
            if (sw.Elapsed >= limit) return false;
            NativeDiagnostics.DwmFlush();
            await Task.Delay(20);
        }
    }

    /// <summary>Asserts that <paramref name="condition"/> becomes true within the timeout.</summary>
    public static async Task EventuallyAsync(Func<bool> condition, string because, TimeSpan? timeout = null)
    {
        Assert.True(await WaitUntilAsync(condition, timeout), because);
    }

    /// <summary>A fresh snapshot of the target via the real window system.</summary>
    public static TargetWindowInfo Snapshot(TestTargetWindow target) =>
        new Win32WindowSystem().GetWindow(target.Handle) ?? throw new InvalidOperationException("Target window is gone.");

    /// <summary>A visible frame covering <paramref name="regions"/> for the target.</summary>
    public static OverlayFrame Frame(TestTargetWindow target, params PixelRect[] regions) =>
        OverlayFrame.Show(regions, regions, Snapshot(target));

    /// <summary>A rectangle inside the target's client area (offset from the client origin).</summary>
    public static PixelRect ClientRect(TestTargetWindow target, int x, int y, int width, int height)
    {
        var client = Snapshot(target).ClientBounds;
        return new PixelRect(client.X + x, client.Y + y, width, height);
    }

    public static async Task<NativeOverlayVisual> CreateAsync(NativeOverlayRenderer renderer, OverlayAppearance appearance,
        OverlayInputMode input = OverlayInputMode.PassThrough, OverlayZOrder zOrder = OverlayZOrder.AboveTarget,
        OverlayFocusPolicy focus = OverlayFocusPolicy.NoActivate, string? ruleId = null)
    {
        var visual = await renderer.CreateVisualAsync(new OverlayVisualRequest(ruleId ?? Guid.NewGuid().ToString("n"), appearance)
        {
            InputMode = input,
            ZOrder = zOrder,
            FocusPolicy = focus,
        });
        return Assert.IsType<NativeOverlayVisual>(visual);
    }

    /// <summary>True when <paramref name="window"/> is somewhere above <paramref name="target"/> in z-order.</summary>
    public static bool IsAbove(nint window, nint target)
    {
        nint current = NativeDiagnostics.GetWindowAbove(target);
        for (int i = 0; current != 0 && i < 10000; i++)
        {
            if (current == window) return true;
            current = NativeDiagnostics.GetWindowAbove(current);
        }
        return false;
    }

    /// <summary>Fraction of pixels within <paramref name="tolerance"/> of <paramref name="color"/>.</summary>
    public static double FractionMatching(BgraImage image, OverlayColor color, int tolerance = 3)
    {
        int matching = 0;
        for (int y = 0; y < image.Height; y++)
            for (int x = 0; x < image.Width; x++)
                if (image.GetPixel(x, y).MaxChannelDifference(color) <= tolerance) matching++;
        return (double)matching / (image.Width * image.Height);
    }
}
