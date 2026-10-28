using System.Globalization;
using WindowVeil.Appearance;
using WindowVeil.Geometry;
using WindowVeil.Imaging;
using WindowVeil.Locators;
using WindowVeil.Text;

namespace WindowVeil.Editor.Model;

/// <summary>Result of <see cref="RuleBuilder.TryBuild"/>.</summary>
/// <param name="Rule">The rule, or null when the design has errors.</param>
/// <param name="Errors">Human-readable validation errors.</param>
public sealed record RuleBuildResult(OverlayRule? Rule, IReadOnlyList<string> Errors)
{
    /// <summary>True when <see cref="Rule"/> is available.</summary>
    public bool Succeeded => Rule is not null;
}

/// <summary>
/// Converts between the designer state (<see cref="RuleDesign"/>) and WindowVeil <see cref="OverlayRule"/>s.
/// Pure logic: no WPF, no I/O, no engine.
/// </summary>
public static class RuleBuilder
{
    /// <summary>Default UI Automation search bounds (mirrors <see cref="UiAutomationLocator"/> defaults).</summary>
    private static readonly UiAutomationLocator DefaultUia = new() { AutomationId = "x" };

    /// <summary>Builds a rule, collecting every validation error instead of throwing.</summary>
    public static RuleBuildResult TryBuild(RuleDesign design)
    {
        ArgumentNullException.ThrowIfNull(design);
        var errors = new List<string>();
        var locator = BuildLocator(design, errors);
        var appearance = BuildAppearance(design, errors);
        var placement = BuildPlacement(design, errors);
        if (locator is null || appearance is null || placement is null || errors.Count > 0)
            return new RuleBuildResult(null, errors);

        var rule = new OverlayRule
        {
            Id = string.IsNullOrWhiteSpace(design.Id) ? NewId() : design.Id.Trim(),
            Name = NullIfEmpty(design.Name),
            Group = NullIfEmpty(design.Group),
            Enabled = design.Enabled,
            Target = locator,
            Appearance = appearance,
            Placement = placement,
            InputMode = design.InputMode,
            FocusPolicy = design.FocusPolicy,
            ZOrder = design.ZOrder,
            FollowTarget = design.FollowTarget,
            Visibility = design.Visibility,
            Priority = design.Priority,
        };
        try
        {
            rule.Validate();
        }
        catch (WindowVeilException ex)
        {
            errors.Add(ex.Message);
            return new RuleBuildResult(null, errors);
        }
        return new RuleBuildResult(rule, errors);
    }

    /// <summary>Creates a short unique rule id.</summary>
    public static string NewId() => "rule-" + Guid.NewGuid().ToString("n")[..8];

    /// <summary>Builds the locator part of a design; adds messages to <paramref name="errors"/> on failure.</summary>
    public static ElementLocator? BuildLocator(RuleDesign design, ICollection<string> errors)
    {
        ArgumentNullException.ThrowIfNull(design);
        ArgumentNullException.ThrowIfNull(errors);
        ElementLocator? primary;
        switch (design.LocatorMode)
        {
            case LocatorMode.UiAutomation:
            {
                var uia = new UiAutomationLocator
                {
                    AutomationId = NullIfEmpty(design.AutomationId),
                    Name = string.IsNullOrEmpty(design.ElementName) ? null : new TextQuery(design.ElementName, design.NameMatch, design.NameIgnoreCase),
                    ControlType = NullIfEmpty(design.ControlType),
                    ClassName = NullIfEmpty(design.ClassName),
                    Index = design.Index,
                };
                if (uia.IsEmpty) { errors.Add("A UI Automation locator needs at least one of AutomationId, Name, ControlType or ClassName."); return null; }
                if (design.Index < 0) { errors.Add("The match index cannot be negative."); return null; }
                if (!TryValidateRegex(design.NameMatch, design.ElementName, errors)) return null;
                primary = uia;
                break;
            }
            case LocatorMode.Text:
            {
                if (string.IsNullOrWhiteSpace(design.Text)) { errors.Add("Enter the text to locate."); return null; }
                if (!TryValidateRegex(design.TextMatch, design.Text, errors)) return null;
                var text = ElementLocator.ByText(design.Text, design.TextMatch, design.TextIgnoreCase);
                if (!string.IsNullOrWhiteSpace(design.TextScopeAutomationId))
                    text = text with { Scope = ElementLocator.ByAutomationId(design.TextScopeAutomationId.Trim()) };
                primary = text;
                break;
            }
            case LocatorMode.WindowRectangle:
                return BuildRectangle(design, errors);
            case LocatorMode.Existing:
                if (design.ExistingLocator is null) errors.Add("No locator is set. Pick an element or draw a rectangle.");
                return design.ExistingLocator;
            default:
                errors.Add($"Unknown locator mode {design.LocatorMode}.");
                return null;
        }

        if (!design.RectangleFallback) return primary;
        var fallback = BuildRectangle(design, errors);
        return fallback is null ? null : primary.Or(fallback);
    }

    private static ElementLocator? BuildRectangle(RuleDesign design, ICollection<string> errors)
    {
        var r = design.Rectangle;
        if (r.Width <= 0 || r.Height <= 0) { errors.Add("The rectangle needs a positive width and height (draw one or enter W/H)."); return null; }
        return ElementLocator.WindowRelative(r.X, r.Y, r.Width, r.Height, design.Anchor);
    }

    private static bool TryValidateRegex(TextMatch match, string pattern, ICollection<string> errors)
    {
        if (match != TextMatch.Regex || string.IsNullOrEmpty(pattern)) return true;
        try
        {
            _ = new System.Text.RegularExpressions.Regex(pattern, System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromMilliseconds(100));
            return true;
        }
        catch (ArgumentException ex)
        {
            errors.Add($"Invalid regular expression: {ex.Message}");
            return false;
        }
    }

    /// <summary>Builds the appearance part of a design; adds messages to <paramref name="errors"/> on failure.</summary>
    public static OverlayAppearance? BuildAppearance(RuleDesign design, ICollection<string> errors)
    {
        ArgumentNullException.ThrowIfNull(design);
        ArgumentNullException.ThrowIfNull(errors);
        if (design.AppearanceType is AppearanceType.GalleryItem or AppearanceType.RegisteredFactory)
        {
            if (string.IsNullOrWhiteSpace(design.FactoryId)) { errors.Add("Choose a gallery item or enter a registered factory id."); return null; }
            return new RegisteredAppearance(design.FactoryId.Trim()) { Parameters = design.FactoryParameters };
        }
        if (!OverlayColor.TryParse(design.Color, out var color)) { errors.Add($"'{design.Color}' is not a valid color. Use #RGB, #RRGGBB or #AARRGGBB."); return null; }
        if (double.IsNaN(design.Opacity) || design.Opacity < 0 || design.Opacity > 1) { errors.Add("Opacity must be between 0 and 1."); return null; }
        switch (design.AppearanceType)
        {
            case AppearanceType.SolidMask:
                return new MaskAppearance(color);
            case AppearanceType.TransparentMask:
                return new MaskAppearance(color, Math.Min(design.Opacity, 0.99));
            case AppearanceType.Highlight:
                if (!(design.Thickness > 0) || design.Thickness > 100) { errors.Add("Highlight thickness must be between 0 and 100 DIP."); return null; }
                return new HighlightAppearance(color, design.Thickness, design.Opacity);
            case AppearanceType.Spotlight:
                return new SpotlightAppearance(color, design.Opacity, design.SpotlightScope);
            default:
                errors.Add($"Unknown appearance type {design.AppearanceType}.");
                return null;
        }
    }

    /// <summary>Builds the placement part of a design; adds messages to <paramref name="errors"/> on failure.</summary>
    public static OverlayPlacement? BuildPlacement(RuleDesign design, ICollection<string> errors)
    {
        ArgumentNullException.ThrowIfNull(design);
        ArgumentNullException.ThrowIfNull(errors);
        if (!double.IsFinite(design.OffsetX) || !double.IsFinite(design.OffsetY)) { errors.Add("Offsets must be numbers."); return null; }
        if (design.Width is <= 0 || design.Height is <= 0) { errors.Add("Width and height must be positive (leave them empty to size to the content)."); return null; }
        return new OverlayPlacement
        {
            Mode = design.PlacementMode,
            OffsetX = design.OffsetX,
            OffsetY = design.OffsetY,
            Padding = design.Padding,
            Width = design.Width,
            Height = design.Height,
            Alignment = design.Alignment,
            MultiRect = design.MultiRect,
        };
    }

    /// <summary>
    /// Loads a rule into designer state. <paramref name="isGalleryId"/> decides whether a registered appearance is shown
    /// as a gallery item or as a free factory id. Locators the designer cannot edit are kept as <see cref="LocatorMode.Existing"/>.
    /// </summary>
    public static RuleDesign FromRule(OverlayRule rule, Func<string, bool>? isGalleryId = null)
    {
        ArgumentNullException.ThrowIfNull(rule);
        var design = new RuleDesign
        {
            Id = rule.Id,
            Name = rule.Name ?? "",
            Group = rule.Group ?? "",
            Enabled = rule.Enabled,
            InputMode = rule.InputMode,
            FocusPolicy = rule.FocusPolicy,
            ZOrder = rule.ZOrder,
            FollowTarget = rule.FollowTarget,
            Visibility = rule.Visibility,
            Priority = rule.Priority,
            PlacementMode = rule.Placement.Mode,
            OffsetX = rule.Placement.OffsetX,
            OffsetY = rule.Placement.OffsetY,
            Padding = rule.Placement.Padding,
            Width = rule.Placement.Width,
            Height = rule.Placement.Height,
            Alignment = rule.Placement.Alignment,
            MultiRect = rule.Placement.MultiRect,
        };
        design = WithLocator(design, rule.Target);
        design = rule.Appearance switch
        {
            MaskAppearance m => design with
            {
                AppearanceType = m.Opacity >= 1 ? AppearanceType.SolidMask : AppearanceType.TransparentMask,
                Color = m.Color.ToString(),
                Opacity = m.Opacity,
            },
            HighlightAppearance h => design with { AppearanceType = AppearanceType.Highlight, Color = h.Color.ToString(), Opacity = h.Opacity, Thickness = h.Thickness },
            SpotlightAppearance s => design with { AppearanceType = AppearanceType.Spotlight, Color = s.DimColor.ToString(), Opacity = s.Opacity, SpotlightScope = s.Scope },
            RegisteredAppearance r => design with
            {
                AppearanceType = isGalleryId?.Invoke(r.FactoryId) == true ? AppearanceType.GalleryItem : AppearanceType.RegisteredFactory,
                FactoryId = r.FactoryId,
                FactoryParameters = r.Parameters,
            },
            _ => design with { AppearanceType = AppearanceType.RegisteredFactory, FactoryId = "" },
        };
        return design;
    }

    /// <summary>
    /// Returns <paramref name="design"/> with its locator fields set from <paramref name="locator"/> (used by
    /// "Use as target" and when loading rules). Appearance, placement and behavior are unchanged.
    /// </summary>
    public static RuleDesign WithLocator(RuleDesign design, ElementLocator locator)
    {
        ArgumentNullException.ThrowIfNull(design);
        ArgumentNullException.ThrowIfNull(locator);
        var cleared = design with
        {
            AutomationId = "", ElementName = "", NameMatch = TextMatch.Exact, NameIgnoreCase = false, ControlType = "", ClassName = "", Index = 0,
            Text = "", TextMatch = TextMatch.Contains, TextIgnoreCase = true, TextScopeAutomationId = "",
            RectangleFallback = false, ExistingLocator = null,
        };

        if (locator is FallbackLocator { Locators.Count: 2 } chain && chain.Locators[1] is WindowRelativeLocator fallbackRect &&
            chain.Locators[0] is UiAutomationLocator or TextLocator && chain.ContinueOnError)
        {
            var primary = WithLocator(cleared, chain.Locators[0]);
            if (primary.LocatorMode != LocatorMode.Existing)
                return primary with { RectangleFallback = true, Rectangle = fallbackRect.Bounds, Anchor = fallbackRect.Anchor };
        }

        switch (locator)
        {
            case UiAutomationLocator uia when IsSimpleUia(uia):
                return cleared with
                {
                    LocatorMode = LocatorMode.UiAutomation,
                    AutomationId = uia.AutomationId ?? "",
                    ElementName = uia.Name?.Pattern ?? "",
                    NameMatch = uia.Name?.Mode ?? TextMatch.Exact,
                    NameIgnoreCase = uia.Name?.IgnoreCase ?? false,
                    ControlType = uia.ControlType ?? "",
                    ClassName = uia.ClassName ?? "",
                    Index = uia.Index,
                };
            case TextLocator text when text.AllMatches && text.Query.NormalizeWhitespace &&
                                       (text.Scope is null || (text.Scope.AutomationId is not null && IsSimpleUia(text.Scope) &&
                                                               text.Scope.Name is null && text.Scope.ControlType is null && text.Scope.ClassName is null && text.Scope.Index == 0)):
                return cleared with
                {
                    LocatorMode = LocatorMode.Text,
                    Text = text.Query.Pattern,
                    TextMatch = text.Query.Mode,
                    TextIgnoreCase = text.Query.IgnoreCase,
                    TextScopeAutomationId = text.Scope?.AutomationId ?? "",
                };
            case WindowRelativeLocator rect:
                return cleared with { LocatorMode = LocatorMode.WindowRectangle, Rectangle = rect.Bounds, Anchor = rect.Anchor };
            default:
                return cleared with { LocatorMode = LocatorMode.Existing, ExistingLocator = locator };
        }
    }

    private static bool IsSimpleUia(UiAutomationLocator uia) =>
        uia.Ancestor is null && uia.MaxDepth == DefaultUia.MaxDepth && uia.MaxNodes == DefaultUia.MaxNodes &&
        (uia.Name is null || uia.Name.NormalizeWhitespace);

    /// <summary>Parses padding text: one value (uniform), two (horizontal, vertical) or four (left, top, right, bottom).</summary>
    public static bool TryParseInsets(string? text, out Insets insets)
    {
        insets = Insets.Zero;
        if (string.IsNullOrWhiteSpace(text)) return true;
        var parts = text.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries);
        var values = new double[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out values[i]) || !double.IsFinite(values[i]))
                return false;
        }
        switch (values.Length)
        {
            case 1: insets = Insets.Uniform(values[0]); return true;
            case 2: insets = new Insets(values[0], values[1], values[0], values[1]); return true;
            case 4: insets = new Insets(values[0], values[1], values[2], values[3]); return true;
            default: return false;
        }
    }

    /// <summary>Formats insets the way <see cref="TryParseInsets"/> reads them (shortest form).</summary>
    public static string FormatInsets(Insets insets)
    {
        static string F(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
        if (insets.Left == insets.Top && insets.Top == insets.Right && insets.Right == insets.Bottom) return F(insets.Left);
        if (insets.Left == insets.Right && insets.Top == insets.Bottom) return $"{F(insets.Left)}, {F(insets.Top)}";
        return $"{F(insets.Left)}, {F(insets.Top)}, {F(insets.Right)}, {F(insets.Bottom)}";
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
