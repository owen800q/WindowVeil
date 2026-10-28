using System.Globalization;
using System.Text;
using WindowVeil.Appearance;
using WindowVeil.Locators;
using WindowVeil.Text;

namespace WindowVeil.Editor.Model;

/// <summary>Human-readable descriptions of locators and appearances for lists and the inspector. Pure logic.</summary>
public static class LocatorFormatter
{
    /// <summary>Short category name of a locator, e.g. "UI Automation" or "Window rectangle".</summary>
    public static string Category(ElementLocator locator)
    {
        ArgumentNullException.ThrowIfNull(locator);
        return locator switch
        {
            UiAutomationLocator { AutomationId: not null } => "AutomationId",
            UiAutomationLocator { ControlType: not null, Name: not null } => "Control type + name",
            UiAutomationLocator { Name: not null } => "Name",
            UiAutomationLocator => "UI Automation",
            TextLocator => "Text (TextPattern)",
            OcrTextLocator => "OCR text",
            WindowRelativeLocator => "Window rectangle",
            NormalizedLocator => "Normalized rectangle",
            ScreenRectangleLocator => "Screen rectangle",
            FallbackLocator => "Fallback chain",
            _ => locator.Kind,
        };
    }

    /// <summary>One-line description, e.g. <c>Button named "OK"</c> or <c>[10, 20, 300×24] px from the client origin</c>.</summary>
    public static string Describe(ElementLocator locator)
    {
        ArgumentNullException.ThrowIfNull(locator);
        switch (locator)
        {
            case UiAutomationLocator uia:
                return DescribeUia(uia);
            case TextLocator text:
            {
                var s = DescribeQuery(text.Query);
                if (text.Scope is not null) s += $" within {DescribeUia(text.Scope)}";
                if (!text.AllMatches) s += " (first match)";
                return s;
            }
            case OcrTextLocator ocr:
            {
                var s = $"{DescribeQuery(ocr.Query)}, confidence ≥ {ocr.MinConfidence.ToString("0.##", CultureInfo.InvariantCulture)}";
                if (ocr.SearchRegion is { } region) s += $" in {region}";
                if (!ocr.Expand.IsZero) s += $", expanded {RuleBuilder.FormatInsets(ocr.Expand)} DIP";
                return s;
            }
            case WindowRelativeLocator rect:
                return $"{FormatRect(rect.Bounds.X, rect.Bounds.Y, rect.Bounds.Width, rect.Bounds.Height)} px from the {AnchorName(rect.Anchor)} origin";
            case NormalizedLocator norm:
                return $"{Percent(norm.Bounds.X)}, {Percent(norm.Bounds.Y)}, {Percent(norm.Bounds.Width)} × {Percent(norm.Bounds.Height)} of the {AnchorName(norm.Anchor)}";
            case ScreenRectangleLocator screen:
                return $"{FormatRect(screen.Bounds.X, screen.Bounds.Y, screen.Bounds.Width, screen.Bounds.Height)} px on the screen (does not follow the window)";
            case FallbackLocator chain:
                return string.Join("  →  ", chain.Locators.Select(Summary));
            default:
                return locator.ToString();
        }
    }

    /// <summary>"Category: description" in one string.</summary>
    public static string Summary(ElementLocator locator) => $"{Category(locator)}: {Describe(locator)}";

    /// <summary>Describes an appearance, e.g. "Solid mask #202020" or "Registered 'privacy-badge'".</summary>
    public static string DescribeAppearance(OverlayAppearance appearance)
    {
        ArgumentNullException.ThrowIfNull(appearance);
        return appearance switch
        {
            MaskAppearance { Opacity: >= 1 } m => $"Solid mask {m.Color}",
            MaskAppearance m => $"Transparent mask {m.Color} @ {Percent(m.Opacity)}",
            HighlightAppearance h => $"Highlight {h.Color}, {h.Thickness.ToString("0.#", CultureInfo.InvariantCulture)} DIP",
            SpotlightAppearance s => $"Spotlight {s.DimColor} @ {Percent(s.Opacity)} ({s.Scope})",
            RegisteredAppearance r => r.Parameters.Count == 0 ? $"Registered '{r.FactoryId}'" : $"Registered '{r.FactoryId}' ({r.Parameters.Count} parameters)",
            _ => appearance.Kind,
        };
    }

    private static string DescribeUia(UiAutomationLocator uia)
    {
        var sb = new StringBuilder();
        if (uia.Ancestor is not null) sb.Append(DescribeUia(uia.Ancestor)).Append(" > ");
        var parts = new List<string>();
        if (uia.ControlType is not null && uia.Name is not null)
            parts.Add($"{uia.ControlType} named {DescribeQuery(uia.Name)}");
        else
        {
            if (uia.ControlType is not null) parts.Add($"any {uia.ControlType}");
            if (uia.Name is not null) parts.Add($"Name {DescribeQuery(uia.Name)}");
        }
        if (uia.AutomationId is not null) parts.Insert(0, $"AutomationId = \"{uia.AutomationId}\"");
        if (uia.ClassName is not null) parts.Add($"class \"{uia.ClassName}\"");
        if (parts.Count == 0) parts.Add("(no condition)");
        sb.Append(string.Join(", ", parts));
        if (uia.Index != 0) sb.Append(CultureInfo.InvariantCulture, $" (match #{uia.Index + 1})");
        return sb.ToString();
    }

    /// <summary>Describes a text query, e.g. <c>contains "Balance" (ignore case)</c>.</summary>
    public static string DescribeQuery(TextQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        string verb = query.Mode switch
        {
            TextMatch.Exact => "equals",
            TextMatch.Contains => "contains",
            TextMatch.StartsWith => "starts with",
            TextMatch.Regex => "matches regex",
            _ => query.Mode.ToString(),
        };
        string pattern = query.Pattern.Length > 60 ? query.Pattern[..59] + "…" : query.Pattern;
        return $"{verb} \"{pattern}\"{(query.IgnoreCase ? " (ignore case)" : "")}";
    }

    private static string AnchorName(WindowAnchor anchor) => anchor == WindowAnchor.Client ? "client" : "window";

    private static string FormatRect(int x, int y, int w, int h) => string.Create(CultureInfo.InvariantCulture, $"[{x}, {y}, {w}×{h}]");

    private static string Percent(double v) => (v * 100).ToString("0.#", CultureInfo.InvariantCulture) + "%";
}
