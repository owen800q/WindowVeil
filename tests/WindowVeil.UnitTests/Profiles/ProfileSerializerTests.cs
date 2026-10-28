using System.Text;
using WindowVeil.Appearance;
using WindowVeil.Geometry;
using WindowVeil.Imaging;
using WindowVeil.Locators;
using WindowVeil.Platform;
using WindowVeil.Profiles;
using WindowVeil.Rendering;
using WindowVeil.Text;
using static WindowVeil.UnitTests.Profiles.ProfileTestData;

namespace WindowVeil.UnitTests.Profiles;

public class ProfileLoadTests
{
    [Fact]
    public void Load_SpecificationExample()
    {
        var result = ProfileSerializer.Load(Example);

        Assert.True(result.Succeeded, string.Join("\n", result.Diagnostics));
        Assert.Empty(result.Diagnostics);
        var profile = result.Profile!;
        Assert.Equal("RetroCap Startup Mask", profile.Name);
        Assert.Equal(1, profile.SchemaVersion);
        Assert.Equal("RetroCap", profile.Application.ProcessName);
        Assert.Equal(WindowSelector.ByProcessName("RetroCap"), profile.Selector);

        var rule = Assert.Single(profile.Rules);
        Assert.Equal("hide-startup-checkbox", rule.Id);
        var chain = Assert.IsType<FallbackLocator>(rule.Target);
        Assert.Equal(ElementLocator.ByAutomationId("StartupCheck"), chain.Locators[0]);
        Assert.Equal(ElementLocator.ByName("Start automatically when I sign in to Windows"), chain.Locators[1]);
        Assert.Equal(MaskAppearance.Solid("#202020"), rule.Appearance);
        Assert.Equal(OverlayPlacement.CoverTarget, rule.Placement);
        Assert.True(rule.FollowTarget);
        Assert.True(rule.Enabled);
        Assert.Equal(MaskRule.Create(ElementLocator.ByAutomationId("StartupCheck").Or(ElementLocator.ByName("Start automatically when I sign in to Windows")),
            MaskAppearance.Solid("#202020"), id: "hide-startup-checkbox"), rule);
    }

    [Fact]
    public void Load_AllVariants_MapsEveryField()
    {
        var result = ProfileSerializer.Load(AllVariants);
        Assert.True(result.Succeeded, string.Join("\n", result.Diagnostics));
        Assert.Empty(result.Diagnostics);
        var p = result.Profile!;
        var r = p.Rules.ToDictionary(x => x.Id);

        Assert.Equal(new ProfileApplication
        {
            ProcessName = "RetroCap", TitleContains = "Settings", TitlePattern = "^RetroCap( - .*)?$", ClassName = "RetroCapMain", RequireVisible = false, RequireTitle = false,
        }, p.Application);
        Assert.Equal(19, p.Rules.Count);

        Assert.Equal(ElementLocator.Rectangle(-1900, -20, 300, 40), r["rect"].Target);
        Assert.Equal(("Absolute", "geo"), (r["rect"].Name, r["rect"].Group));
        Assert.Equal(ElementLocator.WindowRelative(10, 20, 30, 40, WindowAnchor.Client), r["rel-client"].Target);
        Assert.Equal(new MaskAppearance(OverlayColor.FromRgb(255, 0, 0), 0.5), r["rel-client"].Appearance);
        Assert.Equal(ElementLocator.WindowRelative(-5, 0, 1, 1, WindowAnchor.Window), r["rel-window"].Target);
        Assert.Equal(ElementLocator.Normalized(0.1, 0.2, 0.5, 0.25, WindowAnchor.Window), r["norm"].Target);
        Assert.Equal(new HighlightAppearance(OverlayColor.Parse("#FFCC00"), 2.5, 0.8), r["norm"].Appearance);

        var uia = Assert.IsType<UiAutomationLocator>(r["uia-full"].Target);
        Assert.Equal(new UiAutomationLocator
        {
            AutomationId = "Save", Name = new TextQuery("^Save( all)?$", TextMatch.Regex, true), ControlType = "Button", ClassName = "WpfButton",
            Index = 2, MaxDepth = 12, MaxNodes = 900,
            Ancestor = new UiAutomationLocator { ControlType = "Pane", Name = new TextQuery("Toolbar", TextMatch.StartsWith, false), Ancestor = ElementLocator.ByAutomationId("Root") },
        }, uia);
        Assert.Equal(SpotlightAppearance.Create(0.6), r["uia-full"].Appearance);

        var fb = Assert.IsType<FallbackLocator>(r["uia-fallback"].Target);
        Assert.Equal(ElementLocator.ByAutomationId("StartupCheck") with { MaxDepth = 6 }, fb.Locators[0]);
        Assert.Equal(new UiAutomationLocator { Name = new TextQuery("start automatically", TextMatch.Contains, true), MaxDepth = 6 }, fb.Locators[1]);
        Assert.Equal(new SpotlightAppearance(OverlayColor.Parse("#101010"), 0.4, SpotlightScope.Monitor), r["uia-fallback"].Appearance);

        Assert.Equal(new TextLocator(new TextQuery("Password", TextMatch.Exact, false)) { AllMatches = false, Scope = ElementLocator.ByClassName("Edit") }, r["text"].Target);
        Assert.Equal(new RegisteredAppearance("badge") { Parameters = new Dictionary<string, string> { ["text"] = "Secret", ["tone"] = "warning" } }, r["text"].Appearance);

        Assert.Equal(new OcrTextLocator(new TextQuery("Start automatically", TextMatch.StartsWith, false))
        {
            MinConfidence = 0.75, SearchRegion = new PixelRect(0, 40, 600, 200), Expand = new Insets(20, 2, 2, 2),
            MustNotOverlap = [TextQuery.Containing("Minimize"), TextQuery.Matching("^Close$")], MinInterval = TimeSpan.FromMilliseconds(250), AllMatches = false,
        }, r["ocr"].Target);
        Assert.Equal(Insets.Uniform(4), ((OcrTextLocator)r["ocr-uniform"].Target).Expand);
        Assert.Equal(0.6, ((OcrTextLocator)r["ocr-uniform"].Target).MinConfidence);

        var chain = Assert.IsType<FallbackLocator>(r["chain"].Target);
        Assert.False(chain.ContinueOnError);
        Assert.Equal(["uiAutomation", "fallback", "windowRelative"], chain.Locators.Select(l => l.Kind));

        Assert.Equal(OverlayPlacement.Right(), r["p-right"].Placement);
        Assert.Equal(OverlayPlacement.Left(), r["p-left"].Placement);
        Assert.Equal(OverlayPlacement.Above(), r["p-above"].Placement);
        Assert.Equal(OverlayPlacement.Below(), r["p-below"].Placement);
        Assert.Equal(OverlayPlacement.Centered(), r["p-center"].Placement);
        Assert.Equal(OverlayPlacement.Cover(new Insets(20, 2, 2, 2)) with { OffsetX = -1, OffsetY = 1.5, MultiRect = MultiRectMode.Union }, r["p-cover-object"].Placement);
        Assert.Equal(OverlayPlacement.Right(offsetX: 12) with { Width = 80, Height = 24, Alignment = PlacementAlignment.Start, Padding = Insets.Uniform(3) }, r["p-right-object"].Placement);
        Assert.Equal(OverlayPlacement.Below() with { Alignment = PlacementAlignment.End, MultiRect = MultiRectMode.Each }, r["p-below-object"].Placement);

        var b = r["behavior"];
        Assert.Equal(("Interactive blocker", "privacy"), (b.Name, b.Group));
        Assert.Equal(OverlayInputMode.Interactive, b.InputMode);
        Assert.Equal(OverlayFocusPolicy.ActivateOnClick, b.FocusPolicy);
        Assert.Equal(OverlayZOrder.Topmost, b.ZOrder);
        Assert.False(b.FollowTarget);
        Assert.False(b.Enabled);
        Assert.Equal(-3, b.Priority);
        Assert.Equal(OverlayVisibility.WhenTargetForeground, b.Visibility);
    }

    [Fact]
    public void Load_ToleratesCommentsTrailingCommasBomAndExeSuffix()
    {
        const string json = """
            // RetroCap profile
            { "schemaVersion": 1, "name": "x", "application": { "processName": "RetroCap.exe" },
              "rules": [ { "id": "a", "locator": { "type": "rectangle", "x": 0, "y": 0, "width": 1, "height": 1, }, "appearance": { "type": "solid", "color": "#000" } }, ], }
            """;
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(json)).ToArray();

        var result = ProfileSerializer.Load(bytes);

        Assert.True(result.Succeeded, string.Join("\n", result.Diagnostics));
        Assert.Equal("RetroCap", result.Profile!.Application.ProcessName);
    }

    [Fact]
    public void Load_EmptyRules_IsValid()
    {
        var result = ProfileSerializer.Load("""{ "schemaVersion": 1, "name": "x", "application": { "titleContains": "Notepad" }, "rules": [] }""");
        Assert.True(result.Succeeded);
        Assert.Empty(result.Profile!.Rules);
    }

    [Fact]
    public async Task LoadAsync_And_LoadFile_MatchLoad()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(Example));
        var fromStream = await ProfileSerializer.LoadAsync(stream);
        var path = Path.Combine(Path.GetTempPath(), $"wv-{Guid.NewGuid():n}.json");
        try
        {
            File.WriteAllText(path, Example);
            var fromFile = ProfileSerializer.LoadFile(path);
            Assert.Equal(ProfileSerializer.Load(Example).Profile, fromStream.Profile);
            Assert.Equal(fromStream.Profile, fromFile.Profile);
            Assert.Equal(path, fromFile.SourcePath);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void LoadFile_Missing_IsDiagnosticNotException()
    {
        var result = ProfileSerializer.LoadFile(Path.Combine(Path.GetTempPath(), "does-not-exist-" + Guid.NewGuid() + ".json"));
        Assert.False(result.Succeeded);
        Assert.Contains("does not exist", Assert.Single(result.Diagnostics).Message, StringComparison.Ordinal);
        Assert.Throws<ProfileException>(() => result.EnsureSuccess());
    }
}

public class ProfileRoundTripTests
{
    [Theory]
    [InlineData(nameof(ProfileTestData.Example))]
    [InlineData(nameof(ProfileTestData.AllVariants))]
    public void LoadSaveLoad_IsLosslessAndStable(string name)
    {
        var json = name == nameof(ProfileTestData.Example) ? Example : AllVariants;
        var first = ProfileSerializer.Load(json).EnsureSuccess();

        var saved = ProfileSerializer.ToJson(first);
        var second = ProfileSerializer.Load(saved).EnsureSuccess();

        for (int i = 0; i < first.Rules.Count; i++) Assert.Equal(first.Rules[i], second.Rules[i]);
        Assert.Equal(first, second);
        Assert.Equal(saved, ProfileSerializer.ToJson(second));
    }

    public static TheoryData<string> CodeRuleIds() => new(CodeRules().Select(r => r.Id));

    [Theory]
    [MemberData(nameof(CodeRuleIds))]
    public void CodeRule_ExportSaveLoad_ReproducesRule(string id)
    {
        var rule = CodeRules().Single(r => r.Id == id);
        var profile = ProfileExporter.FromRules("Code", WindowSelector.ByProcessName("RetroCap"), [rule]);

        var json = ProfileSerializer.ToJson(profile);
        var loaded = ProfileSerializer.Load(json);

        Assert.True(loaded.Succeeded, json + "\n" + string.Join("\n", loaded.Diagnostics));
        Assert.Equal(rule, Assert.Single(loaded.Profile!.Rules));
    }

    [Fact]
    public void Save_UsesShortFormsAndCamelCase()
    {
        var json = ProfileSerializer.ToJson(ProfileExporter.FromRules("Code", WindowSelector.ByProcessName("RetroCap"), CodeRules()));

        Assert.Contains("\"fallbackName\": \"Start automatically when I sign in to Windows\"", json, StringComparison.Ordinal);
        Assert.Contains("\"placement\": \"cover\"", json, StringComparison.Ordinal);
        Assert.Contains("\"inputMode\": \"interactive\"", json, StringComparison.Ordinal);
        Assert.Contains("\"type\": \"fallback\"", json, StringComparison.Ordinal); // mismatched chains stay explicit
        Assert.Contains("Hi \\\"there\\\" ✓", json, StringComparison.Ordinal); // readable non-ASCII, escaped quotes
        Assert.DoesNotContain("\"Target\"", json, StringComparison.Ordinal);   // no .NET member names
        Assert.DoesNotContain("$type", json, StringComparison.Ordinal);
        Assert.StartsWith("{\n  \"schemaVersion\": 1,", json.ReplaceLineEndings("\n"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SaveFileAndStream_LoadBack()
    {
        var profile = ProfileSerializer.Load(Example).EnsureSuccess();
        var path = Path.Combine(Path.GetTempPath(), $"wv-{Guid.NewGuid():n}.json");
        try
        {
            ProfileSerializer.Save(profile, path);
            Assert.Equal(profile, ProfileSerializer.LoadFile(path).EnsureSuccess());
            Assert.False(File.ReadAllBytes(path).AsSpan().StartsWith(Encoding.UTF8.GetPreamble()));
        }
        finally { File.Delete(path); }

        using var ms = new MemoryStream();
        await ProfileSerializer.SaveAsync(profile, ms);
        ms.Position = 0;
        Assert.Equal(profile, (await ProfileSerializer.LoadAsync(ms)).EnsureSuccess());
    }
}
