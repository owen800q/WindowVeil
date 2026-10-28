using WindowVeil.Appearance;
using WindowVeil.Imaging;
using WindowVeil.Locators;
using WindowVeil.Rendering;
using WindowVeil.Text;

namespace WindowVeil.UnitTests.Core;

public class OverlayRuleTests
{
    private static OverlayRule Valid() => new() { Id = "r", Target = ElementLocator.ByAutomationId("x"), Appearance = MaskAppearance.Solid("#000") };

    public static TheoryData<string, OverlayRule> InvalidRules() => new()
    {
        { "empty id", Valid() with { Id = " " } },
        { "no target", Valid() with { Target = null! } },
        { "no appearance", Valid() with { Appearance = null! } },
        { "no placement", Valid() with { Placement = null! } },
        { "empty uia", Valid() with { Target = new UiAutomationLocator() } },
        { "zero depth", Valid() with { Target = ElementLocator.ByAutomationId("x") with { MaxDepth = 0 } } },
        { "zero nodes", Valid() with { Target = ElementLocator.ByAutomationId("x") with { MaxNodes = -1 } } },
        { "ocr confidence", Valid() with { Target = ElementLocator.ByOcrText("x") with { MinConfidence = 1.5 } } },
        { "placement width", Valid() with { Placement = OverlayPlacement.Right() with { Width = 0 } } },
        { "placement height", Valid() with { Placement = OverlayPlacement.Right() with { Height = -3 } } },
    };

    [Theory]
    [MemberData(nameof(InvalidRules))]
    public void Validate_InvalidRule_ThrowsInvalidRule(string reason, OverlayRule rule)
    {
        var ex = Assert.Throws<WindowVeilException>(rule.Validate);
        Assert.True(ex.Code == WindowVeilErrorCode.InvalidRule, reason);
    }

    [Fact]
    public void Validate_ValidRuleAndDefaults()
    {
        var rule = new OverlayRule { Target = ElementLocator.Rectangle(0, 0, 1, 1), Appearance = MaskAppearance.Solid("#000") };
        rule.Validate();
        Assert.Equal(32, rule.Id.Length);
        Assert.Same(OverlayPlacement.CoverTarget, rule.Placement);
        Assert.True(rule.FollowTarget);
        Assert.True(rule.Enabled);
        Assert.Equal(OverlayInputMode.PassThrough, rule.InputMode);
        Assert.Equal(OverlayFocusPolicy.NoActivate, rule.FocusPolicy);
        Assert.Equal(OverlayZOrder.AboveTarget, rule.ZOrder);
        Assert.Equal(OverlayVisibility.WhenTargetVisible, rule.Visibility);
        Assert.NotEqual(rule.Id, new OverlayRule { Target = rule.Target, Appearance = rule.Appearance }.Id);
    }

    [Fact]
    public void MaskRule_AppliesBehaviorPresets()
    {
        var locator = ElementLocator.ByAutomationId("StartupCheck");
        var follow = MaskRule.Create(locator, MaskAppearance.Solid("#202020"), id: "m");
        Assert.Equal("m", follow.Id);
        Assert.True(follow.FollowTarget);
        Assert.Equal(OverlayInputMode.PassThrough, follow.InputMode);
        Assert.Equal(locator, follow.Target);

        var stat = MaskRule.Create(locator, MaskAppearance.Solid("#202020"), MaskBehavior.Static);
        Assert.False(stat.FollowTarget);

        var blocking = MaskRule.Create(locator, MaskAppearance.Solid("#202020"), MaskBehavior.FollowTarget.BlockingInput());
        Assert.Equal(OverlayInputMode.Interactive, blocking.InputMode);
        Assert.True(blocking.FollowTarget);
        Assert.Equal(OverlayInputMode.PassThrough, MaskBehavior.FollowTarget.InputMode); // presets are immutable
    }
}

public class AppearanceTests
{
    [Fact]
    public void MaskAppearance_KindDependsOnOpacity_AndAlphaIsCarriedByOpacity()
    {
        Assert.Equal("solid", MaskAppearance.Solid("#202020").Kind);
        var t = MaskAppearance.Transparent("#80FF0000", 0.4);
        Assert.Equal("transparent", t.Kind);
        Assert.Equal(255, t.Color.A);
        Assert.Throws<ArgumentOutOfRangeException>(() => new MaskAppearance(OverlayColor.Black, 1.1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MaskAppearance(OverlayColor.Black, double.NaN));
        Assert.Throws<FormatException>(() => MaskAppearance.Solid("nope"));
    }

    [Fact]
    public void HighlightAndSpotlight_Validate()
    {
        Assert.Equal("highlight", HighlightAppearance.Create("#F00").Kind);
        Assert.Throws<ArgumentOutOfRangeException>(() => HighlightAppearance.Create("#F00", thickness: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => HighlightAppearance.Create("#F00", opacity: -0.1));
        var s = SpotlightAppearance.Create(0.5, SpotlightScope.Monitor);
        Assert.Equal(OverlayColor.Black, s.DimColor);
        Assert.Equal(SpotlightScope.Monitor, s.Scope);
        Assert.Throws<ArgumentOutOfRangeException>(() => SpotlightAppearance.Create(2));
    }

    [Fact]
    public void RegisteredAppearance_ComparesParametersByValue()
    {
        var a = new RegisteredAppearance("badge") { Parameters = new Dictionary<string, string> { ["text"] = "Beta", ["tone"] = "info" } };
        var b = new RegisteredAppearance("badge") { Parameters = new Dictionary<string, string> { ["tone"] = "info", ["text"] = "Beta" } };
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.NotEqual(a, b with { Parameters = new Dictionary<string, string> { ["text"] = "Beta" } });
        Assert.NotEqual(a, a with { FactoryId = "other" });
    }
}

public class AppearanceRegistryTests
{
    [Fact]
    public void TryResolve_RegisteredId_PassesParametersCaseInsensitiveId()
    {
        IReadOnlyDictionary<string, string>? seen = null;
        var registry = new AppearanceRegistry().Register("Badge", p => { seen = p; return HighlightAppearance.Create(p["color"]); });
        var reg = new RegisteredAppearance("badge") { Parameters = new Dictionary<string, string> { ["color"] = "#00FF00" } };

        Assert.True(registry.TryResolve(reg, out var resolved, out var error));

        Assert.Null(error);
        Assert.Equal(HighlightAppearance.Create("#00FF00"), resolved);
        Assert.Equal("#00FF00", seen!["color"]);
        Assert.True(registry.Contains("BADGE"));
        Assert.Equal(["Badge"], registry.Ids);
    }

    [Fact]
    public void TryResolve_NonRegisteredAppearance_ReturnsItUnchanged()
    {
        var mask = MaskAppearance.Solid("#000");
        Assert.True(new AppearanceRegistry().TryResolve(mask, out var resolved, out _));
        Assert.Same(mask, resolved);
    }

    [Fact]
    public void Register_DuplicateIdIgnoringCase_Throws()
    {
        var registry = new AppearanceRegistry().Register("badge", () => MaskAppearance.Solid("#000"));
        Assert.Throws<InvalidOperationException>(() => registry.Register("BADGE", () => MaskAppearance.Solid("#000")));
        Assert.Throws<ArgumentException>(() => registry.Register(" ", () => MaskAppearance.Solid("#000")));
    }

    [Fact]
    public void TryResolve_UnknownId_FailsWithMessage()
    {
        Assert.False(new AppearanceRegistry().TryResolve(new RegisteredAppearance("ghost"), out var resolved, out var error));
        Assert.Null(resolved);
        Assert.Contains("ghost", error, StringComparison.Ordinal);
    }

    [Fact]
    public void TryResolve_FactoryThrows_FailsWithMessage()
    {
        var registry = new AppearanceRegistry().Register("bad", _ => throw new KeyNotFoundException("missing 'color'"));
        Assert.False(registry.TryResolve(new RegisteredAppearance("bad"), out var resolved, out var error));
        Assert.Null(resolved);
        Assert.Contains("missing 'color'", error, StringComparison.Ordinal);
    }

    [Fact]
    public void TryResolve_FactoryReturnsInvalidAppearance_Fails()
    {
        var registry = new AppearanceRegistry()
            .Register("null", () => null!)
            .Register("loop", () => new RegisteredAppearance("loop"));
        Assert.False(registry.TryResolve(new RegisteredAppearance("null"), out _, out var e1));
        Assert.False(registry.TryResolve(new RegisteredAppearance("loop"), out _, out var e2));
        Assert.Contains("invalid appearance", e1, StringComparison.Ordinal);
        Assert.Contains("invalid appearance", e2, StringComparison.Ordinal);
    }
}

public class OverlayFrameTests
{
    [Fact]
    public void IsEquivalentTo_ComparesGeometryStructurally()
    {
        var w = new Platform.TargetWindowInfo { Handle = 5, ProcessId = 1, Bounds = new(0, 0, 10, 10), ClientBounds = new(0, 0, 10, 10) };
        var a = OverlayFrame.Show([new(1, 1, 2, 2)], [new(1, 1, 2, 2)], w);
        var b = OverlayFrame.Show([new(1, 1, 2, 2)], [new(1, 1, 2, 2)], w);
        Assert.True(a.IsEquivalentTo(b));
        Assert.False(a.IsEquivalentTo(OverlayFrame.Show([new(1, 1, 3, 2)], [new(1, 1, 2, 2)], w)));
        Assert.False(a.IsEquivalentTo(null));
        Assert.False(OverlayFrame.Show([], [], w).Visible);
        Assert.False(OverlayFrame.Hidden.Visible);
        Assert.Equal(TextMatch.Exact, ElementLocator.ByName("x").Name!.Mode);
    }
}
