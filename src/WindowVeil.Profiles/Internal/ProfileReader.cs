using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using WindowVeil.Appearance;
using WindowVeil.Geometry;
using WindowVeil.Imaging;
using WindowVeil.Locators;
using WindowVeil.Text;
using static WindowVeil.Profiles.Internal.ProfileVocabulary;

namespace WindowVeil.Profiles.Internal;

/// <summary>
/// Converts a (migrated) schema-v1 document into an <see cref="OverlayProfile"/>. Never throws for invalid content:
/// every problem becomes a diagnostic with the JSON path of the offending element. Only data is read; no type named
/// in the document is ever instantiated.
/// </summary>
internal sealed class ProfileReader(ProfileLoadOptions options, List<ProfileDiagnostic> diagnostics)
{
    private int _errors;

    public OverlayProfile? ReadProfile(JsonObject root)
    {
        CheckUnknown(root, "$", RootProperties);
        int before = _errors;
        var name = ReadString(root, "name", "$", required: true);
        var application = ReadApplication(root, "$");

        JsonArray? rules = null;
        if (!root.TryGetPropertyValue("rules", out var rulesNode) || rulesNode is null)
            Error("$.rules", "'rules' is required (use an empty array for a profile without rules).");
        else if (rulesNode is not JsonArray array)
            Error("$.rules", "'rules' must be an array.");
        else if (array.Count > options.MaxRules)
            Error("$.rules", $"The profile has {array.Count} rules; at most {options.MaxRules} are allowed.");
        else
            rules = array;

        if (_errors > before) return null;

        var result = new List<OverlayRule>(rules!.Count);
        var indices = new List<int>(rules.Count);
        var ids = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < rules.Count; i++)
        {
            var rule = ReadRule(rules[i], i, ids);
            if (rule is null) continue;
            result.Add(rule);
            indices.Add(i);
        }
        return new OverlayProfile { Name = name!, Application = application!, Rules = result, SourceIndices = indices };
    }

    // ------------------------------------------------------------------ application

    private ProfileApplication? ReadApplication(JsonObject root, string parent)
    {
        var path = Prop(parent, "application");
        if (!root.TryGetPropertyValue("application", out var node) || node is null)
        {
            Error(path, "'application' is required: it selects the window the rules apply to (for example { \"processName\": \"notepad\" }).");
            return null;
        }
        if (node is not JsonObject o) { Error(path, "'application' must be an object."); return null; }
        CheckUnknown(o, path, ApplicationProperties);
        int before = _errors;
        var processName = ReadString(o, "processName", path);
        if (processName is not null && processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) processName = processName[..^4];
        var titleContains = ReadString(o, "titleContains", path);
        var titlePattern = ReadString(o, "titlePattern", path);
        if (titlePattern is not null) ValidateRegex(titlePattern, RegexOptions.CultureInvariant, Prop(path, "titlePattern"));
        var className = ReadString(o, "className", path);
        var requireVisible = ReadBool(o, "requireVisible", path) ?? true;
        var requireTitle = ReadBool(o, "requireTitle", path) ?? true;
        var app = new ProfileApplication
        {
            ProcessName = processName, TitleContains = titleContains, TitlePattern = titlePattern, ClassName = className,
            RequireVisible = requireVisible, RequireTitle = requireTitle,
        };
        if (_errors == before && app.IsEmpty)
            Error(path, "'application' needs at least one of 'processName', 'titleContains', 'titlePattern' or 'className'.");
        return _errors > before ? null : app;
    }

    // ------------------------------------------------------------------ rules

    private OverlayRule? ReadRule(JsonNode? node, int index, Dictionary<string, int> ids)
    {
        var path = $"$.rules[{index}]";
        if (node is not JsonObject o) { Error(path, "A rule must be an object."); return null; }
        CheckUnknown(o, path, RuleProperties);
        int before = _errors;

        var id = ReadString(o, "id", path, required: true, maxLength: MaxIdLength);
        if (id is not null)
        {
            if (ids.TryGetValue(id, out var first)) Error(Prop(path, "id"), $"Duplicate rule id '{id}' (first used by $.rules[{first}]). Rule ids must be unique.");
            else ids.Add(id, index);
        }
        var name = ReadString(o, "name", path);
        var group = ReadString(o, "group", path);

        o.TryGetPropertyValue("locator", out var locatorNode);
        var locator = ReadLocator(locatorNode, Prop(path, "locator"), 1, required: true);
        o.TryGetPropertyValue("appearance", out var appearanceNode);
        var appearance = ReadAppearance(appearanceNode, Prop(path, "appearance"));
        o.TryGetPropertyValue("placement", out var placementNode);
        var placement = ReadPlacement(placementNode, Prop(path, "placement"));

        var inputMode = ReadEnum(o, "inputMode", path, InputModes) ?? Rendering.OverlayInputMode.PassThrough;
        var focusPolicy = ReadEnum(o, "focusPolicy", path, FocusPolicies) ?? Rendering.OverlayFocusPolicy.NoActivate;
        var zOrder = ReadEnum(o, "zOrder", path, ZOrders) ?? Rendering.OverlayZOrder.AboveTarget;
        var visibility = ReadEnum(o, "visibility", path, Visibilities) ?? OverlayVisibility.WhenTargetVisible;
        var followTarget = ReadBool(o, "followTarget", path) ?? true;
        var enabled = ReadBool(o, "enabled", path) ?? true;
        var priority = ReadInt(o, "priority", path, -1_000_000, 1_000_000) ?? 0;

        if (_errors > before || locator is null || appearance is null || placement is null) return null;

        var rule = new OverlayRule
        {
            Id = id!, Name = name, Group = group, Target = locator, Appearance = appearance, Placement = placement,
            InputMode = inputMode, FocusPolicy = focusPolicy, ZOrder = zOrder, Visibility = visibility,
            FollowTarget = followTarget, Enabled = enabled, Priority = priority,
        };
        try
        {
            rule.Validate();
        }
        catch (WindowVeilException ex)
        {
            Error(path, ex.Message);
            return null;
        }
        return rule;
    }

    // ------------------------------------------------------------------ locators

    private ElementLocator? ReadLocator(JsonNode? node, string path, int depth, bool required)
    {
        if (depth > options.MaxLocatorDepth)
        {
            Error(path, $"Locators are nested more than {options.MaxLocatorDepth} levels deep (fallback chains, 'ancestor' and 'scope' selectors).");
            return null;
        }
        if (node is null)
        {
            if (required) Error(path, "A locator is required.");
            return null;
        }
        if (node is not JsonObject o) { Error(path, "A locator must be an object with a 'type'."); return null; }
        var type = ReadString(o, "type", path, required: true);
        if (type is null) return null;
        int before = _errors;
        ElementLocator? result = type switch
        {
            "rectangle" => ReadRectangle(o, path),
            "windowRelative" => ReadWindowRelative(o, path),
            "normalized" => ReadNormalized(o, path),
            "uiAutomation" => ReadUiAutomation(o, path, depth),
            "text" => ReadText(o, path, depth),
            "ocrText" => ReadOcrText(o, path),
            "fallback" => ReadFallback(o, path, depth),
            _ => UnknownType(Prop(path, "type"), "locator", type, LocatorTypes, null),
        };
        return _errors > before ? null : result;
    }

    private ElementLocator? ReadRectangle(JsonObject o, string path)
    {
        CheckUnknown(o, path, ["type", "x", "y", "width", "height"]);
        return ReadPixelRect(o, path) is { } r ? new ScreenRectangleLocator(r) : null;
    }

    private ElementLocator? ReadWindowRelative(JsonObject o, string path)
    {
        CheckUnknown(o, path, ["type", "x", "y", "width", "height", "anchor"]);
        var rect = ReadPixelRect(o, path);
        var anchor = ReadEnum(o, "anchor", path, Anchors) ?? WindowAnchor.Client;
        return rect is { } r ? new WindowRelativeLocator(r, anchor) : null;
    }

    private ElementLocator? ReadNormalized(JsonObject o, string path)
    {
        CheckUnknown(o, path, ["type", "x", "y", "width", "height", "anchor"]);
        var x = ReadDouble(o, "x", path, 0, 1, required: true);
        var y = ReadDouble(o, "y", path, 0, 1, required: true);
        var w = ReadDouble(o, "width", path, 0, 1, required: true, minExclusive: true);
        var h = ReadDouble(o, "height", path, 0, 1, required: true, minExclusive: true);
        var anchor = ReadEnum(o, "anchor", path, Anchors) ?? WindowAnchor.Client;
        if (x is null || y is null || w is null || h is null) return null;
        if (x + w > 1 + 1e-9 || y + h > 1 + 1e-9)
            Warning(path, "The normalized rectangle extends beyond the window (x + width or y + height is greater than 1).");
        return new NormalizedLocator(new RectD(x.Value, y.Value, w.Value, h.Value), anchor);
    }

    private ElementLocator? ReadUiAutomation(JsonObject o, string path, int depth)
    {
        var primary = ReadUiAutomationCore(o, path, depth, allowFallbackName: true, out var fallbackName);
        if (primary is null) return null;
        return fallbackName is null ? primary : WithFallbackName(primary, fallbackName);
    }

    private UiAutomationLocator? ReadNestedUiAutomation(JsonNode? node, string path, int depth)
    {
        if (depth > options.MaxLocatorDepth)
        {
            Error(path, $"Locators are nested more than {options.MaxLocatorDepth} levels deep (fallback chains, 'ancestor' and 'scope' selectors).");
            return null;
        }
        if (node is not JsonObject o) { Error(path, "Must be an object with uiAutomation conditions."); return null; }
        if (o.ContainsKey("type") && ReadString(o, "type", path) is { } t && t != "uiAutomation")
            Error(Prop(path, "type"), $"Must be 'uiAutomation' (or omitted), not '{t}'.");
        return ReadUiAutomationCore(o, path, depth, allowFallbackName: false, out _);
    }

    private UiAutomationLocator? ReadUiAutomationCore(JsonObject o, string path, int depth, bool allowFallbackName, out TextQuery? fallbackName)
    {
        fallbackName = null;
        CheckUnknown(o, path, allowFallbackName ? [.. UiAutomationProperties, "fallbackName"] : UiAutomationProperties);
        if (!allowFallbackName && o.ContainsKey("fallbackName"))
            Error(Prop(path, "fallbackName"), "'fallbackName' is only supported on a top-level uiAutomation locator.");
        int before = _errors;
        var automationId = ReadString(o, "automationId", path);
        var nameText = ReadString(o, "name", path);
        var match = ReadEnum(o, "nameMatch", path, TextMatches) ?? TextMatch.Exact;
        var ignoreCase = ReadBool(o, "ignoreCase", path) ?? false;
        var controlType = ReadString(o, "controlType", path);
        var className = ReadString(o, "className", path);
        var index = ReadInt(o, "index", path, 0, MaxUiaIndex) ?? 0;
        var maxDepth = ReadInt(o, "maxDepth", path, 1, MaxUiaDepth) ?? 32;
        var maxNodes = ReadInt(o, "maxNodes", path, 1, MaxUiaNodes) ?? 5000;
        UiAutomationLocator? ancestor = null;
        if (o.TryGetPropertyValue("ancestor", out var ancestorNode) && ancestorNode is not null)
            ancestor = ReadNestedUiAutomation(ancestorNode, Prop(path, "ancestor"), depth + 1);
        var fallbackText = allowFallbackName ? ReadString(o, "fallbackName", path) : null;

        var name = nameText is null ? null : MakeQuery(nameText, match, ignoreCase, Prop(path, "name"));
        if (fallbackText is not null) fallbackName = MakeQuery(fallbackText, match, ignoreCase, Prop(path, "fallbackName"));
        if (automationId is null && nameText is null && controlType is null && className is null && _errors == before)
            Error(path, "A uiAutomation locator needs at least one of 'automationId', 'name', 'controlType' or 'className'" +
                        (fallbackText is null ? "." : " ('fallbackName' is only a fallback)."));
        if (_errors > before) return null;
        return new UiAutomationLocator
        {
            AutomationId = automationId, Name = name, ControlType = controlType, ClassName = className,
            Index = index, MaxDepth = maxDepth, MaxNodes = maxNodes, Ancestor = ancestor,
        };
    }

    private ElementLocator? ReadText(JsonObject o, string path, int depth)
    {
        CheckUnknown(o, path, ["type", "text", "match", "ignoreCase", "allMatches", "scope"]);
        var query = ReadQuery(o, path, TextMatch.Contains, ignoreCaseDefault: true);
        var allMatches = ReadBool(o, "allMatches", path) ?? true;
        UiAutomationLocator? scope = null;
        if (o.TryGetPropertyValue("scope", out var scopeNode) && scopeNode is not null)
            scope = ReadNestedUiAutomation(scopeNode, Prop(path, "scope"), depth + 1);
        return query is null ? null : new TextLocator(query) { AllMatches = allMatches, Scope = scope };
    }

    private ElementLocator? ReadOcrText(JsonObject o, string path)
    {
        CheckUnknown(o, path, ["type", "text", "match", "ignoreCase", "minConfidence", "searchRegion", "expand", "mustNotOverlap", "minIntervalMs", "allMatches"]);
        var query = ReadQuery(o, path, TextMatch.Contains, ignoreCaseDefault: true);
        var minConfidence = ReadDouble(o, "minConfidence", path, 0, 1) ?? 0.6;
        var allMatches = ReadBool(o, "allMatches", path) ?? true;
        var minIntervalMs = ReadInt(o, "minIntervalMs", path, 0, MaxMinIntervalMs) ?? 1000;

        PixelRect? searchRegion = null;
        if (o.TryGetPropertyValue("searchRegion", out var regionNode) && regionNode is not null)
        {
            var regionPath = Prop(path, "searchRegion");
            if (regionNode is JsonObject region)
            {
                CheckUnknown(region, regionPath, ["x", "y", "width", "height"]);
                searchRegion = ReadPixelRect(region, regionPath);
            }
            else Error(regionPath, "'searchRegion' must be an object { x, y, width, height } (client-relative physical pixels).");
        }

        var expand = Insets.Zero;
        if (o.TryGetPropertyValue("expand", out var expandNode) && expandNode is not null)
            expand = ReadInsets(expandNode, Prop(path, "expand")) ?? Insets.Zero;

        var mustNotOverlap = new List<TextQuery>();
        if (o.TryGetPropertyValue("mustNotOverlap", out var mnoNode) && mnoNode is not null)
        {
            var mnoPath = Prop(path, "mustNotOverlap");
            if (mnoNode is not JsonArray array) Error(mnoPath, "'mustNotOverlap' must be an array of { text, match, ignoreCase } objects.");
            else if (array.Count > options.MaxListItems) Error(mnoPath, $"'mustNotOverlap' has {array.Count} entries; at most {options.MaxListItems} are allowed.");
            else
            {
                for (int i = 0; i < array.Count; i++)
                {
                    var itemPath = $"{mnoPath}[{i}]";
                    if (array[i] is not JsonObject item) { Error(itemPath, "Must be an object { text, match, ignoreCase }."); continue; }
                    CheckUnknown(item, itemPath, ["text", "match", "ignoreCase"]);
                    if (ReadQuery(item, itemPath, TextMatch.Contains, ignoreCaseDefault: true) is { } q) mustNotOverlap.Add(q);
                }
            }
        }

        if (query is null) return null;
        return new OcrTextLocator(query)
        {
            MinConfidence = minConfidence,
            AllMatches = allMatches,
            MinInterval = TimeSpan.FromMilliseconds(minIntervalMs),
            SearchRegion = searchRegion,
            Expand = expand,
            MustNotOverlap = mustNotOverlap,
        };
    }

    private ElementLocator? ReadFallback(JsonObject o, string path, int depth)
    {
        CheckUnknown(o, path, ["type", "locators", "continueOnError"]);
        var continueOnError = ReadBool(o, "continueOnError", path) ?? true;
        var listPath = Prop(path, "locators");
        if (!o.TryGetPropertyValue("locators", out var node) || node is null) { Error(listPath, "'locators' is required."); return null; }
        if (node is not JsonArray array) { Error(listPath, "'locators' must be an array of locators."); return null; }
        if (array.Count == 0) { Error(listPath, "A fallback chain needs at least one locator."); return null; }
        if (array.Count > options.MaxListItems) { Error(listPath, $"The fallback chain has {array.Count} locators; at most {options.MaxListItems} are allowed."); return null; }
        var locators = new List<ElementLocator>(array.Count);
        for (int i = 0; i < array.Count; i++)
        {
            if (ReadLocator(array[i], $"{listPath}[{i}]", depth + 1, required: true) is { } l) locators.Add(l);
        }
        return locators.Count == array.Count ? new FallbackLocator(locators) { ContinueOnError = continueOnError } : null;
    }

    private TextQuery? ReadQuery(JsonObject o, string path, TextMatch matchDefault, bool ignoreCaseDefault)
    {
        var text = ReadString(o, "text", path, required: true);
        var match = ReadEnum(o, "match", path, TextMatches) ?? matchDefault;
        var ignoreCase = ReadBool(o, "ignoreCase", path) ?? ignoreCaseDefault;
        return text is null ? null : MakeQuery(text, match, ignoreCase, Prop(path, "text"));
    }

    private TextQuery? MakeQuery(string text, TextMatch match, bool ignoreCase, string path)
    {
        if (match == TextMatch.Regex && text.Length > options.MaxRegexLength)
        {
            Error(path, $"The regular expression is {text.Length} characters long; at most {options.MaxRegexLength} are allowed.");
            return null;
        }
        try
        {
            // TextQuery compiles regular expressions eagerly (with a match timeout), which validates the syntax.
            return new TextQuery(text, match, ignoreCase);
        }
        catch (ArgumentException ex)
        {
            Error(path, match == TextMatch.Regex ? $"Invalid regular expression: {ex.Message}" : ex.Message);
            return null;
        }
    }

    // ------------------------------------------------------------------ appearance

    private OverlayAppearance? ReadAppearance(JsonNode? node, string path)
    {
        if (node is null) { Error(path, "An appearance is required."); return null; }
        if (node is not JsonObject o) { Error(path, "An appearance must be an object with a 'type'."); return null; }
        var type = ReadString(o, "type", path, required: true);
        if (type is null) return null;
        int before = _errors;
        OverlayAppearance? result = null;
        switch (type)
        {
            case "solid":
            case "transparent":
            {
                CheckUnknown(o, path, ["type", "color", "opacity"]);
                var color = ReadColor(o, "color", path, required: true);
                var opacity = ReadDouble(o, "opacity", path, 0, 1) ?? (type == "solid" ? 1.0 : DefaultTransparentOpacity);
                if (color is { } c && _errors == before) result = new MaskAppearance(c, opacity);
                break;
            }
            case "highlight":
            {
                CheckUnknown(o, path, ["type", "color", "thickness", "opacity"]);
                var color = ReadColor(o, "color", path, required: true);
                var thickness = ReadDouble(o, "thickness", path, 0, MaxThickness, minExclusive: true) ?? 3;
                var opacity = ReadDouble(o, "opacity", path, 0, 1) ?? 1;
                if (color is { } c && _errors == before) result = new HighlightAppearance(c, thickness, opacity);
                break;
            }
            case "spotlight":
            {
                CheckUnknown(o, path, ["type", "color", "opacity", "scope"]);
                var color = ReadColor(o, "color", path, required: false) ?? OverlayColor.Black;
                var opacity = ReadDouble(o, "opacity", path, 0, 1) ?? 0.6;
                var scope = ReadEnum(o, "scope", path, SpotlightScopes) ?? SpotlightScope.TargetWindow;
                if (_errors == before) result = new SpotlightAppearance(color, opacity, scope);
                break;
            }
            case "registered":
            {
                CheckUnknown(o, path, ["type", "factory", "parameters"]);
                var factory = ReadString(o, "factory", path, required: true, maxLength: MaxIdLength);
                var parameters = ReadParameters(o, path);
                if (factory is not null && parameters is not null) result = new RegisteredAppearance(factory) { Parameters = parameters };
                break;
            }
            default:
                UnknownType(Prop(path, "type"), "appearance", type, AppearanceTypes,
                    " Custom appearances (for example WPF content) are created by the application: register a factory in an " +
                    "AppearanceRegistry and reference it with { \"type\": \"registered\", \"factory\": \"<id>\" }.");
                break;
        }
        return _errors > before ? null : result;
    }

    private Dictionary<string, string>? ReadParameters(JsonObject o, string path)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!o.TryGetPropertyValue("parameters", out var node) || node is null) return result;
        var p = Prop(path, "parameters");
        if (node is not JsonObject parameters) { Error(p, "'parameters' must be an object whose values are strings."); return null; }
        if (parameters.Count > options.MaxListItems) { Error(p, $"'parameters' has {parameters.Count} entries; at most {options.MaxListItems} are allowed."); return null; }
        bool ok = true;
        foreach (var (key, value) in parameters)
        {
            var kp = Prop(p, key);
            if (key.Length > MaxIdLength) { Error(kp, $"Parameter names can be at most {MaxIdLength} characters long."); ok = false; continue; }
            if (value is null || value.GetValueKind() != JsonValueKind.String)
            {
                Error(kp, "Parameter values must be strings (factories parse them themselves).");
                ok = false;
                continue;
            }
            var s = value.GetValue<string>();
            if (s.Length > options.MaxStringLength) { Error(kp, $"The value is longer than {options.MaxStringLength} characters."); ok = false; continue; }
            result[key] = s;
        }
        return ok ? result : null;
    }

    private OverlayColor? ReadColor(JsonObject o, string name, string path, bool required)
    {
        var text = ReadString(o, name, path, required: required);
        if (text is null) return null;
        var p = Prop(path, name);
        if (!OverlayColor.TryParse(text, out var color))
        {
            Error(p, $"'{text}' is not a valid color. Use #RGB, #RRGGBB or #AARRGGBB.");
            return null;
        }
        if (color.A != 255) Warning(p, "The color's alpha channel is ignored; use 'opacity' instead.");
        return color;
    }

    // ------------------------------------------------------------------ placement

    private OverlayPlacement? ReadPlacement(JsonNode? node, string path)
    {
        if (node is null) return OverlayPlacement.CoverTarget;
        var kind = node.GetValueKind();
        if (kind == JsonValueKind.String)
        {
            var s = node.GetValue<string>();
            if (PlacementModes.TryParse(s, out var mode)) return Preset(mode);
            Error(path, $"Unknown placement '{Truncate(s)}'. Use one of {PlacementModes.Describe()} or an object {{ \"mode\": ... }}.");
            return null;
        }
        if (node is not JsonObject o)
        {
            Error(path, $"'placement' must be one of {PlacementModes.Describe()} or an object.");
            return null;
        }
        CheckUnknown(o, path, PlacementProperties);
        int before = _errors;
        var m = ReadEnum(o, "mode", path, PlacementModes) ?? PlacementMode.Cover;
        var placement = Preset(m);
        if (ReadDouble(o, "offsetX", path, -MaxCoordinate, MaxCoordinate) is { } ox) placement = placement with { OffsetX = ox };
        if (ReadDouble(o, "offsetY", path, -MaxCoordinate, MaxCoordinate) is { } oy) placement = placement with { OffsetY = oy };
        if (o.TryGetPropertyValue("padding", out var paddingNode) && paddingNode is not null &&
            ReadInsets(paddingNode, Prop(path, "padding")) is { } padding)
            placement = placement with { Padding = padding };
        if (ReadDouble(o, "width", path, 0, MaxCoordinate, minExclusive: true) is { } w) placement = placement with { Width = w };
        if (ReadDouble(o, "height", path, 0, MaxCoordinate, minExclusive: true) is { } h) placement = placement with { Height = h };
        if (ReadEnum(o, "alignment", path, Alignments) is { } a) placement = placement with { Alignment = a };
        if (ReadEnum(o, "multiRect", path, MultiRectModes) is { } mr) placement = placement with { MultiRect = mr };
        if (m == PlacementMode.Cover && (o.ContainsKey("width") || o.ContainsKey("height")))
            Warning(path, "'width' and 'height' have no effect in 'cover' mode (the overlay is sized to the target).");
        return _errors > before ? null : placement;
    }

    private Insets? ReadInsets(JsonNode node, string path)
    {
        if (node.GetValueKind() == JsonValueKind.Number)
        {
            if (!TryGetFiniteDouble(node, out var v) || Math.Abs(v) > MaxCoordinate)
            {
                Error(path, $"Must be a number between {-MaxCoordinate} and {MaxCoordinate}.");
                return null;
            }
            return Insets.Uniform(v);
        }
        if (node is not JsonObject o)
        {
            Error(path, "Must be a number or an object { left, top, right, bottom }.");
            return null;
        }
        CheckUnknown(o, path, ["left", "top", "right", "bottom"]);
        int before = _errors;
        var left = ReadDouble(o, "left", path, -MaxCoordinate, MaxCoordinate) ?? 0;
        var top = ReadDouble(o, "top", path, -MaxCoordinate, MaxCoordinate) ?? 0;
        var right = ReadDouble(o, "right", path, -MaxCoordinate, MaxCoordinate) ?? 0;
        var bottom = ReadDouble(o, "bottom", path, -MaxCoordinate, MaxCoordinate) ?? 0;
        return _errors > before ? null : new Insets(left, top, right, bottom);
    }

    private PixelRect? ReadPixelRect(JsonObject o, string path)
    {
        var x = ReadInt(o, "x", path, -MaxCoordinate, MaxCoordinate, required: true);
        var y = ReadInt(o, "y", path, -MaxCoordinate, MaxCoordinate, required: true);
        var w = ReadInt(o, "width", path, 1, MaxCoordinate, required: true);
        var h = ReadInt(o, "height", path, 1, MaxCoordinate, required: true);
        return x is null || y is null || w is null || h is null ? null : new PixelRect(x.Value, y.Value, w.Value, h.Value);
    }

    // ------------------------------------------------------------------ primitives

    private string? ReadString(JsonObject o, string name, string parent, bool required = false, int? maxLength = null)
    {
        var path = Prop(parent, name);
        if (!o.TryGetPropertyValue(name, out var node) || node is null)
        {
            if (required) Error(path, $"'{name}' is required.");
            return null;
        }
        if (node.GetValueKind() != JsonValueKind.String) { Error(path, $"'{name}' must be a string."); return null; }
        var s = node.GetValue<string>();
        if (string.IsNullOrWhiteSpace(s)) { Error(path, $"'{name}' cannot be empty."); return null; }
        int max = maxLength ?? options.MaxStringLength;
        if (s.Length > max) { Error(path, $"'{name}' is {s.Length} characters long; at most {max} are allowed."); return null; }
        return s;
    }

    private bool? ReadBool(JsonObject o, string name, string parent)
    {
        if (!o.TryGetPropertyValue(name, out var node) || node is null) return null;
        var kind = node.GetValueKind();
        if (kind is JsonValueKind.True or JsonValueKind.False) return kind == JsonValueKind.True;
        Error(Prop(parent, name), $"'{name}' must be true or false.");
        return null;
    }

    private int? ReadInt(JsonObject o, string name, string parent, int min, int max, bool required = false)
    {
        var path = Prop(parent, name);
        if (!o.TryGetPropertyValue(name, out var node) || node is null)
        {
            if (required) Error(path, $"'{name}' is required.");
            return null;
        }
        if (node.GetValueKind() != JsonValueKind.Number || !TryGetFiniteDouble(node, out var d) || d != Math.Floor(d))
        {
            Error(path, $"'{name}' must be an integer.");
            return null;
        }
        if (d < min || d > max)
        {
            Error(path, $"'{name}' must be between {min} and {max} (was {d.ToString(CultureInfo.InvariantCulture)}).");
            return null;
        }
        return (int)d;
    }

    private double? ReadDouble(JsonObject o, string name, string parent, double min, double max, bool required = false, bool minExclusive = false)
    {
        var path = Prop(parent, name);
        if (!o.TryGetPropertyValue(name, out var node) || node is null)
        {
            if (required) Error(path, $"'{name}' is required.");
            return null;
        }
        if (node.GetValueKind() != JsonValueKind.Number || !TryGetFiniteDouble(node, out var d))
        {
            Error(path, $"'{name}' must be a number.");
            return null;
        }
        if (d < min || d > max || (minExclusive && d == min))
        {
            var range = minExclusive
                ? $"greater than {min.ToString(CultureInfo.InvariantCulture)} and at most {max.ToString(CultureInfo.InvariantCulture)}"
                : $"between {min.ToString(CultureInfo.InvariantCulture)} and {max.ToString(CultureInfo.InvariantCulture)}";
            Error(path, $"'{name}' must be {range} (was {d.ToString(CultureInfo.InvariantCulture)}).");
            return null;
        }
        return d;
    }

    private T? ReadEnum<T>(JsonObject o, string name, string parent, EnumMap<T> map) where T : struct, Enum
    {
        var path = Prop(parent, name);
        if (!o.TryGetPropertyValue(name, out var node) || node is null) return null;
        if (node.GetValueKind() == JsonValueKind.String && map.TryParse(node.GetValue<string>(), out var value)) return value;
        var shown = node.GetValueKind() == JsonValueKind.String ? $"'{Truncate(node.GetValue<string>())}'" : "this value";
        Error(path, $"{shown} is not a valid '{name}'. Use one of {map.Describe()}.");
        return null;
    }

    private static bool TryGetFiniteDouble(JsonNode node, out double value)
    {
        value = 0;
        if (node is not JsonValue v) return false;
        if (!v.TryGetValue(out value) && !(v.TryGetValue(out JsonElement e) && e.TryGetDouble(out value))) return false;
        return double.IsFinite(value);
    }

    private void ValidateRegex(string pattern, RegexOptions regexOptions, string path)
    {
        if (pattern.Length > options.MaxRegexLength)
        {
            Error(path, $"The regular expression is {pattern.Length} characters long; at most {options.MaxRegexLength} are allowed.");
            return;
        }
        try
        {
            _ = new Regex(pattern, regexOptions, TimeSpan.FromMilliseconds(100));
        }
        catch (ArgumentException ex)
        {
            Error(path, $"Invalid regular expression: {ex.Message}");
        }
    }

    private ElementLocator? UnknownType(string path, string what, string type, IReadOnlyList<string> supported, string? hint)
    {
        Error(path, $"Unknown {what} type '{Truncate(type)}'. Supported types: {string.Join(", ", supported)}.{hint}");
        return null;
    }

    private void CheckUnknown(JsonObject o, string path, IReadOnlyList<string> known)
    {
        foreach (var (key, _) in o)
        {
            if (!known.Contains(key, StringComparer.Ordinal))
                Warning(Prop(path, key), $"Unknown property '{Truncate(key)}' is ignored (property names are case-sensitive).");
        }
    }

    private void Error(string path, string message)
    {
        diagnostics.Add(ProfileDiagnostic.Error(path, message));
        _errors++;
    }

    private void Warning(string path, string message) => diagnostics.Add(ProfileDiagnostic.Warning(path, message));

    private static string Truncate(string s) => s.Length <= 64 ? s : s[..64] + "...";

    /// <summary>Appends a property to a JSON path, using bracket notation for names that are not identifiers.</summary>
    internal static string Prop(string parent, string name)
    {
        bool simple = name.Length > 0 && (char.IsLetter(name[0]) || name[0] is '_' or '$') && name.All(c => char.IsLetterOrDigit(c) || c is '_' or '$');
        if (simple) return parent + "." + name;
        var escaped = name.Length > 64 ? name[..64] + "..." : name;
        return parent + "['" + escaped.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal) + "']";
    }
}
