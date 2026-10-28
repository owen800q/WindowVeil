using WindowVeil.Automation;
using WindowVeil.Locators;

namespace WindowVeil.UiTests;

/// <summary>Restarts a dedicated demo target to make a cached element stale.</summary>
[Collection(DemoTargetCollection.Name)]
[Trait("Category", "Desktop")]
public sealed class StaleElementTests
{
    private readonly DemoTargetFixture _f;

    public StaleElementTests(DemoTargetFixture fixture) => _f = fixture;

    [Fact]
    public async Task CachedElement_FromClosedProcess_IsDetected_AndReSearchWorks()
    {
        var locator = ElementLocator.ByAutomationId("StartupCheck");
        LocatorResolution cached;
        Platform.TargetWindowInfo deadWindow;

        using (var first = await DemoTargetProcess.StartAsync(_f.Uia, 120, 120, "WindowVeil Demo Target (stale A)", topmost: false))
        {
            deadWindow = first.Snapshot();
            cached = await _f.Resolver.ResolveAsync(locator, new LocatorContext(deadWindow, ResolveReason.Initial), CancellationToken.None);
            Assert.True(cached.IsResolved, cached.Message);
            Assert.IsType<UiAutomationElementState>(cached.State);
        }

        // The process is gone: the cached element is stale and the window no longer exists.
        var stale = await _f.Resolver.ResolveAsync(locator,
            new LocatorContext(deadWindow, ResolveReason.Poll) { Previous = cached }, CancellationToken.None);
        Assert.False(stale.IsResolved);
        Assert.True(stale.Status is LocatorStatus.Unavailable or LocatorStatus.NotFound, $"{stale.Status}: {stale.Message}");

        using var second = await DemoTargetProcess.StartAsync(_f.Uia, 120, 120, "WindowVeil Demo Target (stale B)", topmost: false);
        var window = second.Snapshot();
        var fresh = await _f.Resolver.ResolveAsync(locator,
            new LocatorContext(window, ResolveReason.Reconnected) { Previous = cached }, CancellationToken.None);
        Assert.True(fresh.IsResolved, fresh.Message);
        Assert.NotSame(cached.State, fresh.State);
        Assert.True(window.Bounds.Contains(fresh.Bounds[0]));
    }

    [Fact]
    public async Task ReleasedState_TriggersReSearch()
    {
        var locator = ElementLocator.ByAutomationId("TelemetryCheck");
        var first = await _f.ResolveAsync(locator);
        Assert.True(first.IsResolved, first.Message);
        ((IDisposable)first.State!).Dispose(); // what the session does when it drops a resolution

        var second = await _f.ResolveAsync(locator, first);
        Assert.True(second.IsResolved, second.Message);
        Assert.NotSame(first.State, second.State);
        Assert.Equal(first.Bounds, second.Bounds);
    }
}
