using WindowVeil.Automation;
using WindowVeil.Geometry;
using WindowVeil.Locators;
using WindowVeil.Text;

namespace WindowVeil.UiTests;

[Collection(DemoTargetCollection.Name)]
[Trait("Category", "Desktop")]
public sealed class UiAutomationLocatorResolverTests
{
    private const string StartupText = "Start automatically when I sign in to Windows";
    private readonly DemoTargetFixture _f;

    public UiAutomationLocatorResolverTests(DemoTargetFixture fixture) => _f = fixture;

    [Fact]
    public void CanResolve_HandlesUiAutomationAndTextLocators()
    {
        Assert.True(_f.Resolver.CanResolve(ElementLocator.ByAutomationId("x")));
        Assert.True(_f.Resolver.CanResolve(ElementLocator.ByText("x")));
        Assert.False(_f.Resolver.CanResolve(ElementLocator.Rectangle(0, 0, 1, 1)));
        Assert.False(_f.Resolver.CanResolve(ElementLocator.ByOcrText("x")));
    }

    [Fact]
    public async Task AutomationId_FindsCheckBoxInsideWindow()
    {
        var window = _f.Window;
        var r = await _f.ResolveAsync(ElementLocator.ByAutomationId("StartupCheck"));

        Assert.Equal(LocatorStatus.Resolved, r.Status);
        var b = Assert.Single(r.Bounds);
        double s = window.Dpi.Scale;
        Assert.True(window.ClientBounds.Contains(b), $"{b} should be inside the client area {window.ClientBounds}");
        Assert.InRange(b.Height, 12 * s, 40 * s);
        Assert.InRange(b.Width, 150 * s, 345 * s);
        Assert.Equal(window.Bounds, r.WindowBounds);
        Assert.IsType<UiAutomationElementState>(r.State);
    }

    [Fact]
    public async Task Name_ExactAndContainsCaseInsensitive()
    {
        var expected = await _f.BoundsOfAsync("StartupCheck");

        var exact = await _f.ResolveAsync(ElementLocator.ByName(StartupText));
        Assert.Equal(LocatorStatus.Resolved, exact.Status);
        Assert.Equal(expected, Assert.Single(exact.Bounds));

        var contains = await _f.ResolveAsync(ElementLocator.ByName("START AUTOMATICALLY", TextMatch.Contains, ignoreCase: true));
        Assert.Equal(LocatorStatus.Resolved, contains.Status);
        Assert.Equal(expected, Assert.Single(contains.Bounds));

        var wrongCase = await _f.ResolveAsync(ElementLocator.ByName(StartupText.ToUpperInvariant()));
        Assert.Equal(LocatorStatus.NotFound, wrongCase.Status);
    }

    [Theory]
    [InlineData("CheckBox")]
    [InlineData("checkbox")]
    [InlineData("ControlType.CheckBox")]
    public async Task ControlTypeAndName_Compound(string controlType)
    {
        var expected = await _f.BoundsOfAsync("StartMinimizedCheck");
        var r = await _f.ResolveAsync(ElementLocator.ByControlType(controlType).WithName("Start hidden", TextMatch.StartsWith));
        Assert.Equal(LocatorStatus.Resolved, r.Status);
        Assert.Equal(expected, Assert.Single(r.Bounds));

        var wrongType = await _f.ResolveAsync(ElementLocator.ByControlType("Button").WithName("Start hidden", TextMatch.StartsWith));
        Assert.Equal(LocatorStatus.NotFound, wrongType.Status);
    }

    [Fact]
    public async Task MultipleMatches_IndexSelectsInTreeOrder()
    {
        var a = await _f.BoundsOfAsync("ApplyButtonA");
        var b = await _f.BoundsOfAsync("ApplyButtonB");
        Assert.NotEqual(a, b);

        var first = await _f.ResolveAsync(ElementLocator.ByName("Apply"));
        var index0 = await _f.ResolveAsync(ElementLocator.ByName("Apply").At(0));
        var index1 = await _f.ResolveAsync(ElementLocator.ByName("Apply").At(1));
        Assert.Equal(a, Assert.Single(first.Bounds));
        Assert.Equal(a, Assert.Single(index0.Bounds));
        Assert.Equal(b, Assert.Single(index1.Bounds));

        var tooFar = await _f.ResolveAsync(ElementLocator.ByControlType("Button").WithName("Apply").At(2));
        Assert.Equal(LocatorStatus.NotFound, tooFar.Status);
        Assert.Contains("Only 2 element(s) matched", tooFar.Message);
    }

    [Fact]
    public async Task Ancestor_RestrictsSearch()
    {
        var expected = await _f.BoundsOfAsync("Row3");
        var r = await _f.ResolveAsync(ElementLocator.ByName("Row 3").Within(ElementLocator.ByAutomationId("ScrollArea")));
        Assert.Equal(LocatorStatus.Resolved, r.Status);
        Assert.Equal(expected, Assert.Single(r.Bounds));

        var outside = await _f.ResolveAsync(ElementLocator.ByAutomationId("StartupCheck").Within(ElementLocator.ByAutomationId("ScrollArea")));
        Assert.Equal(LocatorStatus.NotFound, outside.Status);

        var missingAncestor = await _f.ResolveAsync(ElementLocator.ByAutomationId("Row3").Within(ElementLocator.ByAutomationId("NoSuchPanel")));
        Assert.Equal(LocatorStatus.NotFound, missingAncestor.Status);
        Assert.Contains("NoSuchPanel", missingAncestor.Message);
    }

    [Fact]
    public async Task MissingElement_IsNotFoundWithDiagnostics()
    {
        var r = await _f.ResolveAsync(ElementLocator.ByAutomationId("DoesNotExist"));
        Assert.Equal(LocatorStatus.NotFound, r.Status);
        Assert.False(r.IsResolved);
        Assert.Empty(r.Bounds);
        Assert.Contains("DoesNotExist", r.Message);
        Assert.Contains("searched", r.Message);
        Assert.DoesNotContain("MaxNodes", r.Message);
    }

    [Fact]
    public async Task BoundedSearch_ReportsTheBound()
    {
        var byNodes = await _f.ResolveAsync(ElementLocator.ByAutomationId("Row40") with { MaxNodes = 3 });
        Assert.Equal(LocatorStatus.NotFound, byNodes.Status);
        Assert.Contains("MaxNodes", byNodes.Message);
        Assert.Contains("searched 3 node(s)", byNodes.Message);

        var byDepth = await _f.ResolveAsync(ElementLocator.ByAutomationId("Row40") with { MaxDepth = 1 });
        Assert.Equal(LocatorStatus.NotFound, byDepth.Status);
        Assert.Contains("MaxDepth", byDepth.Message);
    }

    [Fact]
    public async Task EmptySelector_IsAnError()
    {
        var r = await _f.ResolveAsync(new UiAutomationLocator());
        Assert.Equal(LocatorStatus.Error, r.Status);
    }

    [Fact]
    public async Task Cancellation_PreCancelledTokenThrows()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _f.Resolver.ResolveAsync(ElementLocator.ByAutomationId("StartupCheck"), _f.Context(), cts.Token).AsTask());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _f.Resolver.ResolveAsync(ElementLocator.ByText("secret token"), _f.Context(), cts.Token).AsTask());
    }

    [Fact]
    public async Task BoundingRectangle_FollowsLayoutChanges()
    {
        var scale = _f.Window.Dpi.Scale;
        var before = await _f.ResolveAsync(ElementLocator.ByAutomationId("MovingLabel"));
        var b0 = Assert.Single(before.Bounds);

        await _f.InvokeAsync("MoveButton");
        LocatorResolution last = before;
        var after = await Eventually.UntilAsync(async () => last = await _f.ResolveAsync(ElementLocator.ByAutomationId("MovingLabel"), last),
            r => r.IsResolved && r.Bounds[0].X != b0.X, TimeSpan.FromSeconds(5), "MovingLabel to move");
        var b1 = after.Bounds[0];

        // The cached element was re-validated, not searched again.
        Assert.Same(before.State, after.State);
        Assert.InRange(Math.Abs(b1.X - b0.X), (40 * scale) - 2, (40 * scale) + 2);
        Assert.Equal(b0.Y, b1.Y);
        Assert.Equal(b0.Size, b1.Size);

        // Restore.
        await _f.InvokeAsync("MoveButton");
        await Eventually.UntilAsync(() => _f.ResolveAsync(ElementLocator.ByAutomationId("MovingLabel")),
            r => r.IsResolved && r.Bounds[0].X == b0.X, TimeSpan.FromSeconds(5), "MovingLabel to move back");
    }

    [Fact]
    public async Task Offscreen_RowOutsideScrollViewport_ThenScrolledIntoView()
    {
        await _f.ScrollAreaToAsync(0);
        var locator = ElementLocator.ByAutomationId("Row40");
        var hidden = await Eventually.UntilAsync(() => _f.ResolveAsync(locator), r => r.Status == LocatorStatus.Offscreen,
            TimeSpan.FromSeconds(5), "Row40 to be offscreen");
        Assert.Empty(hidden.Bounds);
        Assert.False(hidden.IsResolved);

        try
        {
            await _f.ScrollAreaToAsync(100);
            var visible = await Eventually.UntilAsync(() => _f.ResolveAsync(locator, hidden), r => r.IsResolved,
                TimeSpan.FromSeconds(5), "Row40 to scroll into view");
            var area = await _f.BoundsOfAsync("ScrollArea");
            var row = Assert.Single(visible.Bounds);
            Assert.True(area.Inflate(1).Contains(row), $"{row} should be inside the scroll area {area}");
        }
        finally
        {
            await _f.ScrollAreaToAsync(0);
        }
    }

    [Fact]
    public async Task CollapsedElement_IsNotResolved_AndComesBack()
    {
        var locator = ElementLocator.ByAutomationId("CollapsibleLabel");
        var initial = await _f.ResolveAsync(locator);
        if (!initial.IsResolved)
        {
            await _f.InvokeAsync("ToggleVisibilityButton");
            initial = await Eventually.UntilAsync(() => _f.ResolveAsync(locator), r => r.IsResolved, TimeSpan.FromSeconds(5), "label visible");
        }
        var visibleBounds = Assert.Single(initial.Bounds);

        await _f.InvokeAsync("ToggleVisibilityButton");
        LocatorResolution last = initial;
        var collapsed = await Eventually.UntilAsync(async () => last = await _f.ResolveAsync(locator, last), r => !r.IsResolved,
            TimeSpan.FromSeconds(5), "label collapsed");
        Assert.True(collapsed.Status is LocatorStatus.Offscreen or LocatorStatus.NotFound, $"{collapsed.Status}: {collapsed.Message}");
        Assert.Empty(collapsed.Bounds);

        await _f.InvokeAsync("ToggleVisibilityButton");
        var back = await Eventually.UntilAsync(async () => last = await _f.ResolveAsync(locator, last), r => r.IsResolved,
            TimeSpan.FromSeconds(5), "label visible again");
        Assert.Equal(visibleBounds, Assert.Single(back.Bounds));
    }

    [Fact]
    public async Task CacheDisabled_StillResolves_WithoutState()
    {
        using var resolver = new UiAutomationLocatorResolver(new UiAutomationOptions { CacheElements = false });
        var r = await resolver.ResolveAsync(ElementLocator.ByAutomationId("TelemetryCheck"), _f.Context(), CancellationToken.None);
        Assert.Equal(LocatorStatus.Resolved, r.Status);
        Assert.Null(r.State);
    }

    [Fact]
    public async Task UnknownWindow_IsUnavailable()
    {
        var window = _f.Window with { Handle = 0x7FFF_FFF0 };
        var r = await _f.Resolver.ResolveAsync(ElementLocator.ByAutomationId("StartupCheck"), _f.Context(window: window), CancellationToken.None);
        Assert.Equal(LocatorStatus.Unavailable, r.Status);
        Assert.Contains("UI Automation unavailable", r.Message);
    }

    [Fact]
    public async Task Pipeline_FallbackChainUsesUiAutomation()
    {
        var pipeline = new LocatorPipeline([_f.Resolver, new GeometricLocatorResolver()]);
        var expected = await _f.BoundsOfAsync("StartupCheck");
        var chain = ElementLocator.Smart(automationId: "Missing", name: StartupText, windowRelativeFallback: new PixelRect(0, 0, 10, 10));
        var r = await pipeline.ResolveAsync(chain, _f.Context(), CancellationToken.None);
        Assert.True(r.IsResolved, r.Message);
        Assert.Equal(expected, Assert.Single(r.Bounds));
        Assert.IsType<UiAutomationLocator>(r.ResolvedBy);
        Assert.Equal(LocatorStatus.NotFound, r.Attempts[0].Status);
    }
}
