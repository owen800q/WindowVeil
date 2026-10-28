using System.Text.Json.Nodes;
using WindowVeil.Appearance;
using WindowVeil.Locators;
using WindowVeil.Platform;
using WindowVeil.Profiles;
using WindowVeil.Text;
using WindowVeil.UnitTests.Fakes;
using static WindowVeil.UnitTests.Profiles.ProfileTestData;

namespace WindowVeil.UnitTests.Profiles;

public class ProfileBindingTests
{
    private const string Doc = """
        { "schemaVersion": 1, "name": "Badges", "application": { "processName": "RetroCap" }, "rules": [
          { "id": "broken", "locator": { "type": "nope" }, "appearance": { "type": "solid", "color": "#000" } },
          { "id": "badge", "locator": { "type": "rectangle", "x": 0, "y": 0, "width": 5, "height": 5 }, "appearance": { "type": "registered", "factory": "Badge", "parameters": { "text": "Beta" } } },
          { "id": "ghost", "locator": { "type": "rectangle", "x": 0, "y": 0, "width": 5, "height": 5 }, "appearance": { "type": "registered", "factory": "ghost" } },
          { "id": "mask", "locator": { "type": "rectangle", "x": 0, "y": 0, "width": 5, "height": 5 }, "appearance": { "type": "solid", "color": "#000" } }
        ] }
        """;

    [Fact]
    public void ToRules_WithRegistry_ResolvesKnownIdsAndReportsUnknownAtSourcePath()
    {
        var profile = ProfileSerializer.Load(Doc).Profile!;
        var registry = new AppearanceRegistry().Register("badge", p => new FakeContentAppearance(p["text"]));

        var binding = profile.ToRules(registry);

        Assert.False(binding.Succeeded);
        Assert.Equal(["badge", "mask"], binding.Rules.Select(r => r.Id));
        var error = Assert.Single(binding.Diagnostics);
        Assert.Equal("$.rules[2].appearance.factory", error.Path); // index in the source document, not in profile.Rules
        Assert.Contains("No appearance factory is registered with id 'ghost'. Registered ids: badge.", error.Message, StringComparison.Ordinal);
        // The rule keeps the reference; sessions resolve it through the engine registry.
        Assert.Equal(new RegisteredAppearance("Badge") { Parameters = new Dictionary<string, string> { ["text"] = "Beta" } }, binding.Rules[0].Appearance);
    }

    [Fact]
    public void ToRules_WithoutRegistry_SkipsFactoryCheck()
    {
        var binding = ProfileSerializer.Load(Doc).Profile!.ToRules();
        Assert.True(binding.Succeeded);
        Assert.Equal(3, binding.Rules.Count);
    }

    [Fact]
    public void ToRules_EmptyRegistry_ExplainsNothingIsRegistered()
    {
        var binding = ProfileSerializer.Load(Doc).Profile!.ToRules(new AppearanceRegistry());
        Assert.All(binding.Diagnostics, d => Assert.Contains("has not registered any appearance factories", d.Message, StringComparison.Ordinal));
        Assert.Equal(2, binding.Diagnostics.Count);
    }

    [Fact]
    public void ToRules_ProfileBuiltInCode_ValidatesRulesAndDuplicates()
    {
        var profile = new OverlayProfile
        {
            Name = "Code",
            Application = new ProfileApplication { ProcessName = "x" },
            Rules = [Rule("a", ElementLocator.ByAutomationId("A")), Rule("a", ElementLocator.ByAutomationId("B")), Rule("c", new UiAutomationLocator())],
        };

        var binding = profile.ToRules();

        Assert.Equal(["$.rules[1].id", "$.rules[2]"], binding.Diagnostics.Select(d => d.Path));
        Assert.Single(binding.Rules);
    }
}

public class ProfileExportTests
{
    [Fact]
    public void FromRules_CustomAppearance_RefusedWithGuidanceToRegister()
    {
        var rules = new[] { Rule("ok", ElementLocator.ByAutomationId("A")), Rule("wpf", ElementLocator.ByAutomationId("B"), new FakeContentAppearance("<Button/>")) };

        var result = ProfileExporter.TryFromRules("P", WindowSelector.ByProcessName("RetroCap"), rules);

        Assert.False(result.Succeeded);
        var error = Assert.Single(result.Diagnostics);
        Assert.Equal("$.rules[1].appearance", error.Path);
        Assert.Contains("'fakeContent' (FakeContentAppearance) cannot be stored in a profile", error.Message, StringComparison.Ordinal);
        Assert.Contains("RegisteredAppearance", error.Message, StringComparison.Ordinal);
        var ex = Assert.Throws<ProfileException>(() => ProfileExporter.FromRules("P", WindowSelector.ByProcessName("RetroCap"), rules));
        Assert.Contains("AppearanceRegistry.Register", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ToJson_ProfileWithCustomAppearance_ThrowsAndNeverSerializesObjects()
    {
        var profile = new OverlayProfile
        {
            Name = "P",
            Application = new ProfileApplication { ProcessName = "RetroCap" },
            Rules = [Rule("wpf", ElementLocator.ByAutomationId("B"), new FakeContentAppearance("secret"))],
        };

        var save = ProfileSerializer.TryToJson(profile);

        Assert.Null(save.Json);
        Assert.Throws<ProfileException>(() => ProfileSerializer.ToJson(profile));
    }

    [Fact]
    public void FromRules_CustomLocatorKind_Refused()
    {
        var result = ProfileExporter.TryFromRules("P", WindowSelector.ByProcessName("RetroCap"), [Rule("x", ElementLocator.FirstOf(ElementLocator.ByAutomationId("a"), new FakeLocator("k")))]);
        Assert.Equal("$.rules[0].locator.locators[1]", Assert.Single(result.Diagnostics).Path);
    }

    [Theory]
    [InlineData("handle")]
    [InlineData("pid")]
    [InlineData("predicate")]
    [InlineData("empty")]
    public void FromRules_UnstableSelector_Refused(string kind)
    {
        var selector = kind switch
        {
            "handle" => WindowSelector.ByHandle(0x1234),
            "pid" => WindowSelector.ByProcessId(42),
            "predicate" => WindowSelector.ByProcessName("a") with { Predicate = _ => true },
            _ => new WindowSelector(),
        };

        var result = ProfileExporter.TryFromRules("P", selector, []);

        Assert.Equal("$.application", Assert.Single(result.Diagnostics).Path);
    }

    [Fact]
    public void FromRules_NonNormalizedQueryAndInvalidRule_Refused()
    {
        var rules = new[]
        {
            Rule("ws", ElementLocator.ByText("a  b") with { Query = TextQuery.Containing("a  b") with { NormalizeWhitespace = false } }),
            Rule("bad", new UiAutomationLocator()),
            Rule("ws", ElementLocator.ByAutomationId("dupe")),
        };

        var result = ProfileExporter.TryFromRules("P", WindowSelector.ByTitle("x"), rules);

        Assert.Equal(["$.rules[0].locator", "$.rules[1]", "$.rules[2].id"], result.Diagnostics.Select(d => d.Path));
    }
}

public class ProfileMigrationTests
{
    private const string V0 = """
        {
          "process": "RetroCap",
          "rules": [
            { "id": "startup", "automationId": "StartupCheck", "name": "Start automatically when I sign in to Windows", "mask": { "color": "#202020", "opacity": 1.0 } },
            { "automationId": "Banner", "rect": { "x": 10, "y": 20, "width": 300, "height": 40 }, "mask": { "color": "#FFFFFF", "opacity": 0.5 } },
            { "id": "by-name", "name": "Sign in", "enabled": false },
            { "id": "rect-only", "rect": { "x": 1, "y": 2, "width": 3, "height": 4 } }
          ]
        }
        """;

    [Fact]
    public void LoadV0_MigratesToV1WithInfoDiagnostic()
    {
        var result = ProfileSerializer.Load(V0);

        Assert.True(result.Succeeded, string.Join("\n", result.Diagnostics));
        var info = result.Diagnostics.Single(d => d.Path == "$.schemaVersion");
        Assert.Equal(ProfileDiagnosticSeverity.Info, info.Severity);
        Assert.Contains("migrated from schema version 0 to 1", info.Message, StringComparison.Ordinal);
        Assert.Contains(result.Diagnostics, d => d.Path == "$.rules[1]" && d.Severity == ProfileDiagnosticSeverity.Info && d.Message.Contains("rule-2", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity != ProfileDiagnosticSeverity.Info);

        var p = result.Profile!;
        Assert.Equal("RetroCap", p.Name);
        Assert.Equal(1, p.SchemaVersion);
        Assert.Equal(new ProfileApplication { ProcessName = "RetroCap" }, p.Application);
        // Rule 0 is exactly the v1 example.
        Assert.Equal(ProfileSerializer.Load(Example).Profile!.Rules[0] with { Id = "startup" }, p.Rules[0]);

        var banner = p.Rules[1];
        Assert.Equal("rule-2", banner.Id);
        Assert.Equal(ElementLocator.ByAutomationId("Banner").Or(ElementLocator.WindowRelative(10, 20, 300, 40, WindowAnchor.Client)), banner.Target);
        Assert.Equal(MaskAppearance.Transparent("#FFFFFF", 0.5), banner.Appearance);

        Assert.Equal(ElementLocator.ByName("Sign in"), p.Rules[2].Target);
        Assert.Null(p.Rules[2].Name); // v0 'name' was the element name, not a display name
        Assert.False(p.Rules[2].Enabled);
        Assert.Equal(MaskAppearance.Solid("#000000"), p.Rules[2].Appearance);

        Assert.Equal(ElementLocator.WindowRelative(1, 2, 3, 4), p.Rules[3].Target);
    }

    [Fact]
    public void LoadV0_ExplicitVersionZero_AndSaveUpgradesFile()
    {
        var json = V0.Replace("\"process\"", "\"schemaVersion\": 0, \"process\"", StringComparison.Ordinal);
        var profile = ProfileSerializer.Load(json).EnsureSuccess();

        var saved = ProfileSerializer.ToJson(profile);

        Assert.Contains("\"schemaVersion\": 1", saved, StringComparison.Ordinal);
        var reloaded = ProfileSerializer.Load(saved);
        Assert.Empty(reloaded.Diagnostics);
        Assert.Equal(profile, reloaded.Profile);
    }

    [Fact]
    public void LoadV0_InvalidDraftValues_ReportedAtDraftPaths()
    {
        var result = ProfileSerializer.Load("""{ "process": 7, "rules": [ { "id": "a", "rect": "0,0,1,1" } ] }""");
        Assert.Null(result.Profile);
        Assert.Equal(["$.process", "$.rules[0].rect"], result.Errors.Select(e => e.Path));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(99)]
    public void NewerSchemaVersion_IsRejected(int version)
    {
        var json = Example.Replace("\"schemaVersion\": 1", $"\"schemaVersion\": {version}", StringComparison.Ordinal);

        var result = ProfileSerializer.Load(json);

        Assert.Null(result.Profile);
        var error = Assert.Single(result.Diagnostics);
        Assert.Equal("$.schemaVersion", error.Path);
        Assert.Contains("produced by a newer WindowVeil", error.Message, StringComparison.Ordinal);
        Assert.Contains($"schema version {version}", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\"1\"")]
    [InlineData("1.5")]
    [InlineData("-1")]
    public void InvalidSchemaVersion_IsRejected(string version)
    {
        var json = Example.Replace("\"schemaVersion\": 1", $"\"schemaVersion\": {version}", StringComparison.Ordinal);
        var error = Assert.Single(ProfileSerializer.Load(json).Diagnostics);
        Assert.Equal("$.schemaVersion", error.Path);
        Assert.Contains("non-negative integer", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Migrator_ValidatesChainAndReportsGaps()
    {
        Assert.Throws<ArgumentException>(() => new ProfileMigrator([new V0ToV1Migration(), new V0ToV1Migration()]));
        Assert.Throws<ArgumentException>(() => new ProfileMigrator([new TestMigration(1, 1)]));
        Assert.Throws<ArgumentException>(() => new ProfileMigrator([new TestMigration(0, 5)]));

        var noSteps = new ProfileMigrator([]);
        var result = ProfileSerializer.Load(V0, new ProfileLoadOptions { Migrator = noSteps });
        Assert.Contains("No migration is available from schema version 0", Assert.Single(result.Diagnostics).Message, StringComparison.Ordinal);

        Assert.Equal([0], ProfileMigrator.Default.Migrations.Select(m => m.FromVersion));
    }

    [Fact]
    public void Migrator_CustomMigrationErrorsStopLoading()
    {
        var migrator = new ProfileMigrator([new TestMigration(0, 1, (doc, d) => d.Add(new ProfileDiagnostic(ProfileDiagnosticSeverity.Error, "$.legacy", "unsupported")))]);
        var result = ProfileSerializer.Load(V0, new ProfileLoadOptions { Migrator = migrator });
        Assert.Null(result.Profile);
        Assert.Equal("$.legacy", Assert.Single(result.Diagnostics).Path);

        var throwing = new ProfileMigrator([new TestMigration(0, 1, (_, _) => throw new InvalidOperationException("bad draft"))]);
        Assert.Contains("bad draft", Assert.Single(ProfileSerializer.Load(V0, new ProfileLoadOptions { Migrator = throwing }).Diagnostics).Message, StringComparison.Ordinal);
    }

    private sealed class TestMigration(int from, int to, Action<JsonObject, ICollection<ProfileDiagnostic>>? body = null) : IProfileMigration
    {
        public int FromVersion => from;
        public int ToVersion => to;
        public void Migrate(JsonObject document, ICollection<ProfileDiagnostic> diagnostics) => body?.Invoke(document, diagnostics);
    }
}

public class ProfileSetTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wv-profiles-" + Guid.NewGuid().ToString("n"));

    public ProfileSetTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void LoadDirectory_LoadsEachJsonFileIndependently()
    {
        File.WriteAllText(Path.Combine(_dir, "b-notepad.json"), Example.Replace("RetroCap", "notepad", StringComparison.Ordinal));
        File.WriteAllText(Path.Combine(_dir, "a-retrocap.json"), Example);
        File.WriteAllText(Path.Combine(_dir, "c-broken-rule.json"), Example.Replace("\"solid\"", "\"wpf\"", StringComparison.Ordinal));
        File.WriteAllText(Path.Combine(_dir, "d-malformed.json"), "{ not json");
        File.WriteAllText(Path.Combine(_dir, "readme.txt"), "ignored");

        var set = ProfileSet.LoadDirectory(_dir);

        Assert.Equal(["a-retrocap.json", "b-notepad.json", "c-broken-rule.json", "d-malformed.json"], set.Files.Select(f => Path.GetFileName(f.Path)));
        Assert.Equal(["RetroCap", "notepad"], set.Profiles.Select(p => p.Application.ProcessName));
        Assert.True(set.HasErrors);
        var broken = set.Files[2].Result;
        Assert.NotNull(broken.Profile); // partially valid, but excluded from Profiles
        Assert.Equal("$.rules[0].appearance.type", Assert.Single(broken.Errors).Path);
        Assert.Equal(set.Files[2].Path, broken.SourcePath);
        Assert.NotNull(Assert.Single(set.Files[3].Result.Errors).LineNumber);
        Assert.Empty(set.Diagnostics);
    }

    [Fact]
    public void LoadDirectory_FileLimitAndMissingDirectory()
    {
        for (int i = 0; i < 3; i++) File.WriteAllText(Path.Combine(_dir, $"{i}.json"), Example);

        var set = ProfileSet.LoadDirectory(_dir, maxFiles: 2);

        Assert.Equal(2, set.Files.Count);
        Assert.Contains("only the first 2", Assert.Single(set.Diagnostics).Message, StringComparison.Ordinal);
        Assert.True(set.HasErrors);
        Assert.Throws<DirectoryNotFoundException>(() => ProfileSet.LoadDirectory(Path.Combine(_dir, "missing")));
    }

    [Fact]
    public void LoadDirectory_OversizeFileRejectedWithoutReading()
    {
        File.WriteAllText(Path.Combine(_dir, "big.json"), Example + new string(' ', 2000));
        var set = ProfileSet.LoadDirectory(_dir, new ProfileLoadOptions { MaxDocumentBytes = 1000 });
        Assert.Contains("at most 1000 bytes", Assert.Single(set.Files[0].Result.Diagnostics).Message, StringComparison.Ordinal);
    }
}

public class ProfileEngineTests
{
    private const string Profile = """
        { "schemaVersion": 1, "name": "RetroCap", "application": { "processName": "RetroCap" }, "rules": [
          { "id": "mask", "locator": { "type": "windowRelative", "x": 10, "y": 20, "width": 100, "height": 30 }, "appearance": { "type": "solid", "color": "#202020" } },
          { "id": "badge", "locator": { "type": "windowRelative", "x": 10, "y": 20, "width": 100, "height": 30 },
            "appearance": { "type": "registered", "factory": "badge", "parameters": { "text": "Beta" } }, "placement": { "mode": "right", "width": 40, "height": 30, "offsetX": 0 } }
        ] }
        """;

    private static EngineHarness Harness() =>
        new(configure: b => b.RegisterAppearance("badge", p => new FakeContentAppearance(p["text"])));

    [Fact]
    public async Task AttachProfileAsync_AddsRulesWithoutStarting_ThenRuns()
    {
        await using var h = Harness();
        var w = h.Windows.Add();
        var profile = ProfileSerializer.Load(Profile).EnsureSuccess();

        var session = await h.Engine.AttachProfileAsync(profile);

        Assert.Equal(SessionState.Created, session.State);
        Assert.Equal(["mask", "badge"], session.Rules.Select(r => r.Id));
        Assert.True(session.Options.AllowDeferredAttach);
        Assert.True(session.Options.AutoReconnect);
        Assert.Equal(profile.Selector, session.Selector);
        Assert.Empty(h.Renderer.Created);

        await session.StartAsync();

        var target = new Geometry.PixelRect(w.ClientBounds.X + 10, w.ClientBounds.Y + 20, 100, 30);
        Assert.Equal([target], session.GetRuleStatus("mask").OverlayBounds);
        Assert.Equal([new Geometry.PixelRect(target.Right, target.Y, 40, 30)], session.GetRuleStatus("badge").OverlayBounds);
        Assert.Equal(new FakeContentAppearance("Beta"), h.Renderer.LiveFor("badge")!.Request.Appearance);
    }

    [Fact]
    public async Task AttachProfileAsync_ApplicationNotRunning_AttachesDeferredAndConnectsLater()
    {
        await using var h = Harness();
        var options = EngineHarness.Quiet with { AllowDeferredAttach = false, AutoReconnect = false, ReconnectPollInterval = TimeSpan.FromMilliseconds(20) };

        var session = await h.Engine.AttachProfileAsync(ProfileSerializer.Load(Profile).EnsureSuccess(), options);
        await session.StartAsync();
        Assert.Equal(SessionState.WaitingForTarget, session.State);

        h.Windows.Add();

        await session.ForStateAsync("mask", RuleState.Active);
        Assert.Equal(TimeSpan.FromMilliseconds(20), session.Options.ReconnectPollInterval); // caller options kept
    }

    [Fact]
    public async Task AttachProfileAsync_UnregisteredFactory_ThrowsWithoutCreatingSession()
    {
        await using var h = new EngineHarness();
        h.Windows.Add();

        var ex = await Assert.ThrowsAsync<ProfileException>(() => h.Engine.AttachProfileAsync(ProfileSerializer.Load(Profile).EnsureSuccess()));

        Assert.Equal("$.rules[1].appearance.factory", Assert.Single(ex.Diagnostics).Path);
        Assert.Empty(h.Engine.Sessions);
    }

    [Fact]
    public async Task AttachProfileAsync_RuleTheEngineCannotHost_DisposesSession()
    {
        await using var h = Harness();
        h.Windows.Add();
        var profile = ProfileSerializer.Load(Example).EnsureSuccess(); // uiAutomation, but no UIA resolver is registered

        var ex = await Assert.ThrowsAsync<WindowVeilException>(() => h.Engine.AttachProfileAsync(profile));

        Assert.Equal(WindowVeilErrorCode.InvalidRule, ex.Code);
        Assert.Empty(h.Engine.Sessions);
    }

    [Fact]
    public async Task AttachProfilesAsync_AttachesEveryProfile()
    {
        await using var h = Harness();
        var a = ProfileSerializer.Load(Profile).EnsureSuccess();
        var b = a with { Name = "Notepad", Application = new ProfileApplication { ProcessName = "notepad" } };

        var sessions = await h.Engine.AttachProfilesAsync([a, b]);

        Assert.Equal(2, sessions.Count);
        Assert.All(sessions, s => Assert.Equal(SessionState.WaitingForTarget, s.State));
        Assert.Equal(2, h.Engine.Sessions.Count);
    }
}
