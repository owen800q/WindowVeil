using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WindowVeil.Geometry;
using WindowVeil.Rendering;
using WindowVeil.Wpf;
using WindowVeil.WpfTests.Infrastructure;

namespace WindowVeil.WpfTests;

[Trait("Category", "Desktop")]
public sealed class WpfGroupedHostingTests : IAsyncLifetime
{
    private TestTargetWindow _target = null!;
    private WpfOverlayRenderer _renderer = null!;
    private readonly List<IOverlayVisual> _visuals = [];

    public async Task InitializeAsync()
    {
        _target = await TestTargetWindow.CreateAsync();
        _renderer = new WpfOverlayRenderer(new WpfOverlayOptions { HostingMode = WpfHostingMode.Grouped });
    }

    public async Task DisposeAsync()
    {
        foreach (var v in _visuals) await v.DisposeAsync();
        await _renderer.DisposeAsync();
        await _target.DisposeAsync();
    }

    private async Task<IOverlayVisual> CreateAsync(OverlayInputMode mode, OverlayZOrder zOrder = OverlayZOrder.AboveTarget)
    {
        var visual = await _renderer.CreateVisualAsync(new OverlayVisualRequest(Guid.NewGuid().ToString("n"),
            WpfAppearance.Create(() => new Border { Background = Brushes.DarkOrange })) { InputMode = mode, ZOrder = zOrder });
        _visuals.Add(visual);
        return visual;
    }

    private PixelRect Cell(int i) => _target.Inside(10 + (i % 5) * 60, 40 + (i / 5) * 40, 50, 30);

    [Fact]
    public async Task Twenty_overlays_for_one_target_and_input_mode_share_one_window()
    {
        var visuals = new List<IOverlayVisual>();
        for (int i = 0; i < 20; i++) visuals.Add(await CreateAsync(OverlayInputMode.PassThrough));
        Assert.All(visuals, v => Assert.Equal(0, v.Handle)); // created lazily on the first update

        await _renderer.ApplyAsync(visuals.Select((v, i) => (v, _target.Frame(Cell(i)))).ToArray());

        var handles = visuals.Select(v => v.Handle).Distinct().ToArray();
        var handle = Assert.Single(handles);
        Assert.NotEqual(0, handle);
        Assert.True(OverlayWindowRegistry.IsOverlay(handle));
        Assert.Equal(PixelRect.UnionAll(Enumerable.Range(0, 20).Select(Cell)), TestNative.GetWindowBounds(handle));

        // Each member is placed on the shared canvas relative to the host origin, in DIPs.
        var scale = _target.Dpi.Scale;
        var origin = TestNative.GetWindowBounds(handle).Location;
        var (left, top, width) = await _renderer.OnDispatcher(() =>
        {
            var root = visuals[7].Internal().Root;
            return (Canvas.GetLeft(root), Canvas.GetTop(root), root.Width);
        });
        Assert.Equal((Cell(7).X - origin.X) / scale, left, 3);
        Assert.Equal((Cell(7).Y - origin.Y) / scale, top, 3);
        Assert.Equal(50 / scale, width, 3);
    }

    [Fact]
    public async Task Pass_through_and_interactive_overlays_use_separate_windows()
    {
        var visuals = new List<IOverlayVisual>();
        for (int i = 0; i < 20; i++) visuals.Add(await CreateAsync(i % 2 == 0 ? OverlayInputMode.PassThrough : OverlayInputMode.Interactive));

        await _renderer.ApplyAsync(visuals.Select((v, i) => (v, _target.Frame(Cell(i)))).ToArray());

        Assert.Equal(2, visuals.Select(v => v.Handle).Distinct().Count());
        var pass = visuals[0].Handle;
        var interactive = visuals[1].Handle;
        Assert.NotEqual(0, TestNative.GetExStyle(pass) & TestNative.WS_EX_TRANSPARENT);
        Assert.Equal(0, TestNative.GetExStyle(interactive) & TestNative.WS_EX_TRANSPARENT);
        Assert.NotEqual(0, TestNative.GetExStyle(interactive) & TestNative.WS_EX_NOACTIVATE);
    }

    [Fact]
    public async Task Gaps_between_interactive_members_pass_clicks_through()
    {
        var left = await CreateAsync(OverlayInputMode.Interactive, OverlayZOrder.Topmost);
        var right = await CreateAsync(OverlayInputMode.Interactive, OverlayZOrder.Topmost);
        var a = _target.Inside(20, 60, 80, 60);
        var b = _target.Inside(300, 60, 80, 60);
        await _renderer.ApplyAsync((left, _target.Frame(a)), (right, _target.Frame(b)));
        await TestHelpers.IdleAsync(_renderer.Dispatcher);
        var host = left.Handle;
        Assert.Equal(host, right.Handle);

        Assert.True(await TestHelpers.EventuallyAsync(() => TestNative.WindowAt(a.Center) == host && TestNative.WindowAt(b.Center) == host),
            "Opaque members of the interactive host should be hit.");
        var gap = new PixelPoint((a.Right + b.X) / 2, a.Center.Y);
        Assert.NotEqual(host, TestNative.WindowAt(gap));
    }

    [Fact]
    public async Task Hidden_members_collapse_and_an_empty_group_hides_or_closes_its_window()
    {
        var first = await CreateAsync(OverlayInputMode.PassThrough);
        var second = await CreateAsync(OverlayInputMode.PassThrough);
        await _renderer.ApplyAsync((first, _target.Frame(Cell(0))), (second, _target.Frame(Cell(6))));
        var host = first.Handle;
        Assert.Equal(PixelRect.UnionAll([Cell(0), Cell(6)]), TestNative.GetWindowBounds(host));

        // Hiding one member shrinks the host to the remaining one and collapses the hidden member.
        await _renderer.ApplyAsync((second, OverlayFrame.Hidden));
        Assert.Equal(Cell(0), TestNative.GetWindowBounds(host));
        Assert.Equal(Visibility.Collapsed, await _renderer.OnDispatcher(() => second.Internal().Root.Visibility));

        // Hiding every member hides the host.
        await _renderer.ApplyAsync((first, OverlayFrame.Hidden));
        Assert.False(TestNative.IsWindowVisible(host));

        // Disposing every member closes the shared window.
        await first.DisposeAsync();
        Assert.True(TestNative.IsWindow(host));
        await second.DisposeAsync();
        Assert.False(TestNative.IsWindow(host));
        Assert.False(OverlayWindowRegistry.IsOverlay(host));
    }
}
