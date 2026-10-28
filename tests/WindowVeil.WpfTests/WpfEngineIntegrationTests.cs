using System.Windows.Controls;
using WindowVeil.Appearance;
using WindowVeil.Geometry;
using WindowVeil.Locators;
using WindowVeil.Rendering;
using WindowVeil.Wpf;
using WindowVeil.Wpf.Controls;
using WindowVeil.WpfTests.Infrastructure;

namespace WindowVeil.WpfTests;

[Trait("Category", "Desktop")]
public sealed class WpfEngineIntegrationTests
{
    [Theory]
    [InlineData(WpfHostingMode.PerOverlay)]
    [InlineData(WpfHostingMode.Grouped)]
    public async Task Wpf_button_rule_is_placed_to_the_right_of_the_target(WpfHostingMode mode)
    {
        await using var target = await TestTargetWindow.CreateAsync();
        var builder = WindowVeilEngine.CreateBuilder()
            .UseWindowSystem(new TestWindowSystem(target))
            .UseWpfOverlays(new WpfOverlayOptions { HostingMode = mode });
        await using var engine = builder.Build();
        var session = await engine.AttachAsync(target.Handle);

        var rule = session.AddRule(new OverlayRule
        {
            Id = "action-button",
            Target = ElementLocator.WindowRelative(20, 20, 100, 30),
            Appearance = WpfAppearance.Create(() => new Button { Content = "Custom Action", Width = 140, Height = 36 }),
            Placement = OverlayPlacement.Right(12, 0),
            InputMode = OverlayInputMode.Interactive,
        });
        await session.StartAsync();
        var status = await session.WaitForRuleAsync(rule.Id, s => s.State == RuleState.Active, TestHelpers.Timeout);

        var dpi = target.Dpi;
        var client = target.ClientBounds;
        var targetRect = new PixelRect(client.X + 20, client.Y + 20, 100, 30);
        var overlay = Assert.Single(status.OverlayBounds);
        Assert.Equal(targetRect.Right + CoordinateConverter.DipToPhysicalRounded(12, dpi), overlay.X);
        Assert.Equal((int)Math.Ceiling(140 * dpi.Scale - 1e-6), overlay.Width);
        Assert.Equal((int)Math.Ceiling(36 * dpi.Scale - 1e-6), overlay.Height);
        Assert.Equal(targetRect.Y + (targetRect.Height - overlay.Height) / 2, overlay.Y);
        Assert.Equal("WPF", status.Renderer);

        Assert.True(await TestHelpers.EventuallyAsync(() =>
            session.GetRuleStatus(rule.Id).OverlayHandle is var h && h != 0 && TestNative.GetWindowBounds(h) == overlay));
    }

    [Fact]
    public async Task Built_in_controls_render_through_the_engine()
    {
        await using var target = await TestTargetWindow.CreateAsync();
        await using var engine = WindowVeilEngine.CreateBuilder()
            .UseWindowSystem(new TestWindowSystem(target))
            .UseWpfOverlays()
            .Build();
        var session = await engine.AttachAsync(target.Handle);
        int clicks = 0;
        var command = new RelayCommand(_ => Interlocked.Increment(ref clicks));

        session.AddRule(new OverlayRule
        {
            Id = "badge",
            Target = ElementLocator.WindowRelative(10, 10, 120, 24),
            Appearance = WpfAppearance.Create(() => new PrivacyBadge { Text = "Hidden" }),
            Placement = OverlayPlacement.Below(0, 4),
        });
        session.AddRule(new OverlayRule
        {
            Id = "toolbar",
            Target = ElementLocator.WindowRelative(10, 80, 200, 30),
            Appearance = WpfAppearance.Create(() => new OverlayToolbar
            {
                Items = { new OverlayToolbarItem("Approve", command), new OverlayToolbarItem("Reject", command) },
            }),
            Placement = OverlayPlacement.Below(0, 4),
            InputMode = OverlayInputMode.Interactive,
        });
        await session.StartAsync();

        await session.WaitForRuleAsync("badge", s => s.State == RuleState.Active, TestHelpers.Timeout);
        var toolbar = await session.WaitForRuleAsync("toolbar", s => s.State == RuleState.Active, TestHelpers.Timeout);
        var size = Assert.Single(toolbar.OverlayBounds).Size;
        Assert.True(size.Width > size.Height, $"The toolbar should lay out horizontally, got {size}.");
    }
}
