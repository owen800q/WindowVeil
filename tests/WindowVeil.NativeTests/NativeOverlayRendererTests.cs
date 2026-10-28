using WindowVeil.Appearance;
using WindowVeil.Geometry;
using WindowVeil.Imaging;
using WindowVeil.Native;
using WindowVeil.Rendering;
using static WindowVeil.NativeTests.TestUtil;

namespace WindowVeil.NativeTests;

[Trait("Category", "Desktop")]
public sealed class NativeOverlayRendererTests : IDisposable
{
    private const long WS_EX_TOPMOST = 0x8, WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_LAYERED = 0x80000, WS_EX_NOACTIVATE = 0x8000000;
    private const long WS_POPUP = 0x80000000;

    private readonly TestTargetWindow _target = TestTargetWindow.Create();

    public void Dispose() => _target.Dispose();

    [Fact]
    public async Task PassThroughMask_HasLayeredToolNoActivateTransparentStyles_AndIsCreatedHidden()
    {
        await using var renderer = new NativeOverlayRenderer();
        var visual = await CreateAsync(renderer, MaskAppearance.Solid("#FF0000"));

        Assert.True(NativeDiagnostics.IsWindow(visual.Handle));
        Assert.False(NativeDiagnostics.IsWindowVisible(visual.Handle));
        Assert.True(OverlayWindowRegistry.IsOverlay(visual.Handle));
        long ex = NativeDiagnostics.GetExtendedStyles(visual.Handle);
        Assert.Equal(WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TRANSPARENT,
            ex & (WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TRANSPARENT));
        Assert.Equal(0, ex & WS_EX_TOPMOST);
        Assert.NotEqual(0, NativeDiagnostics.GetWindowStyles(visual.Handle) & WS_POPUP);
        Assert.Null(await visual.MeasureAsync(DpiScale.Default));
        Assert.Equal(1, renderer.CreatedWindowCount);
        Assert.Equal(1, renderer.LiveWindowCount);
    }

    [Fact]
    public async Task InteractiveMask_OmitsTransparent_TopmostOnlyWhenRequested()
    {
        await using var renderer = new NativeOverlayRenderer();
        var interactive = await CreateAsync(renderer, MaskAppearance.Solid("#00FF00"), OverlayInputMode.Interactive);
        var topmost = await CreateAsync(renderer, MaskAppearance.Solid("#00FF00"), zOrder: OverlayZOrder.Topmost);
        var highlight = await CreateAsync(renderer, HighlightAppearance.Create("#FFFF00"), OverlayInputMode.Interactive);

        long ex = NativeDiagnostics.GetExtendedStyles(interactive.Handle);
        Assert.Equal(0, ex & WS_EX_TRANSPARENT);
        Assert.NotEqual(0, ex & WS_EX_NOACTIVATE);
        Assert.NotEqual(0, ex & WS_EX_LAYERED);
        Assert.Equal(0, ex & WS_EX_TOPMOST);

        Assert.NotEqual(0, NativeDiagnostics.GetExtendedStyles(topmost.Handle) & WS_EX_TOPMOST);
        // Highlights are always click-through, whatever the requested input mode.
        Assert.NotEqual(0, NativeDiagnostics.GetExtendedStyles(highlight.Handle) & WS_EX_TRANSPARENT);
        Assert.True(highlight.IsClickThrough);
    }

    [Fact]
    public async Task Apply_PositionsResizesShowsAndHides()
    {
        await using var renderer = new NativeOverlayRenderer();
        var visual = await CreateAsync(renderer, MaskAppearance.Solid("#FF00FF"));
        var rect = ClientRect(_target, 20, 30, 120, 40);

        await renderer.ApplyAsync([new OverlayVisualUpdate(visual, Frame(_target, rect))]);
        Assert.Equal(rect, NativeDiagnostics.GetWindowRect(visual.Handle));
        Assert.True(NativeDiagnostics.IsWindowVisible(visual.Handle));
        Assert.NotNull(visual.CurrentFrame);

        var bigger = ClientRect(_target, 40, 50, 200, 90);
        await renderer.ApplyAsync([new OverlayVisualUpdate(visual, Frame(_target, bigger))]);
        Assert.Equal(bigger, NativeDiagnostics.GetWindowRect(visual.Handle));

        await renderer.ApplyAsync([new OverlayVisualUpdate(visual, OverlayFrame.Hidden)]);
        Assert.False(NativeDiagnostics.IsWindowVisible(visual.Handle));
        Assert.True(NativeDiagnostics.IsWindow(visual.Handle)); // hidden, not destroyed

        await renderer.ApplyAsync([new OverlayVisualUpdate(visual, Frame(_target, rect))]);
        Assert.True(NativeDiagnostics.IsWindowVisible(visual.Handle));
        Assert.Equal(rect, NativeDiagnostics.GetWindowRect(visual.Handle));
        Assert.Equal(1, renderer.CreatedWindowCount); // never recreated per update
    }

    [Fact]
    public async Task OpacityAndColorChanges_AreApplied()
    {
        await using var renderer = new NativeOverlayRenderer();
        var visual = await CreateAsync(renderer, MaskAppearance.Transparent("#000000", 0.5));
        Assert.Equal((byte)128, NativeDiagnostics.GetLayeredWindowAlpha(visual.Handle));

        await visual.SetAppearanceAsync(MaskAppearance.Transparent("#FF0000", 0.25));
        Assert.Equal((byte)64, NativeDiagnostics.GetLayeredWindowAlpha(visual.Handle));
        Assert.Equal(OverlayColor.Parse("#FF0000"), Assert.IsType<MaskAppearance>(visual.Appearance).Color);

        await Assert.ThrowsAsync<ArgumentException>(() => visual.SetAppearanceAsync(HighlightAppearance.Create("#FF0000")));
    }

    [Fact]
    public async Task MultipleMasks_InOneBatch_AreAllPlacedAboveTheTarget()
    {
        await using var renderer = new NativeOverlayRenderer();
        var visuals = new List<NativeOverlayVisual>();
        var rects = new[] { ClientRect(_target, 10, 10, 50, 20), ClientRect(_target, 80, 10, 60, 25), ClientRect(_target, 10, 60, 120, 30) };
        foreach (var _ in rects) visuals.Add(await CreateAsync(renderer, MaskAppearance.Solid("#202020")));

        await renderer.ApplyAsync(visuals.Select((v, i) => new OverlayVisualUpdate(v, Frame(_target, rects[i]))).ToArray());

        for (int i = 0; i < rects.Length; i++)
        {
            Assert.Equal(rects[i], NativeDiagnostics.GetWindowRect(visuals[i].Handle));
            Assert.True(NativeDiagnostics.IsWindowVisible(visuals[i].Handle));
            Assert.True(IsAbove(visuals[i].Handle, _target.Handle), $"overlay {i} must be above the target");
        }

        // Raising the target above its overlays and re-applying puts them back above it.
        _target.BringToTop();
        Assert.False(IsAbove(visuals[0].Handle, _target.Handle));
        await renderer.ApplyAsync(visuals.Select((v, i) => new OverlayVisualUpdate(v, Frame(_target, rects[i].Offset(1, 0)))).ToArray());
        Assert.All(visuals, v => Assert.True(IsAbove(v.Handle, _target.Handle)));
    }

    [Fact]
    public async Task AboveTargetOverlay_StaysDirectlyAboveTarget()
    {
        await using var renderer = new NativeOverlayRenderer();
        var visual = await CreateAsync(renderer, MaskAppearance.Solid("#FF0000"));
        await renderer.ApplyAsync([new OverlayVisualUpdate(visual, Frame(_target, ClientRect(_target, 5, 5, 40, 40)))]);

        // Only WindowVeil overlays may sit between the target and this overlay.
        nint current = NativeDiagnostics.GetWindowAbove(_target.Handle);
        while (current != 0 && current != visual.Handle && OverlayWindowRegistry.IsOverlay(current))
            current = NativeDiagnostics.GetWindowAbove(current);
        Assert.Equal(visual.Handle, current);
        Assert.Equal(0, NativeDiagnostics.GetExtendedStyles(visual.Handle) & WS_EX_TOPMOST);
    }

    [Fact]
    public async Task CreatingAndShowingOverlays_DoesNotChangeTheForegroundWindow()
    {
        nint before = NativeDiagnostics.GetForegroundWindow();
        await using var renderer = new NativeOverlayRenderer();
        var passThrough = await CreateAsync(renderer, MaskAppearance.Solid("#FF0000"));
        var interactive = await CreateAsync(renderer, MaskAppearance.Solid("#00FF00"), OverlayInputMode.Interactive);
        var spotlight = await CreateAsync(renderer, SpotlightAppearance.Create(0.4));
        await renderer.ApplyAsync(
        [
            new OverlayVisualUpdate(passThrough, Frame(_target, ClientRect(_target, 10, 10, 40, 40))),
            new OverlayVisualUpdate(interactive, Frame(_target, ClientRect(_target, 60, 10, 40, 40))),
            new OverlayVisualUpdate(spotlight, Frame(_target, ClientRect(_target, 110, 10, 40, 40))),
        ]);
        NativeDiagnostics.DwmFlush();

        Assert.Equal(before, NativeDiagnostics.GetForegroundWindow());
        Assert.NotEqual(passThrough.Handle, NativeDiagnostics.GetForegroundWindow());
        Assert.NotEqual(interactive.Handle, NativeDiagnostics.GetForegroundWindow());
    }

    [Fact]
    public async Task PassThroughOverlay_IsNotHitTested_InteractiveOverlayIs()
    {
        await using var renderer = new NativeOverlayRenderer();
        var passThrough = await CreateAsync(renderer, MaskAppearance.Solid("#FF0000"));
        var interactive = await CreateAsync(renderer, MaskAppearance.Solid("#00FF00"), OverlayInputMode.Interactive);
        var a = ClientRect(_target, 20, 20, 80, 60);
        var b = ClientRect(_target, 140, 20, 80, 60);
        await renderer.ApplyAsync([new OverlayVisualUpdate(passThrough, Frame(_target, a)), new OverlayVisualUpdate(interactive, Frame(_target, b))]);
        NativeDiagnostics.DwmFlush();

        Assert.NotEqual(passThrough.Handle, NativeDiagnostics.WindowFromPoint(a.Center));
        Assert.Equal(_target.Handle, NativeDiagnostics.WindowFromPoint(a.Center));
        Assert.Equal(interactive.Handle, NativeDiagnostics.WindowFromPoint(b.Center));
    }

    [Fact]
    public async Task MultiRegionMask_CoversOnlyItsRegions()
    {
        await using var renderer = new NativeOverlayRenderer();
        var visual = await CreateAsync(renderer, MaskAppearance.Solid("#00FF00"), OverlayInputMode.Interactive);
        var line1 = ClientRect(_target, 20, 20, 150, 20);
        var line2 = ClientRect(_target, 20, 80, 90, 20);
        await renderer.ApplyAsync([new OverlayVisualUpdate(visual, Frame(_target, line1, line2))]);
        NativeDiagnostics.DwmFlush();

        Assert.Equal(PixelRect.UnionAll([line1, line2]), NativeDiagnostics.GetWindowRect(visual.Handle));
        Assert.Equal(visual.Handle, NativeDiagnostics.WindowFromPoint(line1.Center));
        Assert.Equal(visual.Handle, NativeDiagnostics.WindowFromPoint(line2.Center));
        // The gap between the lines and the area right of line 2 are outside the window region.
        Assert.NotEqual(visual.Handle, NativeDiagnostics.WindowFromPoint(new PixelPoint(line1.X + 10, line1.Bottom + 15)));
        Assert.NotEqual(visual.Handle, NativeDiagnostics.WindowFromPoint(new PixelPoint(line1.Right - 10, line2.Center.Y)));
    }

    [Fact]
    public async Task Highlight_SurroundsTargetWithDpiScaledBorder()
    {
        await using var renderer = new NativeOverlayRenderer();
        var visual = await CreateAsync(renderer, HighlightAppearance.Create("#FFCC00", thickness: 4));
        var rect = ClientRect(_target, 50, 50, 100, 40);
        var frame = Frame(_target, rect);
        await renderer.ApplyAsync([new OverlayVisualUpdate(visual, frame)]);

        int t = CoordinateConverter.DipToPhysicalRounded(4, frame.Dpi);
        Assert.Equal(rect.Inflate(t), NativeDiagnostics.GetWindowRect(visual.Handle));
        Assert.True(NativeDiagnostics.IsWindowVisible(visual.Handle));
    }

    [Fact]
    public async Task Spotlight_CoversTargetWindowBounds()
    {
        await using var renderer = new NativeOverlayRenderer();
        var visual = await CreateAsync(renderer, SpotlightAppearance.Create(0.5));
        var frame = Frame(_target, ClientRect(_target, 30, 30, 60, 30));
        await renderer.ApplyAsync([new OverlayVisualUpdate(visual, frame)]);

        Assert.Equal(frame.TargetWindowBounds, NativeDiagnostics.GetWindowRect(visual.Handle));
        Assert.Equal((byte)128, NativeDiagnostics.GetLayeredWindowAlpha(visual.Handle));
    }

    [Fact]
    public async Task CreateAndDispose200Overlays_ReleasesUserAndGdiObjects()
    {
        await using var renderer = new NativeOverlayRenderer();
        // Warm up (class registration, brush cache, first window).
        var warm = await CreateAsync(renderer, MaskAppearance.Solid("#123456"));
        await warm.DisposeAsync();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        var (userBefore, gdiBefore) = NativeDiagnostics.GetGuiResourceCounts();
        int registryBefore = OverlayWindowRegistry.Count;

        var rect = ClientRect(_target, 10, 10, 60, 20);
        var rect2 = ClientRect(_target, 10, 40, 60, 20);
        for (int i = 0; i < 200; i++)
        {
            OverlayAppearance appearance = (i % 3) switch
            {
                0 => MaskAppearance.Solid(OverlayColor.FromRgb((byte)i, 0, 0)),
                1 => HighlightAppearance.Create("#00FF00"),
                _ => SpotlightAppearance.Create(0.3),
            };
            var v = await CreateAsync(renderer, appearance);
            await renderer.ApplyAsync([new OverlayVisualUpdate(v, Frame(_target, rect, rect2))]);
            await v.DisposeAsync();
            await v.DisposeAsync(); // idempotent
            Assert.False(NativeDiagnostics.IsWindow(v.Handle));
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        var (userAfter, gdiAfter) = NativeDiagnostics.GetGuiResourceCounts();
        Assert.InRange(userAfter - userBefore, -10, 10);
        Assert.InRange(gdiAfter - gdiBefore, -10, 10);
        Assert.Equal(registryBefore, OverlayWindowRegistry.Count);
        Assert.Equal(0, renderer.LiveWindowCount);
        Assert.Equal(201, renderer.CreatedWindowCount);
    }

    [Fact]
    public async Task DisposingTheRenderer_DestroysRemainingWindows()
    {
        int registryBefore = OverlayWindowRegistry.Count;
        var renderer = new NativeOverlayRenderer();
        var visuals = new[]
        {
            await CreateAsync(renderer, MaskAppearance.Solid("#FF0000")),
            await CreateAsync(renderer, HighlightAppearance.Create("#FF0000")),
        };
        await renderer.ApplyAsync([new OverlayVisualUpdate(visuals[0], Frame(_target, ClientRect(_target, 1, 1, 10, 10)))]);

        await renderer.DisposeAsync();
        await renderer.DisposeAsync(); // idempotent

        Assert.All(visuals, v => Assert.False(NativeDiagnostics.IsWindow(v.Handle)));
        Assert.Equal(registryBefore, OverlayWindowRegistry.Count);
        Assert.True(renderer.MessageThread.IsDisposed);
        await visuals[0].DisposeAsync(); // disposing a visual after its renderer is harmless
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await renderer.CreateVisualAsync(new OverlayVisualRequest("x", MaskAppearance.Solid("#000"))));
    }

    [Fact]
    public async Task SharedMessageThread_IsNotDisposedByTheRenderer()
    {
        using var thread = new NativeMessageThread();
        var renderer = new NativeOverlayRenderer(new NativeOverlayOptions { MessageThread = thread });
        var visual = await CreateAsync(renderer, MaskAppearance.Solid("#FF0000"));
        Assert.Equal(thread.ManagedThreadId, await thread.InvokeAsync(() => Environment.CurrentManagedThreadId));
        await renderer.DisposeAsync();
        Assert.False(thread.IsDisposed);
        Assert.False(NativeDiagnostics.IsWindow(visual.Handle));
    }
}
