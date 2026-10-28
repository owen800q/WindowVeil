using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using WindowVeil.Platform;
using WindowVeil.Profiles;
using WindowVeil.Profiles.Internal;
using static WindowVeil.UnitTests.Profiles.ProfileTestData;

namespace WindowVeil.UnitTests.Profiles;

/// <summary>
/// Keeps docs/profile.schema.json consistent with the serializer: every document the tests load or the serializer
/// writes must validate, invalid documents must not, and the schema's vocabularies must match the reader's.
/// </summary>
public class ProfileSchemaTests
{
    private static readonly Lazy<JsonObject> Schema = new(() =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "profile.schema.json")))!.AsObject());

    private static IReadOnlyList<string> Validate(string json) => new MiniSchemaValidator(Schema.Value).Validate(JsonNode.Parse(json));

    [Fact]
    public void Schema_IsDraft202012()
    {
        Assert.Equal("https://json-schema.org/draft/2020-12/schema", (string?)Schema.Value["$schema"]);
        Assert.Equal(["schemaVersion", "name", "application", "rules"], Schema.Value["required"]!.AsArray().Select(n => (string)n!));
    }

    [Theory]
    [InlineData(nameof(ProfileTestData.Example))]
    [InlineData(nameof(ProfileTestData.AllVariants))]
    public void TestDocuments_AreValidAgainstSchema(string name)
    {
        var json = name == nameof(ProfileTestData.Example) ? Example : AllVariants;
        Assert.Empty(Validate(json));
    }

    [Fact]
    public void SerializerOutput_IsValidAgainstSchema()
    {
        var outputs = new List<string>
        {
            ProfileSerializer.ToJson(ProfileSerializer.Load(Example).EnsureSuccess()),
            ProfileSerializer.ToJson(ProfileSerializer.Load(AllVariants).EnsureSuccess()),
            ProfileSerializer.ToJson(ProfileExporter.FromRules("Code", WindowSelector.ByProcessName("RetroCap"), CodeRules())),
            ProfileSerializer.ToJson(ProfileSerializer.Load("""{ "process": "Legacy", "rules": [ { "automationId": "A", "rect": { "x": 1, "y": 1, "width": 1, "height": 1 } } ] }""").EnsureSuccess()),
        };
        foreach (var json in outputs) Assert.True(Validate(json).Count == 0, json + "\n" + string.Join("\n", Validate(json)));
    }

    [Theory]
    [InlineData("""{ "schemaVersion": 1, "name": "x", "application": { "processName": "a" }, "rules": [ { "locator": { "type": "rectangle", "x": 0, "y": 0, "width": 1, "height": 1 }, "appearance": { "type": "solid", "color": "#000" } } ] }""")]
    [InlineData("""{ "schemaVersion": 1, "name": "x", "application": { "processName": "a" }, "rules": [ { "id": "a", "locator": { "type": "xpath" }, "appearance": { "type": "solid", "color": "#000" } } ] }""")]
    [InlineData("""{ "schemaVersion": 1, "name": "x", "application": { "processName": "a" }, "rules": [ { "id": "a", "locator": { "type": "uiAutomation" }, "appearance": { "type": "solid", "color": "#000" } } ] }""")]
    [InlineData("""{ "schemaVersion": 1, "name": "x", "application": { "processName": "a" }, "rules": [ { "id": "a", "locator": { "type": "text", "text": "a" }, "appearance": { "type": "solid", "color": "red" } } ] }""")]
    [InlineData("""{ "schemaVersion": 1, "name": "x", "application": { "processName": "a" }, "rules": [ { "id": "a", "locator": { "type": "text", "text": "a" }, "appearance": { "type": "solid", "color": "#000", "opacity": 2 } } ] }""")]
    [InlineData("""{ "schemaVersion": 1, "name": "x", "application": { "processName": "a" }, "rules": [ { "id": "a", "locator": { "type": "text", "text": "a" }, "appearance": { "type": "wpf" } } ] }""")]
    [InlineData("""{ "schemaVersion": 1, "name": "x", "application": { "processName": "a" }, "rules": [ { "id": "a", "locator": { "type": "text", "text": "a" }, "appearance": { "type": "solid", "color": "#000" }, "placement": "diagonal" } ] }""")]
    [InlineData("""{ "schemaVersion": 2, "name": "x", "application": { "processName": "a" }, "rules": [] }""")]
    [InlineData("""{ "schemaVersion": 1, "name": "x", "application": {}, "rules": [] }""")]
    public void InvalidDocuments_FailBothSchemaAndLoader(string json)
    {
        Assert.NotEmpty(Validate(json));
        Assert.False(ProfileSerializer.Load(json).Succeeded);
    }

    [Fact]
    public void UnknownProperty_SchemaRejectsForEditors_LoaderOnlyWarns()
    {
        const string json = """{ "schemaVersion": 1, "name": "x", "application": { "processName": "a" }, "rules": [], "extra": 1 }""";
        Assert.NotEmpty(Validate(json));
        var result = ProfileSerializer.Load(json);
        Assert.True(result.Succeeded);
        Assert.Equal(ProfileDiagnosticSeverity.Warning, Assert.Single(result.Diagnostics).Severity);
    }

    [Fact]
    public void SchemaVocabularies_MatchSerializer()
    {
        var defs = Schema.Value["$defs"]!.AsObject();
        string[] Consts(string def) => defs[def]!["oneOf"]!.AsArray()
            .Select(r => defs[((string)r!["$ref"]!).Split('/')[^1]]!["properties"]!["type"]!["const"]!.GetValue<string>()).ToArray();
        string[] Enum(JsonNode node) => node["enum"]!.AsArray().Select(n => (string)n!).ToArray();
        string[] Props(string def) => defs[def]!["properties"]!.AsObject().Select(kv => kv.Key).ToArray();

        Assert.Equal(ProfileVocabulary.LocatorTypes, Consts("locator"));
        Assert.Equal(ProfileVocabulary.AppearanceTypes, Consts("appearance"));
        Assert.Equal(ProfileVocabulary.RuleProperties, Props("rule"));
        Assert.Equal(ProfileVocabulary.ApplicationProperties, Props("application"));
        Assert.Equal(ProfileVocabulary.RootProperties, Schema.Value["properties"]!.AsObject().Select(kv => kv.Key));
        Assert.Equal(ProfileVocabulary.UiAutomationProperties, Props("uiAutomationSelector"));
        Assert.Equal([.. ProfileVocabulary.UiAutomationProperties, "fallbackName"], Props("uiAutomationLocator"));
        Assert.Equal(ProfileVocabulary.PlacementModes.Names, Enum(defs["placement"]!["oneOf"]![0]!));
        Assert.Equal(ProfileVocabulary.PlacementProperties, defs["placement"]!["oneOf"]![1]!["properties"]!.AsObject().Select(kv => kv.Key));
        Assert.Equal(ProfileVocabulary.TextMatches.Names, Enum(defs["textMatch"]!));
        Assert.Equal(ProfileVocabulary.Anchors.Names, Enum(defs["anchor"]!));
        var rule = defs["rule"]!["properties"]!;
        Assert.Equal(ProfileVocabulary.InputModes.Names, Enum(rule["inputMode"]!));
        Assert.Equal(ProfileVocabulary.FocusPolicies.Names, Enum(rule["focusPolicy"]!));
        Assert.Equal(ProfileVocabulary.ZOrders.Names, Enum(rule["zOrder"]!));
        Assert.Equal(ProfileVocabulary.Visibilities.Names, Enum(rule["visibility"]!));
        Assert.Equal(ProfileVocabulary.SpotlightScopes.Names, Enum(defs["spotlightAppearance"]!["properties"]!["scope"]!));
        Assert.Equal(ProfileLoadOptions.Default.MaxRules, (int)Schema.Value["properties"]!["rules"]!["maxItems"]!);
    }
}

/// <summary>
/// A deliberately small JSON Schema (2020-12) subset validator: $ref (local), type, const, enum, required, properties,
/// additionalProperties, minimum/maximum/exclusiveMinimum, minLength/maxLength, pattern, items, minItems/maxItems,
/// maxProperties, oneOf, anyOf. Enough to keep the documented schema honest without a third-party dependency.
/// </summary>
internal sealed class MiniSchemaValidator(JsonObject root)
{
    public IReadOnlyList<string> Validate(JsonNode? instance)
    {
        var errors = new List<string>();
        Check(root, instance, "$", errors);
        return errors;
    }

    private void Check(JsonNode schemaNode, JsonNode? value, string path, List<string> errors)
    {
        var schema = schemaNode.AsObject();
        if (schema["$ref"] is { } refNode)
        {
            var parts = ((string)refNode!).TrimStart('#', '/').Split('/');
            JsonNode target = root;
            foreach (var p in parts) target = target[p] ?? throw new InvalidOperationException("Bad $ref " + refNode);
            Check(target, value, path, errors);
        }
        var kind = value?.GetValueKind() ?? JsonValueKind.Null;

        if (schema["type"] is { } type && !TypeMatches((string)type!, value, kind)) { errors.Add($"{path}: expected {type}"); return; }
        if (schema["const"] is { } c && !JsonNode.DeepEquals(c, value)) errors.Add($"{path}: expected const {c.ToJsonString()}");
        if (schema["enum"] is JsonArray e && !e.Any(x => JsonNode.DeepEquals(x, value))) errors.Add($"{path}: not in enum");

        if (kind == JsonValueKind.Number)
        {
            var d = value!.GetValue<double>();
            if (schema["minimum"] is { } min && d < (double)min) errors.Add($"{path}: below minimum");
            if (schema["maximum"] is { } max && d > (double)max) errors.Add($"{path}: above maximum");
            if (schema["exclusiveMinimum"] is { } emin && d <= (double)emin) errors.Add($"{path}: not above exclusiveMinimum");
        }
        if (kind == JsonValueKind.String)
        {
            var s = value!.GetValue<string>();
            if (schema["minLength"] is { } minL && s.Length < (int)minL) errors.Add($"{path}: too short");
            if (schema["maxLength"] is { } maxL && s.Length > (int)maxL) errors.Add($"{path}: too long");
            if (schema["pattern"] is { } pat && !Regex.IsMatch(s, (string)pat!, RegexOptions.None, TimeSpan.FromSeconds(1))) errors.Add($"{path}: pattern mismatch");
        }
        if (value is JsonObject obj)
        {
            if (schema["required"] is JsonArray req)
                foreach (var r in req) if (!obj.ContainsKey((string)r!)) errors.Add($"{path}: missing required '{r}'");
            if (schema["maxProperties"] is { } maxP && obj.Count > (int)maxP) errors.Add($"{path}: too many properties");
            var props = schema["properties"] as JsonObject;
            foreach (var (key, child) in obj)
            {
                var childPath = $"{path}.{key}";
                if (props?[key] is { } ps) Check(ps, child, childPath, errors);
                else if (schema["additionalProperties"] is { } ap)
                {
                    if (ap.GetValueKind() == JsonValueKind.False) errors.Add($"{childPath}: additional property not allowed");
                    else if (ap is JsonObject) Check(ap, child, childPath, errors);
                }
            }
        }
        if (value is JsonArray arr)
        {
            if (schema["minItems"] is { } minI && arr.Count < (int)minI) errors.Add($"{path}: too few items");
            if (schema["maxItems"] is { } maxI && arr.Count > (int)maxI) errors.Add($"{path}: too many items");
            if (schema["items"] is { } items)
                for (int i = 0; i < arr.Count; i++) Check(items, arr[i], $"{path}[{i}]", errors);
        }
        if (schema["oneOf"] is JsonArray oneOf)
        {
            int matches = oneOf.Count(s => Sub(s!, value, path).Count == 0);
            if (matches != 1) errors.Add($"{path}: matches {matches} of oneOf");
        }
        if (schema["anyOf"] is JsonArray anyOf && !anyOf.Any(s => Sub(s!, value, path).Count == 0)) errors.Add($"{path}: matches none of anyOf");
    }

    private List<string> Sub(JsonNode schema, JsonNode? value, string path)
    {
        var errors = new List<string>();
        Check(schema, value, path, errors);
        return errors;
    }

    private static bool TypeMatches(string type, JsonNode? value, JsonValueKind kind) => type switch
    {
        "object" => kind == JsonValueKind.Object,
        "array" => kind == JsonValueKind.Array,
        "string" => kind == JsonValueKind.String,
        "boolean" => kind is JsonValueKind.True or JsonValueKind.False,
        "number" => kind == JsonValueKind.Number,
        "integer" => kind == JsonValueKind.Number && double.Parse(value!.ToJsonString(), CultureInfo.InvariantCulture) is var d && d == Math.Floor(d),
        _ => throw new InvalidOperationException("Unsupported type " + type),
    };
}
