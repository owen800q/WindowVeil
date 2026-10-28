using WindowVeil.Appearance;
using WindowVeil.Geometry;
using WindowVeil.Native;
using WindowVeil.Platform;
using WindowVeil.Rendering;
using WindowVeil.Tracking;

namespace WindowVeil.NativeTests;

/// <summary>Pure geometry and polling logic (no real windows), kept with the desktop suite because it ships in the Windows packages.</summary>
[Trait("Category", "Desktop")]
public sealed class OverlayLayoutTests
{
    private static readonly TargetWindowInfo Window = new()
    {
        Handle = 0x1234,
        ProcessId = 42,
        Bounds = new PixelRect(100, 100, 800, 600),
        ClientBounds = new PixelRect(108, 130, 784, 562),
        MonitorBounds = new PixelRect(0, 0, 1920, 1080),
        Dpi = DpiScale.FromPercent(150),
    };

    [Fact]
    public void Mask_SingleRegion_CoversWholeWindow_MultipleRegions_UseWindowRelativeShapes()
    {
        var single = OverlayLayout.Compute(MaskAppearance.Solid("#000"), OverlayFrame.Show([new PixelRect(200, 200, 50, 20)], [], Window));
        Assert.True(single.Visible);
        Assert.Equal(new PixelRect(200, 200, 50, 20), single.Bounds);
        Assert.Null(single.Shapes);

        var multi = OverlayLayout.Compute(MaskAppearance.Solid("#000"),
            OverlayFrame.Show([new PixelRect(200, 200, 50, 20), new PixelRect(200, 240, 30, 20)], [], Window));
        Assert.Equal(new PixelRect(200, 200, 50, 60), multi.Bounds);
        Assert.NotNull(multi.Shapes);
        Assert.Equal(new PixelRect(0, 0, 50, 20), multi.Shapes![0].Outer);
        Assert.Equal(new PixelRect(0, 40, 30, 20), multi.Shapes[1].Outer);
        Assert.All(multi.Shapes, s => Assert.Empty(s.Holes));
    }

    [Fact]
    public void Highlight_InflatesByDpiScaledThickness_AndCutsOutTheTarget()
    {
        var target = new PixelRect(300, 300, 100, 40);
        var layout = OverlayLayout.Compute(HighlightAppearance.Create("#F00", thickness: 2), OverlayFrame.Show([target], [target], Window));
        // 2 DIP at 150% = 3 px.
        Assert.Equal(target.Inflate(3), layout.Bounds);
        var shape = Assert.Single(layout.Shapes!);
        Assert.Equal(new PixelRect(0, 0, 106, 46), shape.Outer);
        Assert.Equal(new PixelRect(3, 3, 100, 40), Assert.Single(shape.Holes));
    }

    [Fact]
    public void Spotlight_CoversWindowOrMonitor_WithHolesForTargets()
    {
        var target = new PixelRect(300, 300, 100, 40);
        var frame = OverlayFrame.Show([target], [target], Window);

        var windowScope = OverlayLayout.Compute(SpotlightAppearance.Create(0.5), frame);
        Assert.Equal(Window.Bounds, windowScope.Bounds);
        Assert.Equal(new PixelRect(200, 200, 100, 40), Assert.Single(Assert.Single(windowScope.Shapes!).Holes));

        var monitorScope = OverlayLayout.Compute(SpotlightAppearance.Create(0.5, SpotlightScope.Monitor), frame);
        Assert.Equal(Window.MonitorBounds, monitorScope.Bounds);
        Assert.Equal(target, Assert.Single(Assert.Single(monitorScope.Shapes!).Holes));
    }

    [Fact]
    public void HiddenFrames_ProduceHiddenLayouts_AndShapeComparisonIsStructural()
    {
        Assert.False(OverlayLayout.Compute(MaskAppearance.Solid("#000"), OverlayFrame.Hidden).Visible);
        var a = new[] { new OverlayRegionShape(new PixelRect(0, 0, 5, 5), [new PixelRect(1, 1, 1, 1)]) };
        var b = new[] { new OverlayRegionShape(new PixelRect(0, 0, 5, 5), [new PixelRect(1, 1, 1, 1)]) };
        Assert.True(OverlayLayout.SameShapes(a, b));
        Assert.True(OverlayLayout.SameShapes(null, null));
        Assert.False(OverlayLayout.SameShapes(a, null));
    }

    [Fact]
    public async Task Polling_WithFakeWindowSystem_DiffsSnapshots()
    {
        var fake = new FakeWindowSystem { Current = Window };
        using var source = new PollingWindowEventSource(fake, TimeSpan.FromMilliseconds(10));
        var events = new System.Collections.Concurrent.ConcurrentQueue<WindowEvent>();
        using var sub = source.Subscribe(Window, events.Enqueue);

        fake.Current = Window with { Bounds = Window.Bounds.Offset(10, 0), IsForeground = true };
        await TestUtil.EventuallyAsync(() => events.Any(e => e.Kind == WindowEventKind.LocationChanged) && events.Any(e => e.Kind == WindowEventKind.ForegroundChanged), "move + foreground");

        fake.Current = fake.Current with { IsMinimized = true };
        await TestUtil.EventuallyAsync(() => events.Any(e => e.Kind == WindowEventKind.Minimized), "minimize");

        fake.Current = null;
        await TestUtil.EventuallyAsync(() => events.Any(e => e.Kind == WindowEventKind.Destroyed), "destroy");
        int count = events.Count;
        await Task.Delay(100);
        Assert.Equal(count, events.Count); // polling stops after destruction
    }

    private sealed class FakeWindowSystem : IWindowSystem
    {
        public volatile TargetWindowInfo? Current;

        public IReadOnlyList<TargetWindowInfo> EnumerateWindows() => Current is { } c ? [c] : [];

        public TargetWindowInfo? GetWindow(nint handle) => Current is { } c && c.Handle == handle ? c : null;

        public bool IsProcessAlive(int processId) => Current is not null;

        public PixelRect VirtualScreenBounds => new(0, 0, 1920, 1080);
    }
}
