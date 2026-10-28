using WindowVeil.Automation;
using WindowVeil.Locators;

namespace WindowVeil.UiTests;

[Collection(DemoTargetCollection.Name)]
[Trait("Category", "Desktop")]
public sealed class UiAutomationInspectorTests
{
    private readonly DemoTargetFixture _f;

    public UiAutomationInspectorTests(DemoTargetFixture fixture) => _f = fixture;

    [Fact]
    public async Task ElementFromPoint_AtCheckBoxCenter_ReturnsCheckBoxWithSuggestions()
    {
        using var inspector = new UiAutomationInspector();
        var bounds = await _f.BoundsOfAsync("StartupCheck");

        var info = await inspector.ElementFromPointAsync(bounds.Center);

        Assert.NotNull(info);
        Assert.Equal("StartupCheck", info.AutomationId);
        Assert.Equal("CheckBox", info.ControlType);
        Assert.Equal("Start automatically when I sign in to Windows", info.Name);
        Assert.Equal(bounds, info.Bounds);
        Assert.Equal(_f.Handle, info.TopLevelWindowHandle);
        Assert.Equal(_f.Window.ProcessId, info.ProcessId);
        Assert.Contains(ElementLocator.ByAutomationId("StartupCheck"), info.SuggestedLocators);
        Assert.Equal(ElementLocator.ByAutomationId("StartupCheck"), info.SuggestedLocators[0]);
        Assert.IsType<WindowRelativeLocator>(info.SuggestedLocators[^1]);
        Assert.Contains("StartupCheck", info.Describe());

        // Every suggestion resolves to the same element (the window-relative one to the same rectangle).
        var pipeline = new LocatorPipeline([_f.Resolver, new GeometricLocatorResolver()]);
        foreach (var suggestion in info.SuggestedLocators)
        {
            var r = await pipeline.ResolveAsync(suggestion, _f.Context(), CancellationToken.None);
            Assert.True(r.IsResolved, $"{suggestion}: {r.Status} {r.Message}");
            Assert.Equal(bounds, Assert.Single(r.Bounds));
        }
    }

    [Fact]
    public async Task TreeNavigation_RootChildrenParent()
    {
        using var inspector = new UiAutomationInspector();

        var root = await inspector.GetRootAsync(_f.Handle);
        Assert.NotNull(root);
        Assert.Equal("Window", root.ControlType);
        Assert.Equal(_f.Handle, root.NativeWindowHandle);

        var children = await inspector.GetChildrenAsync(root);
        var check = Assert.Single(children, c => c.AutomationId == "StartupCheck");
        Assert.Equal("CheckBox", check.ControlType);

        var parent = await inspector.GetParentAsync(check);
        Assert.NotNull(parent);
        Assert.Equal(root.RuntimeId, parent.RuntimeId);

        var limited = await inspector.GetChildrenAsync(root, maxChildren: 2);
        Assert.Equal(2, limited.Count);

        var notes = Assert.Single(children, c => c.AutomationId == "NotesBox");
        Assert.True(notes.SupportsTextPattern);
        Assert.Contains(notes.SuggestedLocators, l => l is TextLocator);

        var refreshed = await inspector.RefreshAsync(check);
        Assert.NotNull(refreshed);
        Assert.Equal(check.Bounds, refreshed.Bounds);

        Assert.Null(await inspector.GetRootAsync(0x7FFF_FFF0));
    }

    [Fact]
    public async Task UnknownElement_ReturnsEmpty()
    {
        using var inspector = new UiAutomationInspector();
        var ghost = new AutomationElementInfo { RuntimeId = "1.2.3" };
        Assert.Empty(await inspector.GetChildrenAsync(ghost));
        Assert.Null(await inspector.GetParentAsync(ghost));
        Assert.Null(await inspector.RefreshAsync(ghost));
    }
}
