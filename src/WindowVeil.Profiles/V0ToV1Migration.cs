using System.Text.Json;
using System.Text.Json.Nodes;

namespace WindowVeil.Profiles;

/// <summary>
/// Upgrades the pre-release 0.x draft format (schema version 0) to version 1.
/// </summary>
/// <remarks>
/// <para>The 0.x draft format had no <c>schemaVersion</c> (or <c>"schemaVersion": 0</c>) and looked like this:</para>
/// <code>
/// {
///   "name": "RetroCap",                    // optional
///   "process": "RetroCap",                 // became application.processName
///   "rules": [
///     {
///       "id": "hide-startup",              // optional; generated as rule-1, rule-2, ... when missing
///       "automationId": "StartupCheck",    // UI Automation AutomationId
///       "name": "Start automatically",     // UI Automation Name (NOT a display name)
///       "rect": { "x": 10, "y": 20, "width": 200, "height": 24 },  // client-relative pixels
///       "mask": { "color": "#202020", "opacity": 1.0 }
///     }
///   ]
/// }
/// </code>
/// <para>Mapping to version 1:</para>
/// <list type="bullet">
/// <item><c>process</c> becomes <c>application.processName</c>; a missing <c>name</c> defaults to the process name.</item>
/// <item><c>automationId</c> + <c>name</c> become a <c>uiAutomation</c> locator; when both are present the name is the
/// <c>fallbackName</c> (the draft resolver tried the AutomationId first, then the Name).</item>
/// <item><c>rect</c> becomes a client-anchored <c>windowRelative</c> locator; combined with element conditions it is
/// the last entry of a <c>fallback</c> chain.</item>
/// <item><c>mask</c> becomes a <c>solid</c> (opacity 1) or <c>transparent</c> appearance; the draft default was opaque black.</item>
/// </list>
/// <para>Rules that already use the v1 <c>locator</c>/<c>appearance</c> shape are left unchanged.</para>
/// </remarks>
public sealed class V0ToV1Migration : IProfileMigration
{
    /// <inheritdoc />
    public int FromVersion => 0;

    /// <inheritdoc />
    public int ToVersion => 1;

    /// <inheritdoc />
    public void Migrate(JsonObject document, ICollection<ProfileDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(diagnostics);

        if (document.TryGetPropertyValue("process", out var process))
        {
            document.Remove("process");
            if (document.ContainsKey("application"))
            {
                diagnostics.Add(ProfileDiagnostic.Warning("$.process", "Both 'process' (draft format) and 'application' are present; 'process' is ignored."));
            }
            else if (process is JsonValue pv && pv.GetValueKind() == JsonValueKind.String)
            {
                document["application"] = new JsonObject { ["processName"] = pv.GetValue<string>() };
                if (!document.ContainsKey("name") || document["name"] is null) document["name"] = pv.GetValue<string>();
            }
            else
            {
                diagnostics.Add(ProfileDiagnostic.Error("$.process", "'process' must be a string (the process name)."));
            }
        }
        if (!document.ContainsKey("name") || document["name"] is null) document["name"] = "Migrated profile";

        if (document["rules"] is not JsonArray rules) return; // reported by the v1 reader
        for (int i = 0; i < rules.Count; i++)
        {
            if (rules[i] is JsonObject rule) MigrateRule(rule, i, diagnostics);
        }
    }

    private static void MigrateRule(JsonObject rule, int index, ICollection<ProfileDiagnostic> diagnostics)
    {
        var path = $"$.rules[{index}]";
        if (!rule.ContainsKey("id") || rule["id"] is null)
        {
            rule["id"] = $"rule-{index + 1}";
            diagnostics.Add(ProfileDiagnostic.Info(path, $"The draft rule had no id; it was named 'rule-{index + 1}'."));
        }

        if (!rule.ContainsKey("locator"))
        {
            var automationId = Take(rule, "automationId");
            var elementName = Take(rule, "name");
            var rect = Take(rule, "rect");

            JsonObject? uia = null;
            if (automationId is not null || elementName is not null)
            {
                uia = new JsonObject { ["type"] = "uiAutomation" };
                if (automationId is not null) uia["automationId"] = automationId;
                if (elementName is not null) uia[automationId is not null ? "fallbackName" : "name"] = elementName;
            }
            JsonObject? rectangle = null;
            if (rect is JsonObject r)
            {
                rectangle = new JsonObject { ["type"] = "windowRelative", ["anchor"] = "client" };
                foreach (var key in new[] { "x", "y", "width", "height" })
                {
                    if (r.TryGetPropertyValue(key, out var value)) rectangle[key] = value?.DeepClone();
                }
            }
            else if (rect is not null)
            {
                diagnostics.Add(ProfileDiagnostic.Error($"{path}.rect", "'rect' must be an object { x, y, width, height }."));
            }

            if (uia is not null && rectangle is not null) rule["locator"] = new JsonObject { ["type"] = "fallback", ["locators"] = new JsonArray(uia, rectangle) };
            else if (uia is not null) rule["locator"] = uia;
            else if (rectangle is not null) rule["locator"] = rectangle;
            // Otherwise the v1 reader reports the missing locator.
        }

        if (!rule.ContainsKey("appearance"))
        {
            var mask = Take(rule, "mask");
            var appearance = new JsonObject();
            JsonNode? color = "#000000", opacity = 1.0;
            if (mask is JsonObject m)
            {
                if (m.TryGetPropertyValue("color", out var c) && c is not null) color = c.DeepClone();
                if (m.TryGetPropertyValue("opacity", out var o) && o is not null) opacity = o.DeepClone();
            }
            else if (mask is not null)
            {
                diagnostics.Add(ProfileDiagnostic.Error($"{path}.mask", "'mask' must be an object { color, opacity }."));
            }
            bool translucent = opacity is JsonValue ov && ov.TryGetValue(out double d) && d < 1;
            appearance["type"] = translucent ? "transparent" : "solid";
            appearance["color"] = color;
            appearance["opacity"] = opacity;
            rule["appearance"] = appearance;
        }
    }

    private static JsonNode? Take(JsonObject o, string name)
    {
        if (!o.TryGetPropertyValue(name, out var value)) return null;
        o.Remove(name);
        return value;
    }
}
