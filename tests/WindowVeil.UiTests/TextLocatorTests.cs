using WindowVeil.Locators;
using WindowVeil.Text;

namespace WindowVeil.UiTests;

[Collection(DemoTargetCollection.Name)]
[Trait("Category", "Desktop")]
public sealed class TextLocatorTests
{
    private readonly DemoTargetFixture _f;

    public TextLocatorTests(DemoTargetFixture fixture) => _f = fixture;

    [Fact]
    public async Task TextPattern_FindsEveryMatch_WithOneRectanglePerLine()
    {
        var notes = await _f.BoundsOfAsync("NotesBox");
        var locator = ElementLocator.ByText("secret token") with { Scope = ElementLocator.ByAutomationId("NotesBox") };

        var r = await _f.ResolveAsync(locator);

        Assert.True(r.IsResolved, r.Message);
        Assert.Contains("TextPattern", r.Message);
        // One rectangle for the single-line match plus two for the match that spans a line break.
        Assert.True(r.Bounds.Count >= 3, $"Expected at least 3 line rectangles, got {r.Bounds.Count}: {string.Join(", ", r.Bounds)}");
        Assert.True(r.Bounds.Select(b => b.Y).Distinct().Count() >= 3, "The matches should lie on three different lines.");
        foreach (var b in r.Bounds)
        {
            Assert.True(notes.Inflate(2).Contains(b), $"{b} should lie inside NotesBox {notes}");
            Assert.True(b.Area * 4 < notes.Area, $"{b} should be much smaller than NotesBox {notes}");
        }
    }

    [Fact]
    public async Task TextPattern_FirstMatchOnly()
    {
        var locator = ElementLocator.ByText("secret token") with { Scope = ElementLocator.ByAutomationId("NotesBox"), AllMatches = false };
        var r = await _f.ResolveAsync(locator);
        Assert.True(r.IsResolved, r.Message);
        Assert.Single(r.Bounds);
    }

    [Fact]
    public async Task TextPattern_RichTextBox()
    {
        var doc = await _f.BoundsOfAsync("DocumentBox");
        var r = await _f.ResolveAsync(ElementLocator.ByText("Confidential") with { Scope = ElementLocator.ByAutomationId("DocumentBox") });
        Assert.True(r.IsResolved, r.Message);
        var b = Assert.Single(r.Bounds);
        Assert.True(doc.Inflate(2).Contains(b), $"{b} should lie inside DocumentBox {doc}");
        Assert.True(b.Width * 2 < doc.Width, "Only the word should be located, not the whole document.");
    }

    [Fact]
    public async Task WholeWindow_TextBlockLabel()
    {
        var label = await _f.BoundsOfAsync("AccountBalanceLabel");
        var r = await _f.ResolveAsync(ElementLocator.ByText("Account Balance"));
        Assert.True(r.IsResolved, r.Message);
        var b = Assert.Single(r.Bounds);
        Assert.True(label.Inflate(2).Contains(b), $"{b} should lie inside the label {label}");
    }

    [Fact]
    public async Task NameFallback_ForElementWithoutTextPattern()
    {
        var button = await _f.BoundsOfAsync("IconButton");
        var r = await _f.ResolveAsync(ElementLocator.ByText("upload avatar"));
        Assert.True(r.IsResolved, r.Message);
        Assert.Contains("matched element Name (no TextPattern geometry)", r.Message);
        Assert.Equal(button, Assert.Single(r.Bounds));
        Assert.True(r.Confidence < 1);
    }

    [Fact]
    public async Task MissingText_IsNotFound()
    {
        var r = await _f.ResolveAsync(ElementLocator.ByText("this text does not exist anywhere"));
        Assert.Equal(LocatorStatus.NotFound, r.Status);
        Assert.Contains("searched", r.Message);
    }

    [Fact]
    public async Task OffscreenText_IsOffscreen()
    {
        await _f.ScrollAreaToAsync(0);
        var r = await Eventually.UntilAsync(
            () => _f.ResolveAsync(ElementLocator.ByText("Row 40", TextMatch.Exact) with { Scope = ElementLocator.ByAutomationId("ScrollArea") }),
            r => r.Status == LocatorStatus.Offscreen, TimeSpan.FromSeconds(5), "Row 40 text to be offscreen");
        Assert.Empty(r.Bounds);
    }

    [Fact]
    public async Task MissingScope_IsNotFound()
    {
        var r = await _f.ResolveAsync(ElementLocator.ByText("secret") with { Scope = ElementLocator.ByAutomationId("NoSuchBox") });
        Assert.Equal(LocatorStatus.NotFound, r.Status);
        Assert.Contains("scope", r.Message, StringComparison.OrdinalIgnoreCase);
    }
}
