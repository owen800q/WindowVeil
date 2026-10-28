using System.Windows.Automation;
using WindowVeil.Automation;
using WindowVeil.Geometry;
using WindowVeil.Locators;
using WindowVeil.Platform;

namespace WindowVeil.UiTests;

/// <summary>
/// Launches one demo target per test collection at a known position and provides a resolver, a UIA driver thread
/// and helpers. Tests that change the target's state (moving, collapsing, scrolling) restore it.
/// </summary>
public sealed class DemoTargetFixture : IAsyncLifetime
{
    /// <summary>Window left position in DIPs.</summary>
    public const int LeftDip = 60;

    /// <summary>Window top position in DIPs.</summary>
    public const int TopDip = 60;

    private DemoTargetProcess? _target;

    /// <summary>Thread used to drive the target through UI Automation patterns (Invoke, Scroll).</summary>
    public UiAutomationThread Uia { get; } = new("UiTests driver");

    /// <summary>The resolver under test.</summary>
    public UiAutomationLocatorResolver Resolver { get; } = new();

    internal DemoTargetProcess Target => _target ?? throw new InvalidOperationException("The demo target is not running.");

    /// <summary>Main window handle.</summary>
    public nint Handle => Target.Handle;

    /// <summary>Fresh snapshot of the target window.</summary>
    public TargetWindowInfo Window => Target.Snapshot();

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        await Uia.Started;
        _target = await DemoTargetProcess.StartAsync(Uia, LeftDip, TopDip, $"WindowVeil Demo Target (UiTests {Environment.ProcessId})");
    }

    /// <inheritdoc />
    public Task DisposeAsync()
    {
        _target?.Dispose();
        Resolver.Dispose();
        Uia.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>Creates a locator context for the current window snapshot.</summary>
    public LocatorContext Context(LocatorResolution? previous = null, ResolveReason reason = ResolveReason.Poll, TargetWindowInfo? window = null) =>
        new(window ?? Window, reason) { Previous = previous };

    /// <summary>Resolves a locator against the demo target.</summary>
    public Task<LocatorResolution> ResolveAsync(ElementLocator locator, LocatorResolution? previous = null, CancellationToken cancellationToken = default) =>
        Resolver.ResolveAsync(locator, Context(previous, previous is null ? ResolveReason.Initial : ResolveReason.Poll), cancellationToken).AsTask();

    /// <summary>Resolves an AutomationId that must be found and returns its single rectangle.</summary>
    public async Task<PixelRect> BoundsOfAsync(string automationId)
    {
        var r = await ResolveAsync(ElementLocator.ByAutomationId(automationId));
        Assert.True(r.Status == LocatorStatus.Resolved, $"{automationId}: {r.Status} {r.Message}");
        return Assert.Single(r.Bounds);
    }

    /// <summary>Invokes a button (InvokePattern) by AutomationId. WPF processes the click asynchronously.</summary>
    public Task InvokeAsync(string automationId) => Uia.InvokeAsync(_ =>
    {
        var element = Find(automationId);
        ((InvokePattern)element.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
    });

    /// <summary>Scrolls ScrollArea vertically to a percentage (0..100) through ScrollPattern.</summary>
    public Task ScrollAreaToAsync(double verticalPercent) => Uia.InvokeAsync(_ =>
    {
        var element = Find("ScrollArea");
        ((ScrollPattern)element.GetCurrentPattern(ScrollPattern.Pattern)).SetScrollPercent(ScrollPattern.NoScroll, verticalPercent);
    });

    /// <summary>The raw UI Automation bounding rectangle (no WindowVeil clipping), rounded outward.</summary>
    public Task<PixelRect> RawBoundsAsync(string automationId) => Uia.InvokeAsync(_ =>
    {
        var r = Find(automationId).Current.BoundingRectangle;
        return r.IsEmpty ? PixelRect.Empty : PixelRect.FromRectDOutward(new RectD(r.X, r.Y, r.Width, r.Height));
    });

    /// <summary>Expands or collapses an element through ExpandCollapsePattern.</summary>
    public Task SetExpandedAsync(string automationId, bool expanded) => Uia.InvokeAsync(_ =>
    {
        var p = (ExpandCollapsePattern)Find(automationId).GetCurrentPattern(ExpandCollapsePattern.Pattern);
        if (expanded) p.Expand(); else p.Collapse();
    });

    private AutomationElement Find(string automationId)
    {
        var root = AutomationElement.FromHandle(Handle);
        return root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, automationId))
            ?? throw new InvalidOperationException($"Element '{automationId}' not found in the demo target.");
    }
}

/// <summary>All desktop tests share one demo target instance and run sequentially.</summary>
[CollectionDefinition(Name)]
public sealed class DemoTargetCollection : ICollectionFixture<DemoTargetFixture>
{
    /// <summary>Collection name.</summary>
    public const string Name = "DemoTarget";
}
