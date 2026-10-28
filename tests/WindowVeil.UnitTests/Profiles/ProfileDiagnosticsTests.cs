using System.Text;
using WindowVeil.Profiles;

namespace WindowVeil.UnitTests.Profiles;

public class ProfileDiagnosticsTests
{
    private const string Loc = """{ "type": "rectangle", "x": 0, "y": 0, "width": 10, "height": 10 }""";
    private const string App = """{ "type": "solid", "color": "#000" }""";

    private static string Doc(params string[] rules) =>
        $$"""{ "schemaVersion": 1, "name": "T", "application": { "processName": "RetroCap" }, "rules": [ {{string.Join(",\n", rules)}} ] }""";

    private static string Rule(string id = "r", string locator = Loc, string appearance = App, string extra = "") =>
        $$"""{ "id": "{{id}}", "locator": {{locator}}, "appearance": {{appearance}}{{(extra.Length > 0 ? ", " + extra : "")}} }""";

    private static ProfileDiagnostic SingleError(ProfileLoadResult result)
    {
        var errors = result.Errors.ToArray();
        Assert.True(errors.Length == 1, "Expected one error, got:\n" + string.Join("\n", result.Diagnostics));
        return errors[0];
    }

    private static void AssertError(string json, string path, string messageFragment)
    {
        var result = ProfileSerializer.Load(json);
        var error = SingleError(result);
        Assert.Equal(path, error.Path);
        Assert.Contains(messageFragment, error.Message, StringComparison.Ordinal);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public void MissingId() =>
        AssertError(Doc($$"""{ "locator": {{Loc}}, "appearance": {{App}} }"""), "$.rules[0].id", "'id' is required");

    [Fact]
    public void DuplicateId_SecondRuleRejected_FirstKept()
    {
        var result = ProfileSerializer.Load(Doc(Rule("a"), Rule("b"), Rule("a")));
        var error = SingleError(result);
        Assert.Equal("$.rules[2].id", error.Path);
        Assert.Contains("Duplicate rule id 'a' (first used by $.rules[0])", error.Message, StringComparison.Ordinal);
        Assert.Equal(["a", "b"], result.Profile!.Rules.Select(r => r.Id));
    }

    [Fact]
    public void UnknownLocatorType() =>
        AssertError(Doc(Rule(locator: """{ "type": "xpath", "query": "//Button" }""")), "$.rules[0].locator.type", "Unknown locator type 'xpath'. Supported types: rectangle, windowRelative");

    [Fact]
    public void UnknownAppearanceType_NeverInstantiatesTypeNames()
    {
        AssertError(Doc(Rule(appearance: """{ "type": "System.Diagnostics.Process, System.Diagnostics.Process", "fileName": "calc.exe" }""")),
            "$.rules[0].appearance.type", "register a factory");
        AssertError(Doc(Rule(appearance: """{ "type": "wpf", "$type": "WindowVeil.Wpf.WpfAppearance" }""")),
            "$.rules[0].appearance.type", "Unknown appearance type 'wpf'");
    }

    [Theory]
    [InlineData("#12", "'#12' is not a valid color")]
    [InlineData("red", "'red' is not a valid color")]
    public void BadColor(string color, string message) =>
        AssertError(Doc(Rule(appearance: $$"""{ "type": "solid", "color": "{{color}}" }""")), "$.rules[0].appearance.color", message);

    [Fact]
    public void ColorWithAlpha_IsWarningOnly()
    {
        var result = ProfileSerializer.Load(Doc(Rule(appearance: """{ "type": "solid", "color": "#80FF0000" }""")));
        Assert.True(result.Succeeded);
        var w = Assert.Single(result.Diagnostics);
        Assert.Equal(ProfileDiagnosticSeverity.Warning, w.Severity);
        Assert.Equal("$.rules[0].appearance.color", w.Path);
    }

    [Theory]
    [InlineData("""{ "type": "solid", "color": "#000", "opacity": 1.5 }""", "$.rules[0].appearance.opacity", "between 0 and 1 (was 1.5)")]
    [InlineData("""{ "type": "transparent", "color": "#000", "opacity": -0.1 }""", "$.rules[0].appearance.opacity", "between 0 and 1")]
    [InlineData("""{ "type": "highlight", "color": "#000", "thickness": 0 }""", "$.rules[0].appearance.thickness", "greater than 0")]
    [InlineData("""{ "type": "spotlight", "opacity": "0.5" }""", "$.rules[0].appearance.opacity", "must be a number")]
    [InlineData("""{ "type": "spotlight", "scope": "desktop" }""", "$.rules[0].appearance.scope", "'targetWindow', 'monitor'")]
    [InlineData("""{ "type": "registered", "factory": "badge", "parameters": { "size": 12 } }""", "$.rules[0].appearance.parameters.size", "must be strings")]
    [InlineData("""{ "type": "registered" }""", "$.rules[0].appearance.factory", "'factory' is required")]
    [InlineData("""{ "color": "#000" }""", "$.rules[0].appearance.type", "'type' is required")]
    [InlineData("\"#000\"", "$.rules[0].appearance", "must be an object")]
    public void InvalidAppearanceValues(string appearance, string path, string message) =>
        AssertError(Doc(Rule(appearance: appearance)), path, message);

    [Theory]
    [InlineData("""{ "type": "uiAutomation", "name": "(unclosed", "nameMatch": "regex" }""", "$.rules[0].locator.name", "Invalid regular expression")]
    [InlineData("""{ "type": "text", "text": "[a-", "match": "regex" }""", "$.rules[0].locator.text", "Invalid regular expression")]
    [InlineData("""{ "type": "ocrText", "text": "x", "mustNotOverlap": [ { "text": "ok" }, { "text": "*bad", "match": "regex" } ] }""", "$.rules[0].locator.mustNotOverlap[1].text", "Invalid regular expression")]
    [InlineData("""{ "type": "uiAutomation" }""", "$.rules[0].locator", "needs at least one of 'automationId'")]
    [InlineData("""{ "type": "uiAutomation", "fallbackName": "x" }""", "$.rules[0].locator", "'fallbackName' is only a fallback")]
    [InlineData("""{ "type": "uiAutomation", "automationId": "a", "ancestor": { "automationId": "b", "fallbackName": "c" } }""", "$.rules[0].locator.ancestor.fallbackName", "only supported on a top-level")]
    [InlineData("""{ "type": "uiAutomation", "automationId": "a", "ancestor": { "type": "text", "automationId": "b" } }""", "$.rules[0].locator.ancestor.type", "Must be 'uiAutomation'")]
    [InlineData("""{ "type": "uiAutomation", "automationId": "a", "maxNodes": 100000000 }""", "$.rules[0].locator.maxNodes", "between 1 and 100000")]
    [InlineData("""{ "type": "uiAutomation", "automationId": "a", "index": -1 }""", "$.rules[0].locator.index", "between 0 and 10000")]
    [InlineData("""{ "type": "uiAutomation", "automationId": "" }""", "$.rules[0].locator.automationId", "cannot be empty")]
    [InlineData("""{ "type": "uiAutomation", "automationId": "a", "nameMatch": "fuzzy" }""", "$.rules[0].locator.nameMatch", "'exact', 'contains', 'startsWith', 'regex'")]
    [InlineData("""{ "type": "rectangle", "x": 1.5, "y": 0, "width": 1, "height": 1 }""", "$.rules[0].locator.x", "must be an integer")]
    [InlineData("""{ "type": "rectangle", "x": 0, "y": 0, "width": 0, "height": 1 }""", "$.rules[0].locator.width", "between 1 and 1000000")]
    [InlineData("""{ "type": "rectangle", "x": 0, "y": 0, "width": 1 }""", "$.rules[0].locator.height", "'height' is required")]
    [InlineData("""{ "type": "windowRelative", "x": 0, "y": 0, "width": 1, "height": 1, "anchor": "screen" }""", "$.rules[0].locator.anchor", "'client', 'window'")]
    [InlineData("""{ "type": "normalized", "x": 0, "y": 0, "width": 1.5, "height": 1 }""", "$.rules[0].locator.width", "at most 1")]
    [InlineData("""{ "type": "ocrText", "text": "x", "minConfidence": 2 }""", "$.rules[0].locator.minConfidence", "between 0 and 1")]
    [InlineData("""{ "type": "ocrText", "text": "x", "searchRegion": [0, 0, 1, 1] }""", "$.rules[0].locator.searchRegion", "must be an object")]
    [InlineData("""{ "type": "ocrText", "text": "x", "expand": "wide" }""", "$.rules[0].locator.expand", "number or an object")]
    [InlineData("""{ "type": "ocrText", "text": "x", "minIntervalMs": -1 }""", "$.rules[0].locator.minIntervalMs", "between 0 and 86400000")]
    [InlineData("""{ "type": "fallback", "locators": [] }""", "$.rules[0].locator.locators", "at least one locator")]
    [InlineData("""{ "type": "fallback", "locators": [ { "type": "uiAutomation", "automationId": "a" }, { "type": "bogus" } ] }""", "$.rules[0].locator.locators[1].type", "Unknown locator type 'bogus'")]
    [InlineData("""{ "type": "text" }""", "$.rules[0].locator.text", "'text' is required")]
    [InlineData("[]", "$.rules[0].locator", "must be an object")]
    public void InvalidLocatorValues(string locator, string path, string message) =>
        AssertError(Doc(Rule(locator: locator)), path, message);

    [Theory]
    [InlineData("\"placement\": \"diagonal\"", "$.rules[0].placement", "Unknown placement 'diagonal'")]
    [InlineData("\"placement\": 3", "$.rules[0].placement", "must be one of")]
    [InlineData("\"placement\": { \"mode\": \"right\", \"width\": 0 }", "$.rules[0].placement.width", "greater than 0")]
    [InlineData("\"placement\": { \"padding\": { \"left\": \"x\" } }", "$.rules[0].placement.padding.left", "must be a number")]
    [InlineData("\"placement\": { \"alignment\": \"middle\" }", "$.rules[0].placement.alignment", "'start', 'center', 'end'")]
    [InlineData("\"inputMode\": \"clickThrough\"", "$.rules[0].inputMode", "'passThrough', 'interactive'")]
    [InlineData("\"zOrder\": \"bottom\"", "$.rules[0].zOrder", "'aboveTarget', 'topmost'")]
    [InlineData("\"focusPolicy\": 1", "$.rules[0].focusPolicy", "not a valid 'focusPolicy'")]
    [InlineData("\"visibility\": \"always\"", "$.rules[0].visibility", "'whenTargetVisible', 'whenTargetForeground'")]
    [InlineData("\"enabled\": \"yes\"", "$.rules[0].enabled", "true or false")]
    [InlineData("\"priority\": 2.5", "$.rules[0].priority", "must be an integer")]
    [InlineData("\"name\": 5", "$.rules[0].name", "must be a string")]
    public void InvalidRuleFields(string extra, string path, string message) =>
        AssertError(Doc(Rule(extra: extra)), path, message);

    [Fact]
    public void TooDeepFallbackNesting_ReportedAtTheOffendingLevel()
    {
        // 10 nested fallback chains; the default limit is 8 levels.
        string locator = Loc;
        for (int i = 0; i < 10; i++) locator = $$"""{ "type": "fallback", "locators": [ {{locator}} ] }""";

        var expectedPath = "$.rules[0].locator" + string.Concat(Enumerable.Repeat(".locators[0]", 8));
        AssertError(Doc(Rule(locator: locator)), expectedPath, "nested more than 8 levels");
    }

    [Fact]
    public void TooDeepAncestorNesting_IsRejected()
    {
        string ancestor = """{ "automationId": "leaf" }""";
        for (int i = 0; i < 9; i++) ancestor = $$"""{ "automationId": "a{{i}}", "ancestor": {{ancestor}} }""";
        var locator = $$"""{ "type": "uiAutomation", "automationId": "x", "ancestor": {{ancestor}} }""";

        var error = SingleError(ProfileSerializer.Load(Doc(Rule(locator: locator))));
        Assert.Equal("$.rules[0].locator" + string.Concat(Enumerable.Repeat(".ancestor", 8)), error.Path);
    }

    [Fact]
    public void ExcessiveJsonDepth_IsMalformedNotStackOverflow()
    {
        var json = Doc(Rule(extra: "\"name\": " + new string('[', 5000) + new string(']', 5000)));
        var error = SingleError(ProfileSerializer.Load(json));
        Assert.StartsWith("Malformed JSON", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TooManyRules_RejectsDocument()
    {
        var rules = Enumerable.Range(0, 1001).Select(i => Rule("r" + i)).ToArray();
        var result = ProfileSerializer.Load(Doc(rules));
        var error = SingleError(result);
        Assert.Equal("$.rules", error.Path);
        Assert.Contains("1001 rules; at most 1000", error.Message, StringComparison.Ordinal);
        Assert.Null(result.Profile);
        Assert.True(ProfileSerializer.Load(Doc(rules[..1000])).Succeeded);
    }

    [Fact]
    public void LimitsAreConfigurable()
    {
        var result = ProfileSerializer.Load(Doc(Rule("a"), Rule("b")), new ProfileLoadOptions { MaxRules = 1 });
        Assert.Equal("$.rules", SingleError(result).Path);
        Assert.Throws<ArgumentException>(() => ProfileSerializer.Load("{}", new ProfileLoadOptions { MaxRules = 0 }));
    }

    [Fact]
    public async Task OversizeDocument_RejectedBeforeParsing()
    {
        var padding = new string(' ', 1024 * 1024);
        var json = Doc(Rule()) + padding;

        var error = SingleError(ProfileSerializer.Load(json));
        Assert.Equal("$", error.Path);
        Assert.Contains("at most 1048576 bytes", error.Message, StringComparison.Ordinal);

        // Multi-byte characters count as UTF-8 bytes, not UTF-16 chars.
        var wide = Doc(Rule(extra: $"\"name\": \"{new string('€', 400_000)}\""));
        Assert.True(wide.Length < 1024 * 1024);
        Assert.Contains("bytes", SingleError(ProfileSerializer.Load(wide)).Message, StringComparison.Ordinal);

        // An endless stream is cut off after the limit instead of being buffered.
        var endless = new EndlessStream();
        var streamError = SingleError(await ProfileSerializer.LoadAsync(endless));
        Assert.Contains("more than 1048576 bytes", streamError.Message, StringComparison.Ordinal);
        Assert.True(endless.BytesRead <= 1024 * 1024 + 64 * 1024);
    }

    [Theory]
    [InlineData("{ \"schemaVersion\": 1, \"name\": \"x\",\n  \"rules\": [\n    { \"id\": \"a\" \"locator\": {} }\n  ]\n}", 3)]
    [InlineData("{ \"schemaVersion\": 1,\n\n\n  \"name\": 'single quotes' }", 4)]
    [InlineData("{ \"schemaVersion\": 1", 1)]
    [InlineData("", 1)]
    public void MalformedJson_ReportsLineAndPosition(string json, long line)
    {
        var result = ProfileSerializer.Load(json);
        var error = SingleError(result);
        Assert.StartsWith("Malformed JSON", error.Message, StringComparison.Ordinal);
        Assert.Equal(line, error.LineNumber);
        Assert.NotNull(error.BytePositionInLine);
        Assert.Contains($"(line {line}, position", error.ToString(), StringComparison.Ordinal);
        Assert.Null(result.Profile);
    }

    [Theory]
    [InlineData("[]", "$", "must be a JSON object")]
    [InlineData("""{ "schemaVersion": 1, "name": "x", "rules": [] }""", "$.application", "'application' is required")]
    [InlineData("""{ "schemaVersion": 1, "name": "x", "application": {}, "rules": [] }""", "$.application", "needs at least one of")]
    [InlineData("""{ "schemaVersion": 1, "name": "x", "application": { "titlePattern": "([" }, "rules": [] }""", "$.application.titlePattern", "Invalid regular expression")]
    [InlineData("""{ "schemaVersion": 1, "application": { "processName": "a" }, "rules": [] }""", "$.name", "'name' is required")]
    [InlineData("""{ "schemaVersion": 1, "name": "x", "application": { "processName": "a" } }""", "$.rules", "'rules' is required")]
    [InlineData("""{ "schemaVersion": 1, "name": "x", "application": { "processName": "a" }, "rules": {} }""", "$.rules", "must be an array")]
    [InlineData("""{ "schemaVersion": 1, "name": "x", "name": "y", "application": { "processName": "a" }, "rules": [] }""", "$.name", "appears more than once")]
    public void DocumentLevelErrors_ProduceNoProfile(string json, string path, string message)
    {
        AssertError(json, path, message);
        Assert.Null(ProfileSerializer.Load(json).Profile);
    }

    [Fact]
    public void InvalidRules_DoNotAffectValidOnes()
    {
        var result = ProfileSerializer.Load(Doc(Rule("ok1"), Rule("bad", locator: """{ "type": "nope" }"""), "42", Rule("ok2")));

        Assert.False(result.Succeeded);
        Assert.Equal(["ok1", "ok2"], result.Profile!.Rules.Select(r => r.Id));
        Assert.Equal(["$.rules[1].locator.type", "$.rules[2]"], result.Errors.Select(e => e.Path));
        Assert.Throws<ProfileException>(() => result.EnsureSuccess());
    }

    [Fact]
    public void UnknownProperties_AreWarnings()
    {
        var result = ProfileSerializer.Load(Doc(Rule(extra: "\"Enabled\": false, \"$type\": \"x\", \"weird key\": 1")));

        Assert.True(result.Succeeded);
        Assert.Equal(["$.rules[0].Enabled", "$.rules[0].$type", "$.rules[0]['weird key']"], result.Diagnostics.Select(d => d.Path));
        Assert.All(result.Diagnostics, d => Assert.Equal(ProfileDiagnosticSeverity.Warning, d.Severity));
        Assert.True(result.Profile!.Rules[0].Enabled); // property names are case-sensitive
    }

    [Fact]
    public void StringAndRegexLengthLimits()
    {
        var longName = new string('x', 5000);
        AssertError(Doc(Rule(extra: $"\"name\": \"{longName}\"")), "$.rules[0].name", "at most 4096");
        var longRegex = new string('a', 2000);
        AssertError(Doc(Rule(locator: $$"""{ "type": "text", "text": "{{longRegex}}", "match": "regex" }""")), "$.rules[0].locator.text", "at most 1024");
    }

    [Fact]
    public void ProfileException_ListsErrors()
    {
        var ex = Assert.Throws<ProfileException>(() => ProfileSerializer.Load(Doc(Rule(locator: "{}"))).EnsureSuccess());
        Assert.Equal(WindowVeilErrorCode.InvalidRule, ex.Code);
        Assert.Contains("$.rules[0].locator.type", ex.Message, StringComparison.Ordinal);
        Assert.Single(ex.Diagnostics);
    }

    private sealed class EndlessStream : Stream
    {
        public long BytesRead { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => BytesRead; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            buffer.AsSpan(offset, count).Fill((byte)' ');
            BytesRead += count;
            return count;
        }
    }
}
