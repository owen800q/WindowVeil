using WindowVeil.Geometry;
using WindowVeil.Locators;

namespace WindowVeil.UiTests;

/// <summary>Geometry is clipped to what is actually visible: scroll viewports and the window, but not popups.</summary>
[Collection(DemoTargetCollection.Name)]
[Trait("Category", "Desktop")]
public sealed class VisibilityClippingTests(DemoTargetFixture f)
{
    [Fact]
    public async Task PartiallyScrolledRow_IsClippedToTheViewport()
    {
        var area = await f.BoundsOfAsync("ScrollArea");
        foreach (var percent in new[] { 0.0, 3.0, 7.0 })
        {
            await f.ScrollAreaToAsync(percent);
            try
            {
                for (int i = 1; i <= 40; i++)
                {
                    var raw = await f.RawBoundsAsync($"Row{i}");
                    if (raw.IsEmpty || !raw.IntersectsWith(area) || area.Contains(raw)) continue;

                    // Row i straddles the viewport edge: only its visible part may be returned.
                    var resolved = await f.ResolveAsync(ElementLocator.ByAutomationId($"Row{i}"));
                    Assert.True(resolved.IsResolved, $"Row{i}: {resolved.Status} {resolved.Message}");
                    var bounds = Assert.Single(resolved.Bounds);
                    Assert.Equal(raw.Intersect(area), bounds);
                    Assert.True(bounds.Height < raw.Height, $"Row{i} {bounds} should be shorter than its full rectangle {raw}");
                    Assert.Contains("Clipped to the visible area", resolved.Message);
                    return;
                }
            }
            finally
            {
                await f.ScrollAreaToAsync(0);
            }
        }
        Assert.Fail("No row straddled the ScrollArea viewport at any tested scroll position.");
    }

    [Fact]
    public async Task ComboBoxDropDownItems_AreNotClippedToTheirHostOrTheWindow()
    {
        var combo = await f.BoundsOfAsync("ColorCombo");
        await f.SetExpandedAsync("ColorCombo", true);
        try
        {
            var item = await Eventually.UntilAsync(() => f.ResolveAsync(ElementLocator.ByAutomationId("Color8")), r => r.IsResolved,
                TimeSpan.FromSeconds(5), "drop-down item to resolve");
            var bounds = Assert.Single(item.Bounds);
            var raw = await f.RawBoundsAsync("Color8");
            Assert.False(combo.Contains(bounds), $"item {bounds} lies outside its ComboBox {combo}, in the popup");
            Assert.Equal(raw, bounds); // not clipped to the closed ComboBox or to the window
        }
        finally
        {
            await f.SetExpandedAsync("ColorCombo", false);
        }
    }
}
