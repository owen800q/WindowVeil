using WindowVeil.Appearance;
using WindowVeil.Geometry;
using WindowVeil.Imaging;
using WindowVeil.Locators;
using WindowVeil.Rendering;
using WindowVeil.Text;

namespace WindowVeil.UnitTests.Profiles;

internal static class ProfileTestData
{
    /// <summary>The example from the specification; must load unchanged.</summary>
    public const string Example = """
        {
          "schemaVersion": 1,
          "name": "RetroCap Startup Mask",
          "application": { "processName": "RetroCap" },
          "rules": [
            {
              "id": "hide-startup-checkbox",
              "locator": { "type": "uiAutomation", "automationId": "StartupCheck", "fallbackName": "Start automatically when I sign in to Windows" },
              "appearance": { "type": "solid", "color": "#202020", "opacity": 1.0 },
              "placement": "cover",
              "followTarget": true,
              "enabled": true
            }
          ]
        }
        """;

    /// <summary>Every locator, appearance and placement variant of schema v1 in one document.</summary>
    public const string AllVariants = """
        {
          "$schema": "https://github.com/owen800q/WindowVeil/blob/main/docs/profile.schema.json",
          "schemaVersion": 1,
          "name": "All variants",
          "application": { "processName": "RetroCap", "titleContains": "Settings", "titlePattern": "^RetroCap( - .*)?$", "className": "RetroCapMain", "requireVisible": false, "requireTitle": false },
          "rules": [
            { "id": "rect", "name": "Absolute", "group": "geo", "locator": { "type": "rectangle", "x": -1900, "y": -20, "width": 300, "height": 40 }, "appearance": { "type": "solid", "color": "#000" } },
            { "id": "rel-client", "locator": { "type": "windowRelative", "x": 10, "y": 20, "width": 30, "height": 40 }, "appearance": { "type": "transparent", "color": "#FF0000" } },
            { "id": "rel-window", "locator": { "type": "windowRelative", "x": -5, "y": 0, "width": 1, "height": 1, "anchor": "window" }, "appearance": { "type": "transparent", "color": "#00ff00", "opacity": 0.25 } },
            { "id": "norm", "locator": { "type": "normalized", "x": 0.1, "y": 0.2, "width": 0.5, "height": 0.25, "anchor": "window" }, "appearance": { "type": "highlight", "color": "#FFCC00", "thickness": 2.5, "opacity": 0.8 } },
            { "id": "uia-full", "locator": { "type": "uiAutomation", "automationId": "Save", "name": "^Save( all)?$", "nameMatch": "regex", "ignoreCase": true, "controlType": "Button", "className": "WpfButton", "index": 2, "maxDepth": 12, "maxNodes": 900,
                "ancestor": { "controlType": "Pane", "name": "Toolbar", "nameMatch": "startsWith", "ancestor": { "type": "uiAutomation", "automationId": "Root" } } },
              "appearance": { "type": "spotlight" } },
            { "id": "uia-fallback", "locator": { "type": "uiAutomation", "automationId": "StartupCheck", "fallbackName": "start automatically", "nameMatch": "contains", "ignoreCase": true, "maxDepth": 6 },
              "appearance": { "type": "spotlight", "color": "#101010", "opacity": 0.4, "scope": "monitor" } },
            { "id": "text", "locator": { "type": "text", "text": "Password", "match": "exact", "ignoreCase": false, "allMatches": false, "scope": { "className": "Edit" } },
              "appearance": { "type": "registered", "factory": "badge", "parameters": { "text": "Secret", "tone": "warning" } } },
            { "id": "ocr", "locator": { "type": "ocrText", "text": "Start automatically", "match": "startsWith", "ignoreCase": false, "minConfidence": 0.75,
                "searchRegion": { "x": 0, "y": 40, "width": 600, "height": 200 }, "expand": { "left": 20, "top": 2, "right": 2, "bottom": 2 },
                "mustNotOverlap": [ { "text": "Minimize" }, { "text": "^Close$", "match": "regex", "ignoreCase": false } ], "minIntervalMs": 250, "allMatches": false },
              "appearance": { "type": "registered", "factory": "plain" } },
            { "id": "ocr-uniform", "locator": { "type": "ocrText", "text": "Card number", "expand": 4 }, "appearance": { "type": "solid", "color": "#202020", "opacity": 1 } },
            { "id": "chain", "locator": { "type": "fallback", "continueOnError": false, "locators": [
                { "type": "uiAutomation", "automationId": "A" },
                { "type": "fallback", "locators": [ { "type": "text", "text": "B" }, { "type": "ocrText", "text": "B" } ] },
                { "type": "windowRelative", "x": 1, "y": 2, "width": 3, "height": 4 } ] },
              "appearance": { "type": "solid", "color": "#202020" } },
            { "id": "p-right", "locator": { "type": "uiAutomation", "controlType": "CheckBox" }, "appearance": { "type": "registered", "factory": "badge" }, "placement": "right" },
            { "id": "p-left", "locator": { "type": "uiAutomation", "controlType": "CheckBox" }, "appearance": { "type": "registered", "factory": "badge" }, "placement": "left" },
            { "id": "p-above", "locator": { "type": "uiAutomation", "controlType": "CheckBox" }, "appearance": { "type": "registered", "factory": "badge" }, "placement": "above" },
            { "id": "p-below", "locator": { "type": "uiAutomation", "controlType": "CheckBox" }, "appearance": { "type": "registered", "factory": "badge" }, "placement": "below" },
            { "id": "p-center", "locator": { "type": "uiAutomation", "controlType": "CheckBox" }, "appearance": { "type": "registered", "factory": "badge" }, "placement": "center" },
            { "id": "p-cover-object", "locator": { "type": "uiAutomation", "className": "Static" }, "appearance": { "type": "solid", "color": "#202020" },
              "placement": { "mode": "cover", "padding": { "left": 20, "top": 2, "right": 2, "bottom": 2 }, "offsetX": -1, "offsetY": 1.5, "multiRect": "union" } },
            { "id": "p-right-object", "locator": { "type": "uiAutomation", "className": "Static" }, "appearance": { "type": "registered", "factory": "badge" },
              "placement": { "mode": "right", "offsetX": 12, "width": 80, "height": 24, "alignment": "start", "padding": 3 } },
            { "id": "p-below-object", "locator": { "type": "uiAutomation", "className": "Static" }, "appearance": { "type": "registered", "factory": "badge" },
              "placement": { "mode": "below", "alignment": "end", "multiRect": "each" } },
            { "id": "behavior", "name": "Interactive blocker", "group": "privacy", "locator": { "type": "uiAutomation", "automationId": "Delete" }, "appearance": { "type": "solid", "color": "#202020" },
              "inputMode": "interactive", "focusPolicy": "activateOnClick", "zOrder": "topmost", "followTarget": false, "enabled": false, "priority": -3, "visibility": "whenTargetForeground" }
          ]
        }
        """;

    public static OverlayRule Rule(string id, ElementLocator locator, OverlayAppearance? appearance = null) => new()
    {
        Id = id,
        Target = locator,
        Appearance = appearance ?? MaskAppearance.Solid("#202020"),
    };

    /// <summary>Rules built in code, covering the tricky writer/reader asymmetries.</summary>
    public static IEnumerable<OverlayRule> CodeRules()
    {
        yield return Rule("example", ElementLocator.ByAutomationId("StartupCheck").Or(ElementLocator.ByName("Start automatically when I sign in to Windows")));
        yield return Rule("regex-name", ElementLocator.ByName(@"^Save\b", TextMatch.Regex, ignoreCase: true));
        yield return Rule("fallback-name-shared-mode", (ElementLocator.ByAutomationId("A") with { Name = new TextQuery("x", TextMatch.Contains, true) })
            .Or(ElementLocator.ByName("y", TextMatch.Contains, true)));
        // Modes differ: cannot use the fallbackName shorthand, must be written as an explicit chain.
        yield return Rule("fallback-name-mode-mismatch", (ElementLocator.ByAutomationId("A") with { Name = new TextQuery("x", TextMatch.Contains, true) })
            .Or(ElementLocator.ByName("y")));
        // Fallback with a different scope than the primary: explicit chain.
        yield return Rule("fallback-scope-mismatch", ElementLocator.ByAutomationId("A").Within(ElementLocator.ByControlType("Pane")).Or(ElementLocator.ByName("y")));
        yield return Rule("three-uia", ElementLocator.FirstOf(ElementLocator.ByAutomationId("A"), ElementLocator.ByName("B"), ElementLocator.ByClassName("C")));
        yield return Rule("no-continue", new FallbackLocator([ElementLocator.ByAutomationId("A"), ElementLocator.ByName("B")]) { ContinueOnError = false });
        yield return Rule("ocr", ElementLocator.ByOcrText("Card", TextMatch.Exact, ignoreCase: false).Expanded(new Insets(1, 2, 3, 4), TextQuery.Matching("^CVV$")) with
        {
            MinConfidence = 0.9, MinInterval = TimeSpan.FromMilliseconds(250), SearchRegion = new PixelRect(-10, 0, 20, 30), AllMatches = false,
        });
        yield return Rule("text-scope", ElementLocator.ByText("Total", TextMatch.StartsWith) with { Scope = ElementLocator.ByAutomationId("Grid").At(1) });
        yield return Rule("normalized", ElementLocator.Window(WindowAnchor.Client));
        yield return Rule("translucent", ElementLocator.Rectangle(0, 0, 10, 10), MaskAppearance.Transparent("#123456", 0.33));
        yield return Rule("highlight", ElementLocator.Rectangle(0, 0, 10, 10), new HighlightAppearance(OverlayColor.FromRgb(1, 2, 3), 1.25, 0.5));
        yield return Rule("spotlight", ElementLocator.Rectangle(0, 0, 10, 10), SpotlightAppearance.Create(0.7, SpotlightScope.Monitor));
        yield return Rule("registered", ElementLocator.Rectangle(0, 0, 10, 10), new RegisteredAppearance("badge") { Parameters = new Dictionary<string, string> { ["text"] = "Hi \"there\" ✓", ["z"] = "" } });
        yield return Rule("p-right-width", ElementLocator.Rectangle(0, 0, 10, 10)) with { Placement = OverlayPlacement.Right() with { Width = 33.5 } };
        yield return Rule("p-cover-offset", ElementLocator.Rectangle(0, 0, 10, 10)) with { Placement = OverlayPlacement.CoverTarget with { OffsetX = 3 } };
        yield return Rule("p-cover-padding", ElementLocator.Rectangle(0, 0, 10, 10)) with { Placement = OverlayPlacement.Cover(new Insets(20, 2, 2, 2)) };
        yield return Rule("p-above-end-each", ElementLocator.Rectangle(0, 0, 10, 10)) with
        {
            Placement = OverlayPlacement.Above() with { Alignment = PlacementAlignment.End, MultiRect = MultiRectMode.Each, OffsetY = 0 },
        };
        yield return Rule("p-center-size", ElementLocator.Rectangle(0, 0, 10, 10)) with { Placement = OverlayPlacement.Centered(120, 40) };
        yield return Rule("behavior", ElementLocator.Rectangle(0, 0, 10, 10)) with
        {
            Name = "B", Group = "G", InputMode = OverlayInputMode.Interactive, FocusPolicy = OverlayFocusPolicy.ActivateOnClick,
            ZOrder = OverlayZOrder.Topmost, FollowTarget = false, Enabled = false, Priority = 7, Visibility = OverlayVisibility.WhenTargetForeground,
        };
    }
}
