using WindowVeil.Geometry;
using WindowVeil.Locators;
using WindowVeil.Ocr;
using WindowVeil.Text;
using Xunit;

namespace WindowVeil.OcrTests;

/// <summary><see cref="OcrLocatorResolver"/> with the real PaddleOCR engine on the fixtures (capture is faked).</summary>
public class OcrIntegrationTests(PaddleEngineFixture fixture) : IClassFixture<PaddleEngineFixture>
{
    private static LocatorContext Context(Platform.TargetWindowInfo window, FakeCapture capture) =>
        new(window, ResolveReason.Initial) { Capture = capture, Time = new ManualTime() };

    private const string FirstLabel = "Start automatically when I sign in to Windows";

    [SkippableFact]
    public async Task CheckboxLabel_ExpandedOverTheCheckbox_Resolves_ButCoveringTheNextLabelIsRejected()
    {
        TestEnvironment.RequireModels();
        var image = TestEnvironment.LoadFixture("en_settings");
        var capture = new FakeCapture { ImageFactory = _ => image };
        var window = Windows.At(-700, 250, 520, 100); // client area on a monitor left of the primary one
        var resolver = new OcrLocatorResolver(fixture.Engine);
        var neighbor = new TextQuery("Start hidden in the notification area", TextMatch.Exact);

        // The checkbox square is 16 px wide (x = 16..31) left of the label; expand by its width plus a margin.
        var locator = ElementLocator.ByOcrText(FirstLabel, TextMatch.Exact).Expanded(new Insets(16, 2, 2, 2), neighbor);
        var ok = await resolver.ResolveAsync(locator, Context(window, capture), default);
        Assert.Equal(LocatorStatus.Resolved, ok.Status);
        var box = Assert.Single(ok.Bounds);
        Assert.True(box.X <= window.ClientBounds.X + 16, $"the mask must cover the checkbox (left edge {box.X - window.ClientBounds.X} in client space)");
        Assert.True(box.Bottom <= window.ClientBounds.Y + 59, "the mask must stay above the second label");
        Assert.InRange(ok.Confidence, 0.9, 1.0);

        // Growing downwards by 24 DIP would cover "Start hidden …": rejected instead of masking the neighbor.
        var tooBig = await resolver.ResolveAsync(locator with { Expand = new Insets(16, 2, 2, 24) }, Context(window, capture), default);
        Assert.Equal(LocatorStatus.Rejected, tooBig.Status);
        Assert.DoesNotContain("Recognized:", tooBig.Message ?? ""); // only query patterns, no recognized text
        Assert.Equal(1, resolver.InferenceCount); // the second resolution reused the cached OCR result
    }

    [SkippableFact]
    public async Task ConfidenceThreshold_DistinguishesRejectedFromNotFound()
    {
        TestEnvironment.RequireModels();
        var image = TestEnvironment.LoadFixture("en_low_contrast"); // recognized as "number704-XK2-M9" (0.976)
        var capture = new FakeCapture { ImageFactory = _ => image };
        var resolver = new OcrLocatorResolver(fixture.Engine);
        var window = Windows.At(0, 0, 360, 56);
        var strict = await resolver.ResolveAsync(ElementLocator.ByOcrText("XK2") with { MinConfidence = 0.99 }, Context(window, capture), default);
        Assert.Equal(LocatorStatus.Rejected, strict.Status);
        Assert.Contains("confidence too low", strict.Message);
        var lenient = await resolver.ResolveAsync(ElementLocator.ByOcrText("XK2") with { MinConfidence = 0.6 }, Context(window, capture), default);
        Assert.Equal(LocatorStatus.Resolved, lenient.Status);
        var absent = await resolver.ResolveAsync(ElementLocator.ByOcrText("password"), Context(window, capture), default);
        Assert.Equal(LocatorStatus.NotFound, absent.Status);
    }

    [SkippableTheory]
    [InlineData("zh_hans", "余额")]
    [InlineData("zh_hant", "設定")]
    [InlineData("ja", "ログイン")]
    [InlineData("dialog", "john.doe@example.com")]
    public async Task FindsTextInEveryValidatedScript(string fixtureName, string query)
    {
        TestEnvironment.RequireModels();
        var image = TestEnvironment.LoadFixture(fixtureName);
        var capture = new FakeCapture { ImageFactory = _ => image };
        var window = Windows.At(100, 100, image.Width, image.Height);
        var r = await new OcrLocatorResolver(fixture.Engine).ResolveAsync(ElementLocator.ByOcrText(query), Context(window, capture), default);
        Assert.Equal(LocatorStatus.Resolved, r.Status);
        Assert.True(window.ClientBounds.Contains(r.Bounds[0].Intersect(window.ClientBounds)));
    }
}
