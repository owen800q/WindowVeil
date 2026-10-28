using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WindowVeil.Geometry;
using WindowVeil.Rendering;
using WindowVeil.Wpf;
using WindowVeil.WpfTests.Infrastructure;
using DpiScale = WindowVeil.Geometry.DpiScale;

namespace WindowVeil.WpfTests;

[Trait("Category", "Desktop")]
public sealed class WpfOverlayRendererTests : IAsyncLifetime
{
    private TestTargetWindow _target = null!;
    private WpfOverlayRenderer _renderer = null!;

    public async Task InitializeAsync()
    {
        _target = await TestTargetWindow.CreateAsync();
        _renderer = new WpfOverlayRenderer();
    }

    public async Task DisposeAsync()
    {
        await _renderer.DisposeAsync();
        await _target.DisposeAsync();
    }

    private static OverlayVisualRequest Request(WpfAppearance appearance, OverlayInputMode input = OverlayInputMode.PassThrough,
        OverlayZOrder zOrder = OverlayZOrder.AboveTarget, string? ruleId = null) =>
        new(ruleId ?? Guid.NewGuid().ToString("n"), appearance) { InputMode = input, ZOrder = zOrder };

    private PixelRect DipSized(int dx, int dy, double widthDip, double heightDip)
    {
        var scale = _target.Dpi.Scale;
        return _target.Inside(dx, dy, (int)Math.Round(widthDip * scale), (int)Math.Round(heightDip * scale));
    }

    [Fact]
    public async Task Factory_runs_on_the_renderer_dispatcher_thread()
    {
        int factoryThread = 0;
        int testThread = Environment.CurrentManagedThreadId;
        var appearance = WpfAppearance.Create(() =>
        {
            factoryThread = Environment.CurrentManagedThreadId;
            return new Border();
        });

        await using var visual = await _renderer.CreateVisualAsync(Request(appearance));

        Assert.Equal(_renderer.Dispatcher.Thread.ManagedThreadId, factoryThread);
        Assert.NotEqual(testThread, factoryThread);
        Assert.Equal(ApartmentState.STA, _renderer.Dispatcher.Thread.GetApartmentState());
    }

    [Fact]
    public async Task Factory_receives_a_context_bound_to_the_dispatcher()
    {
        WpfOverlayContext? context = null;
        var raised = 0;
        var appearance = WpfAppearance.Create(ctx =>
        {
            context = ctx;
            ctx.TargetBoundsChanged += (_, e) =>
            {
                Assert.True(ctx.Dispatcher.CheckAccess());
                Interlocked.Increment(ref raised);
            };
            return new Border();
        });
        await using var visual = await _renderer.CreateVisualAsync(Request(appearance, ruleId: "ctx-rule"));
        var bounds = _target.Inside(20, 40, 120, 30);

        await _renderer.ApplyAsync((visual, _target.Frame(bounds)));

        Assert.NotNull(context);
        Assert.Equal("ctx-rule", context!.RuleId);
        Assert.Same(_renderer.Dispatcher, context.Dispatcher);
        Assert.Equal(bounds, context.OverlayBounds);
        Assert.Equal(_target.Dpi, context.Dpi);
        Assert.Equal(bounds.Width / _target.Dpi.Scale, context.OverlayBoundsDip.Width, 3);
        Assert.Equal(1, raised);
    }

    [Fact]
    public async Task Custom_user_control_renders_its_pixels()
    {
        await using var visual = await _renderer.CreateVisualAsync(Request(WpfAppearance.Create(() => new SolidUserControl())));
        await _renderer.ApplyAsync((visual, _target.Frame(DipSized(40, 60, 160, 60))));
        await TestHelpers.IdleAsync(_renderer.Dispatcher);

        var (matching, total) = await _renderer.OnDispatcher(() =>
        {
            var root = visual.Internal().Root;
            root.UpdateLayout();
            int w = (int)Math.Ceiling(root.ActualWidth), h = (int)Math.Ceiling(root.ActualHeight);
            var bitmap = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(root);
            var pixels = new byte[w * h * 4];
            bitmap.CopyPixels(pixels, w * 4, 0);
            int count = 0;
            for (int i = 0; i < pixels.Length; i += 4)
            {
                var c = SolidUserControl.Fill;
                if (pixels[i] == c.B && pixels[i + 1] == c.G && pixels[i + 2] == c.R && pixels[i + 3] == 255) count++;
            }
            return (count, w * h);
        });

        Assert.True(total > 0);
        Assert.True(matching > total / 2, $"Only {matching} of {total} pixels have the control's background color.");
    }

    [Fact]
    public async Task Data_binding_updates_the_content()
    {
        var vm = new GreetingViewModel();
        TextBlock? text = null;
        var appearance = WpfAppearance.Create(() =>
        {
            text = new TextBlock { DataContext = vm };
            text.SetBinding(TextBlock.TextProperty, new Binding(nameof(GreetingViewModel.Message)));
            return text;
        });
        await using var visual = await _renderer.CreateVisualAsync(Request(appearance));
        await _renderer.ApplyAsync((visual, _target.Frame(_target.Inside(10, 10, 200, 40))));

        // Binding work may be queued at DataBind priority, which runs after Normal: wait for the dispatcher to go idle.
        await TestHelpers.IdleAsync(_renderer.Dispatcher);
        Assert.Equal("Hello", await _renderer.OnDispatcher(() => text!.Text));

        await _renderer.OnDispatcher(() => { vm.Message = "Updated from the view model"; });
        await TestHelpers.IdleAsync(_renderer.Dispatcher);
        Assert.Equal("Updated from the view model", await _renderer.OnDispatcher(() => text!.Text));

        // Changes raised from another thread are marshalled by WPF's binding engine.
        vm.Message = "Updated from a worker thread";
        Assert.True(await TestHelpers.EventuallyAsync(() =>
            _renderer.Dispatcher.Invoke(() => text!.Text) == "Updated from a worker thread"));
    }

    [Fact]
    public async Task Interactive_button_executes_its_command()
    {
        int executed = 0;
        object? parameter = null;
        Button? button = null;
        var command = new RelayCommand(p => { parameter = p; Interlocked.Increment(ref executed); });
        var appearance = WpfAppearance.Create(() => button = new Button { Content = "Custom Action", Width = 140, Height = 36, Command = command, CommandParameter = "go" });
        await using var visual = await _renderer.CreateVisualAsync(Request(appearance, OverlayInputMode.Interactive));
        await _renderer.ApplyAsync((visual, _target.Frame(DipSized(30, 30, 140, 36))));
        await TestHelpers.IdleAsync(_renderer.Dispatcher);

        await _renderer.OnDispatcher(() =>
        {
            var peer = new ButtonAutomationPeer(button!);
            ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)).Invoke();
        });

        Assert.True(await TestHelpers.EventuallyAsync(() => Volatile.Read(ref executed) == 1));
        Assert.Equal("go", parameter);
    }

    [Fact]
    public async Task Input_modes_set_the_expected_window_styles_and_hit_testing()
    {
        Border Opaque() => new() { Background = Brushes.OrangeRed };
        await using var passThrough = await _renderer.CreateVisualAsync(Request(WpfAppearance.Create(Opaque), OverlayInputMode.PassThrough, OverlayZOrder.Topmost));
        await using var interactive = await _renderer.CreateVisualAsync(Request(WpfAppearance.Create(Opaque), OverlayInputMode.Interactive, OverlayZOrder.Topmost));
        var passBounds = _target.Inside(40, 60, 150, 80);
        var interBounds = _target.Inside(260, 60, 150, 80);
        await _renderer.ApplyAsync((passThrough, _target.Frame(passBounds)), (interactive, _target.Frame(interBounds)));
        await TestHelpers.IdleAsync(_renderer.Dispatcher);

        long passStyle = TestNative.GetExStyle(passThrough.Handle);
        long interStyle = TestNative.GetExStyle(interactive.Handle);
        Assert.NotEqual(0, passStyle & TestNative.WS_EX_TRANSPARENT);
        Assert.Equal(0, interStyle & TestNative.WS_EX_TRANSPARENT);
        foreach (var style in new[] { passStyle, interStyle })
        {
            Assert.NotEqual(0, style & TestNative.WS_EX_NOACTIVATE);
            Assert.NotEqual(0, style & TestNative.WS_EX_TOOLWINDOW);
            Assert.NotEqual(0, style & TestNative.WS_EX_LAYERED);
        }

        // Layered windows are hit-tested against their last presented bitmap: wait for WPF to render.
        Assert.True(await TestHelpers.EventuallyAsync(() => TestNative.WindowAt(interBounds.Center) == interactive.Handle),
            "WindowFromPoint at the interactive overlay's center should return the overlay.");
        Assert.NotEqual(passThrough.Handle, TestNative.WindowAt(passBounds.Center));
    }

    [Fact]
    public async Task Hit_testing_the_interactive_content_finds_the_button()
    {
        Button? button = null;
        var appearance = WpfAppearance.Create(() => new Border
        {
            Background = Brushes.Transparent,
            Child = button = new Button { Content = "Hit me", Width = 100, Height = 30 },
        });
        await using var visual = await _renderer.CreateVisualAsync(Request(appearance, OverlayInputMode.Interactive));
        await _renderer.ApplyAsync((visual, _target.Frame(DipSized(20, 20, 200, 80))));
        await TestHelpers.IdleAsync(_renderer.Dispatcher);

        var hitButton = await _renderer.OnDispatcher(() =>
        {
            var root = visual.Internal().Root;
            root.UpdateLayout();
            var center = button!.TranslatePoint(new Point(button.ActualWidth / 2, button.ActualHeight / 2), root);
            DependencyObject? hit = VisualTreeHelper.HitTest(root, center)?.VisualHit;
            while (hit is not null and not Button) hit = VisualTreeHelper.GetParent(hit);
            return ReferenceEquals(hit, button);
        });
        Assert.True(hitButton);
    }

    [Fact]
    public async Task Showing_overlays_does_not_change_the_foreground_window()
    {
        await _target.BringToForegroundAsync();
        var foreground = TestNative.GetForegroundWindow();

        await using var a = await _renderer.CreateVisualAsync(Request(WpfAppearance.Create(() => new Button { Content = "A" }), OverlayInputMode.Interactive));
        await using var b = await _renderer.CreateVisualAsync(Request(WpfAppearance.Create(() => new TextBlock { Text = "B" })));
        await using var c = await _renderer.CreateVisualAsync(Request(WpfAppearance.Create(() => new Button { Content = "C" }), OverlayInputMode.Interactive, OverlayZOrder.Topmost));
        await _renderer.ApplyAsync(
            (a, _target.Frame(_target.Inside(10, 40, 100, 30))),
            (b, _target.Frame(_target.Inside(10, 80, 100, 30))),
            (c, _target.Frame(_target.Inside(10, 120, 100, 30))));
        await TestHelpers.IdleAsync(_renderer.Dispatcher);

        // Hide and show again: the second show goes through a different path than the first.
        await _renderer.ApplyAsync((a, OverlayFrame.Hidden));
        await _renderer.ApplyAsync((a, _target.Frame(_target.Inside(10, 40, 100, 30))));
        await TestHelpers.IdleAsync(_renderer.Dispatcher);

        Assert.Equal(foreground, TestNative.GetForegroundWindow());
        Assert.True(TestNative.IsWindowVisible(a.Handle));
    }

    [Fact]
    public async Task Host_window_bounds_equal_the_frame_bounds_in_physical_pixels()
    {
        await using var visual = await _renderer.CreateVisualAsync(Request(WpfAppearance.Create(() => new Border { Background = Brushes.Navy })));
        var bounds = _target.Inside(33, 47, 211, 79);

        await _renderer.ApplyAsync((visual, _target.Frame(bounds)));

        Assert.Equal(bounds, TestNative.GetWindowBounds(visual.Handle));
        Assert.True(TestNative.IsWindowVisible(visual.Handle));
        Assert.Equal(bounds, visual.CurrentFrame!.Bounds);

        // Moving and resizing applies the new physical rectangle and re-sizes the content in DIPs.
        var moved = bounds.Offset(25, 10) with { Width = 150 };
        await _renderer.ApplyAsync((visual, _target.Frame(moved)));
        Assert.Equal(moved, TestNative.GetWindowBounds(visual.Handle));
        var rootWidth = await _renderer.OnDispatcher(() => visual.Internal().Root.Width);
        Assert.Equal(150 / _target.Dpi.Scale, rootWidth, 3);

        // Hidden frames hide the window without destroying it.
        await _renderer.ApplyAsync((visual, OverlayFrame.Hidden));
        Assert.False(TestNative.IsWindowVisible(visual.Handle));
        Assert.True(TestNative.IsWindow(visual.Handle));
    }

    [Fact]
    public async Task Above_target_overlay_is_stacked_directly_above_the_target()
    {
        await using var visual = await _renderer.CreateVisualAsync(Request(WpfAppearance.Create(() => new Border { Background = Brushes.Navy })));
        await _renderer.ApplyAsync((visual, _target.Frame(_target.Inside(20, 20, 100, 40))));

        Assert.Equal(visual.Handle, TestNativeZOrder.WindowAbove(_target.Handle));
    }

    [Theory]
    [InlineData(96)]
    [InlineData(144)]
    [InlineData(120)]
    public async Task MeasureAsync_returns_the_desired_size_scaled_to_physical_pixels(int dpi)
    {
        await using var visual = await _renderer.CreateVisualAsync(Request(WpfAppearance.Create(() => new Button { Content = "Custom Action", Width = 140, Height = 36 })));

        var size = await visual.MeasureAsync(new DpiScale(dpi));

        var scale = dpi / 96.0;
        Assert.Equal(new PixelSize((int)Math.Ceiling(140 * scale - 1e-6), (int)Math.Ceiling(36 * scale - 1e-6)), size);
    }

    [Fact]
    public async Task MeasureAsync_includes_text_from_data_bindings()
    {
        // The binding is resolved by WPF at DataBind priority after the factory returns; measuring must wait for it.
        await using var bound = await _renderer.CreateVisualAsync(Request(WpfAppearance.Create(() =>
        {
            var text = new TextBlock { FontSize = 14 };
            text.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(BoundModel.Label)));
            text.DataContext = new BoundModel();
            return text;
        })));
        await using var literal = await _renderer.CreateVisualAsync(Request(WpfAppearance.Create(() =>
            new TextBlock { FontSize = 14, Text = new BoundModel().Label })));

        var boundSize = await bound.MeasureAsync(DpiScale.Default);
        var literalSize = await literal.MeasureAsync(DpiScale.Default);

        Assert.NotNull(boundSize);
        Assert.Equal(literalSize, boundSize);
        Assert.True(boundSize!.Value.Width > 60, $"bound text measured {boundSize}");
    }

    [Fact]
    public async Task MeasureAsync_completes_on_a_thread_pool_thread_not_the_dispatcher()
    {
        await using var visual = await _renderer.CreateVisualAsync(Request(WpfAppearance.Create(() => new Button { Content = "x", Width = 40, Height = 20 })));

        // ExecuteSynchronously runs the continuation on whichever thread completed the task.
        bool completedOnDispatcher = await visual.MeasureAsync(DpiScale.Default).AsTask()
            .ContinueWith(_ => _renderer.Dispatcher.CheckAccess(), TaskContinuationOptions.ExecuteSynchronously);

        Assert.False(completedOnDispatcher, "the session loop must never continue on the WPF dispatcher thread");
    }

    [Fact]
    public async Task MeasureAsync_falls_back_when_the_dispatcher_never_goes_idle()
    {
        await using var visual = await _renderer.CreateVisualAsync(Request(WpfAppearance.Create(() => new Button { Content = "x", Width = 40, Height = 20 })));
        var dispatcher = _renderer.Dispatcher;
        var stop = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        void Busy()
        {
            Thread.Sleep(2);
            if (DateTime.UtcNow < stop) dispatcher.BeginInvoke(Busy, System.Windows.Threading.DispatcherPriority.Background);
        }
        await dispatcher.InvokeAsync(() => dispatcher.BeginInvoke(Busy, System.Windows.Threading.DispatcherPriority.Background));

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var size = await visual.MeasureAsync(DpiScale.Default);

        Assert.Equal(new PixelSize(40, 20), size);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"measure took {watch.Elapsed} while the dispatcher was busy");
    }

    private sealed class BoundModel
    {
        public string Label { get; } = "Masked by policy";
    }

    [Fact]
    public async Task MeasureAsync_returns_null_for_empty_content_or_when_size_to_content_is_off()
    {
        await using var empty = await _renderer.CreateVisualAsync(Request(WpfAppearance.Create(() => new Border())));
        await using var fixedSize = await _renderer.CreateVisualAsync(Request(WpfAppearance.Create(() => new Button { Width = 50, Height = 20 }) with { SizeToContent = false }));

        Assert.Null(await empty.MeasureAsync(DpiScale.Default));
        Assert.Null(await fixedSize.MeasureAsync(DpiScale.Default));
    }

    [Fact]
    public async Task Disposing_a_visual_destroys_its_window_and_unregisters_it()
    {
        var visual = await _renderer.CreateVisualAsync(Request(WpfAppearance.Create(() => new Border { Background = Brushes.Navy })));
        await _renderer.ApplyAsync((visual, _target.Frame(_target.Inside(20, 20, 100, 40))));
        var handle = visual.Handle;
        Assert.True(TestNative.IsWindow(handle));
        Assert.True(OverlayWindowRegistry.IsOverlay(handle));

        await visual.DisposeAsync();
        await visual.DisposeAsync(); // idempotent

        Assert.False(TestNative.IsWindow(handle));
        Assert.False(OverlayWindowRegistry.IsOverlay(handle));
    }

    [Fact]
    public async Task Disposing_the_renderer_closes_windows_and_shuts_its_dispatcher_down()
    {
        var renderer = new WpfOverlayRenderer();
        var dispatcher = renderer.Dispatcher;
        var visual = await renderer.CreateVisualAsync(Request(WpfAppearance.Create(() => new Border { Background = Brushes.Navy })));
        await renderer.ApplyAsync((visual, _target.Frame(_target.Inside(20, 20, 100, 40))));
        var handle = visual.Handle;

        await renderer.DisposeAsync();
        await renderer.DisposeAsync(); // idempotent
        await visual.DisposeAsync();   // safe after the dispatcher is gone

        Assert.True(dispatcher.HasShutdownFinished);
        Assert.False(dispatcher.Thread.IsAlive);
        Assert.False(TestNative.IsWindow(handle));
        Assert.False(OverlayWindowRegistry.IsOverlay(handle));
        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
            await renderer.CreateVisualAsync(Request(WpfAppearance.Create(() => new Border()))));
    }

    [Fact]
    public async Task Renderer_never_shuts_down_a_supplied_dispatcher()
    {
        await using var host = new WpfDispatcherHost("Application dispatcher");
        var renderer = new WpfOverlayRenderer(new WpfOverlayOptions { Dispatcher = host.Dispatcher });
        int factoryThread = 0;
        var visual = await renderer.CreateVisualAsync(Request(WpfAppearance.Create(() =>
        {
            factoryThread = Environment.CurrentManagedThreadId;
            return new Border();
        })));
        var handle = visual.Handle;

        await renderer.DisposeAsync();

        Assert.Equal(host.Thread.ManagedThreadId, factoryThread);
        Assert.False(host.Dispatcher.HasShutdownStarted);
        Assert.False(TestNative.IsWindow(handle));
    }

    [Fact]
    public async Task Factory_exception_surfaces_as_WindowVeilException_without_leaking_windows()
    {
        var before = OverlayWindowRegistry.Snapshot();
        var appearance = WpfAppearance.Create(() => throw new InvalidOperationException("boom"));

        var ex = await Assert.ThrowsAsync<WindowVeilException>(async () => await _renderer.CreateVisualAsync(Request(appearance)));

        Assert.Equal(WindowVeilErrorCode.OverlayCreationFailed, ex.Code);
        Assert.IsType<InvalidOperationException>(ex.InnerException);
        Assert.True(OverlayWindowRegistry.Snapshot().SetEquals(before));
    }

    [Fact]
    public async Task Factory_returning_an_unusable_element_is_rejected()
    {
        var shared = await _renderer.OnDispatcher(() => new Border());
        await using var first = await _renderer.CreateVisualAsync(Request(WpfAppearance.Create(() => shared)));
        var before = OverlayWindowRegistry.Snapshot();

        var reused = await Assert.ThrowsAsync<WindowVeilException>(async () => await _renderer.CreateVisualAsync(Request(WpfAppearance.Create(() => shared))));
        var nullContent = await Assert.ThrowsAsync<WindowVeilException>(async () => await _renderer.CreateVisualAsync(Request(WpfAppearance.Create(() => null!))));

        Assert.Equal(WindowVeilErrorCode.OverlayCreationFailed, reused.Code);
        Assert.Equal(WindowVeilErrorCode.OverlayCreationFailed, nullContent.Code);
        Assert.True(OverlayWindowRegistry.Snapshot().SetEquals(before));
    }

    [Fact]
    public async Task Exceptions_thrown_by_overlay_content_do_not_crash_the_dispatcher()
    {
        Button? button = null;
        var appearance = WpfAppearance.Create(() =>
        {
            button = new Button { Content = "Faulty" };
            button.Click += (_, _) => throw new InvalidOperationException("handler failure");
            return button;
        });
        await using var visual = await _renderer.CreateVisualAsync(Request(appearance, OverlayInputMode.Interactive));
        await _renderer.ApplyAsync((visual, _target.Frame(_target.Inside(10, 10, 100, 30))));

        await _renderer.OnDispatcher(() => ((IInvokeProvider)new ButtonAutomationPeer(button!)).Invoke());
        await TestHelpers.IdleAsync(_renderer.Dispatcher);

        Assert.False(_renderer.Dispatcher.HasShutdownStarted);
        Assert.Equal("ok", await _renderer.OnDispatcher(() => "ok"));
    }

    [Fact]
    public async Task Twenty_overlays_in_per_overlay_mode_use_twenty_windows()
    {
        var visuals = new List<IOverlayVisual>();
        try
        {
            for (int i = 0; i < 20; i++)
                visuals.Add(await _renderer.CreateVisualAsync(Request(WpfAppearance.Create(() => new Border { Background = Brushes.Teal }))));
            await _renderer.ApplyAsync(visuals.Select((v, i) => (v, _target.Frame(_target.Inside(10 + (i % 5) * 60, 40 + (i / 5) * 40, 50, 30)))).ToArray());

            var handles = visuals.Select(v => v.Handle).Distinct().ToArray();
            Assert.Equal(20, handles.Length);
            Assert.All(handles, h => Assert.True(TestNative.IsWindow(h) && OverlayWindowRegistry.IsOverlay(h)));
        }
        finally
        {
            foreach (var v in visuals) await v.DisposeAsync();
        }
    }
}

internal static partial class TestNativeZOrder
{
    [System.Runtime.InteropServices.LibraryImport("user32.dll")]
    private static partial nint GetWindow(nint hwnd, uint cmd);

    /// <summary>The window directly above <paramref name="hwnd"/> in the z-order (GW_HWNDPREV).</summary>
    public static nint WindowAbove(nint hwnd) => GetWindow(hwnd, 3);
}
