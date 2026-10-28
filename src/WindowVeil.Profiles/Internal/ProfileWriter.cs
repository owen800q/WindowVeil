using System.Text.Json.Nodes;
using WindowVeil.Appearance;
using WindowVeil.Geometry;
using WindowVeil.Imaging;
using WindowVeil.Locators;
using WindowVeil.Rendering;
using WindowVeil.Text;
using static WindowVeil.Profiles.Internal.ProfileVocabulary;

namespace WindowVeil.Profiles.Internal;

/// <summary>
/// Converts an <see cref="OverlayProfile"/> to a schema-v1 JSON tree. Only data with an explicit profile
/// representation is written; anything else (custom appearances, custom locator kinds, selectors with handles)
/// becomes an error diagnostic. Object graphs are never serialized.
/// </summary>
internal sealed class ProfileWriter(List<ProfileDiagnostic> diagnostics)
{
    private int _errors;

    public JsonObject? Write(OverlayProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        int before = _errors;
        if (string.IsNullOrWhiteSpace(profile.Name)) Error("$.name", "The profile needs a name.");
        if (profile.SchemaVersion != OverlayProfile.CurrentSchemaVersion)
            Error("$.schemaVersion", $"Only schema version {OverlayProfile.CurrentSchemaVersion} can be written (the profile says {profile.SchemaVersion}).");
        var app = profile.Application;
        if (app is null || app.IsEmpty)
            Error("$.application", "The profile needs an application selector (processName, titleContains, titlePattern or className).");

        var root = new JsonObject
        {
            ["schemaVersion"] = OverlayProfile.CurrentSchemaVersion,
            ["name"] = profile.Name,
        };
        if (app is not null && !app.IsEmpty)
        {
            var a = new JsonObject();
            AddIfNotNull(a, "processName", app.ProcessName);
            AddIfNotNull(a, "titleContains", app.TitleContains);
            AddIfNotNull(a, "titlePattern", app.TitlePattern);
            AddIfNotNull(a, "className", app.ClassName);
            if (!app.RequireVisible) a["requireVisible"] = false;
            if (!app.RequireTitle) a["requireTitle"] = false;
            root["application"] = a;
        }

        var rules = new JsonArray();
        var ids = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < profile.Rules.Count; i++)
        {
            var path = $"$.rules[{i}]";
            var rule = profile.Rules[i];
            if (rule is null) { Error(path, "Rules cannot be null."); continue; }
            if (rule.Id is not null)
            {
                if (ids.TryGetValue(rule.Id, out var first)) Error(Prop(path, "id"), $"Duplicate rule id '{rule.Id}' (first used by $.rules[{first}]).");
                else ids.Add(rule.Id, i);
            }
            if (WriteRule(rule, path) is { } r) rules.Add(r);
        }
        root["rules"] = rules;
        return _errors > before ? null : root;
    }

    private JsonObject? WriteRule(OverlayRule rule, string path)
    {
        int before = _errors;
        try
        {
            rule.Validate();
        }
        catch (WindowVeilException ex)
        {
            Error(path, ex.Message);
            return null;
        }
        var o = new JsonObject { ["id"] = rule.Id };
        AddIfNotNull(o, "name", rule.Name);
        AddIfNotNull(o, "group", rule.Group);
        o["locator"] = WriteLocator(rule.Target, Prop(path, "locator"), 1);
        o["appearance"] = WriteAppearance(rule.Appearance, Prop(path, "appearance"));
        o["placement"] = WritePlacement(rule.Placement, Prop(path, "placement"));
        if (rule.InputMode != OverlayInputMode.PassThrough) o["inputMode"] = Format(InputModes, rule.InputMode, Prop(path, "inputMode"));
        if (rule.FocusPolicy != OverlayFocusPolicy.NoActivate) o["focusPolicy"] = Format(FocusPolicies, rule.FocusPolicy, Prop(path, "focusPolicy"));
        if (rule.ZOrder != OverlayZOrder.AboveTarget) o["zOrder"] = Format(ZOrders, rule.ZOrder, Prop(path, "zOrder"));
        o["followTarget"] = rule.FollowTarget;
        o["enabled"] = rule.Enabled;
        if (rule.Priority != 0) o["priority"] = rule.Priority;
        if (rule.Visibility != OverlayVisibility.WhenTargetVisible) o["visibility"] = Format(Visibilities, rule.Visibility, Prop(path, "visibility"));
        return _errors > before ? null : o;
    }

    // ------------------------------------------------------------------ locators

    private JsonObject? WriteLocator(ElementLocator locator, string path, int depth)
    {
        if (depth > ProfileLoadOptions.Default.MaxLocatorDepth)
        {
            Error(path, $"Locators are nested more than {ProfileLoadOptions.Default.MaxLocatorDepth} levels deep; the profile could not be loaded again.");
            return null;
        }
        switch (locator)
        {
            case ScreenRectangleLocator s:
                return Rect(new JsonObject { ["type"] = "rectangle" }, s.Bounds);
            case WindowRelativeLocator w:
            {
                var o = Rect(new JsonObject { ["type"] = "windowRelative" }, w.Bounds);
                o["anchor"] = Format(Anchors, w.Anchor, Prop(path, "anchor"));
                return o;
            }
            case NormalizedLocator n:
                return new JsonObject
                {
                    ["type"] = "normalized",
                    ["x"] = n.Bounds.X, ["y"] = n.Bounds.Y, ["width"] = n.Bounds.Width, ["height"] = n.Bounds.Height,
                    ["anchor"] = Format(Anchors, n.Anchor, Prop(path, "anchor")),
                };
            case UiAutomationLocator u:
            {
                var o = new JsonObject { ["type"] = "uiAutomation" };
                WriteUiAutomation(o, u, path, depth, fallbackName: null);
                return o;
            }
            case FallbackLocator f when TryMatchFallbackName(f, out var primary, out var fallbackName):
            {
                var o = new JsonObject { ["type"] = "uiAutomation" };
                WriteUiAutomation(o, primary!, path, depth, fallbackName);
                return o;
            }
            case FallbackLocator f:
            {
                var list = new JsonArray();
                for (int i = 0; i < f.Locators.Count; i++)
                    list.Add(WriteLocator(f.Locators[i], $"{Prop(path, "locators")}[{i}]", depth + 1));
                var o = new JsonObject { ["type"] = "fallback", ["locators"] = list };
                if (!f.ContinueOnError) o["continueOnError"] = false;
                return o;
            }
            case TextLocator t:
            {
                var o = new JsonObject { ["type"] = "text" };
                WriteQuery(o, t.Query, path, TextMatch.Contains, ignoreCaseDefault: true);
                if (!t.AllMatches) o["allMatches"] = false;
                if (t.Scope is not null)
                {
                    var scope = new JsonObject();
                    WriteUiAutomation(scope, t.Scope, Prop(path, "scope"), depth + 1, fallbackName: null);
                    o["scope"] = scope;
                }
                return o;
            }
            case OcrTextLocator ocr:
                return WriteOcr(ocr, path);
            default:
                Error(path, $"Locator kind '{locator.Kind}' ({locator.GetType().Name}) has no profile representation. " +
                            $"Profiles support: {string.Join(", ", LocatorTypes)}.");
                return null;
        }
    }

    private void WriteUiAutomation(JsonObject o, UiAutomationLocator u, string path, int depth, TextQuery? fallbackName)
    {
        if (depth > ProfileLoadOptions.Default.MaxLocatorDepth)
        {
            Error(path, $"Locators are nested more than {ProfileLoadOptions.Default.MaxLocatorDepth} levels deep; the profile could not be loaded again.");
            return;
        }
        AddIfNotNull(o, "automationId", u.AutomationId);
        if (u.Name is not null) o["name"] = u.Name.Pattern;
        var shared = u.Name ?? fallbackName;
        if (shared is not null)
        {
            CheckNormalization(shared, path);
            if (shared.Mode != TextMatch.Exact) o["nameMatch"] = Format(TextMatches, shared.Mode, Prop(path, "nameMatch"));
            if (shared.IgnoreCase) o["ignoreCase"] = true;
        }
        AddIfNotNull(o, "controlType", u.ControlType);
        AddIfNotNull(o, "className", u.ClassName);
        if (u.Index != 0) o["index"] = u.Index;
        if (u.MaxDepth != 32) o["maxDepth"] = u.MaxDepth;
        if (u.MaxNodes != 5000) o["maxNodes"] = u.MaxNodes;
        if (u.Ancestor is not null)
        {
            var a = new JsonObject();
            WriteUiAutomation(a, u.Ancestor, Prop(path, "ancestor"), depth + 1, fallbackName: null);
            o["ancestor"] = a;
        }
        if (fallbackName is not null) o["fallbackName"] = fallbackName.Pattern;
    }

    private JsonObject WriteOcr(OcrTextLocator ocr, string path)
    {
        var o = new JsonObject { ["type"] = "ocrText" };
        WriteQuery(o, ocr.Query, path, TextMatch.Contains, ignoreCaseDefault: true);
        o["minConfidence"] = ocr.MinConfidence;
        if (ocr.SearchRegion is { } region) o["searchRegion"] = Rect(new JsonObject(), region);
        if (!ocr.Expand.IsZero) o["expand"] = WriteInsets(ocr.Expand);
        if (ocr.MustNotOverlap.Count > 0)
        {
            var list = new JsonArray();
            for (int i = 0; i < ocr.MustNotOverlap.Count; i++)
            {
                var item = new JsonObject();
                WriteQuery(item, ocr.MustNotOverlap[i], $"{Prop(path, "mustNotOverlap")}[{i}]", TextMatch.Contains, ignoreCaseDefault: true);
                list.Add(item);
            }
            o["mustNotOverlap"] = list;
        }
        var ms = (long)Math.Round(ocr.MinInterval.TotalMilliseconds);
        if (ms < 0 || ms > MaxMinIntervalMs) Error(Prop(path, "minIntervalMs"), $"The OCR interval must be between 0 and {MaxMinIntervalMs} ms.");
        else if (TimeSpan.FromMilliseconds(ms) != ocr.MinInterval)
            Warning(Prop(path, "minIntervalMs"), $"The OCR interval {ocr.MinInterval} is rounded to whole milliseconds.");
        if (ms != 1000) o["minIntervalMs"] = ms;
        if (!ocr.AllMatches) o["allMatches"] = false;
        return o;
    }

    private void WriteQuery(JsonObject o, TextQuery query, string path, TextMatch matchDefault, bool ignoreCaseDefault)
    {
        CheckNormalization(query, path);
        o["text"] = query.Pattern;
        if (query.Mode != matchDefault) o["match"] = Format(TextMatches, query.Mode, Prop(path, "match"));
        if (query.IgnoreCase != ignoreCaseDefault) o["ignoreCase"] = query.IgnoreCase;
    }

    private void CheckNormalization(TextQuery query, string path)
    {
        if (!query.NormalizeWhitespace)
            Error(path, "Text queries with NormalizeWhitespace = false have no profile representation (profiles always normalize whitespace).");
    }

    // ------------------------------------------------------------------ appearance

    private JsonObject? WriteAppearance(OverlayAppearance appearance, string path)
    {
        switch (appearance)
        {
            case MaskAppearance m:
                return new JsonObject { ["type"] = m.Opacity >= 1 ? "solid" : "transparent", ["color"] = Color(m.Color), ["opacity"] = m.Opacity };
            case HighlightAppearance h:
                return new JsonObject { ["type"] = "highlight", ["color"] = Color(h.Color), ["thickness"] = h.Thickness, ["opacity"] = h.Opacity };
            case SpotlightAppearance s:
                return new JsonObject
                {
                    ["type"] = "spotlight", ["color"] = Color(s.DimColor), ["opacity"] = s.Opacity,
                    ["scope"] = Format(SpotlightScopes, s.Scope, Prop(path, "scope")),
                };
            case RegisteredAppearance r:
            {
                if (string.IsNullOrWhiteSpace(r.FactoryId)) { Error(Prop(path, "factory"), "The registered appearance has no factory id."); return null; }
                var o = new JsonObject { ["type"] = "registered", ["factory"] = r.FactoryId };
                if (r.Parameters.Count > 0)
                {
                    var p = new JsonObject();
                    foreach (var (k, v) in r.Parameters.OrderBy(kv => kv.Key, StringComparer.Ordinal)) p[k] = v;
                    o["parameters"] = p;
                }
                return o;
            }
            default:
                Error(path,
                    $"Appearance '{appearance.Kind}' ({appearance.GetType().Name}) cannot be stored in a profile: it is created in code and may " +
                    "reference arbitrary objects, and profiles never serialize object graphs. Register a factory with " +
                    "AppearanceRegistry.Register(\"<id>\", parameters => ...) and use new RegisteredAppearance(\"<id>\") in the rule.");
                return null;
        }
    }

    private static string Color(OverlayColor c) => c.WithAlpha(255).ToString();

    // ------------------------------------------------------------------ placement

    private JsonNode? WritePlacement(OverlayPlacement placement, string path)
    {
        if (!PlacementModes.CanFormat(placement.Mode))
        {
            Error(Prop(path, "mode"), $"Placement mode {placement.Mode} has no profile representation.");
            return null;
        }
        var mode = PlacementModes.Format(placement.Mode);
        var preset = Preset(placement.Mode);
        if (placement == preset) return JsonValue.Create(mode);
        var o = new JsonObject { ["mode"] = mode };
        if (placement.OffsetX != preset.OffsetX) o["offsetX"] = placement.OffsetX;
        if (placement.OffsetY != preset.OffsetY) o["offsetY"] = placement.OffsetY;
        if (placement.Padding != preset.Padding) o["padding"] = WriteInsets(placement.Padding);
        if (placement.Width is { } w) o["width"] = w;
        if (placement.Height is { } h) o["height"] = h;
        if (placement.Alignment != preset.Alignment) o["alignment"] = Format(Alignments, placement.Alignment, Prop(path, "alignment"));
        if (placement.MultiRect != preset.MultiRect) o["multiRect"] = Format(MultiRectModes, placement.MultiRect, Prop(path, "multiRect"));
        return o;
    }

    private static JsonNode WriteInsets(Insets i) =>
        i.Left == i.Top && i.Top == i.Right && i.Right == i.Bottom
            ? JsonValue.Create(i.Left)
            : new JsonObject { ["left"] = i.Left, ["top"] = i.Top, ["right"] = i.Right, ["bottom"] = i.Bottom };

    // ------------------------------------------------------------------ helpers

    private static JsonObject Rect(JsonObject o, PixelRect r)
    {
        o["x"] = r.X;
        o["y"] = r.Y;
        o["width"] = r.Width;
        o["height"] = r.Height;
        return o;
    }

    private string? Format<T>(EnumMap<T> map, T value, string path) where T : struct, Enum
    {
        if (map.CanFormat(value)) return map.Format(value);
        Error(path, $"{typeof(T).Name}.{value} has no profile representation.");
        return null;
    }

    private static void AddIfNotNull(JsonObject o, string name, string? value)
    {
        if (value is not null) o[name] = value;
    }

    private void Error(string path, string message)
    {
        diagnostics.Add(ProfileDiagnostic.Error(path, message));
        _errors++;
    }

    private void Warning(string path, string message) => diagnostics.Add(ProfileDiagnostic.Warning(path, message));

    private static string Prop(string parent, string name) => ProfileReader.Prop(parent, name);
}
