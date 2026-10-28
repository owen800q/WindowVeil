using System.Text.Json;
using WindowVeil.Geometry;
using WindowVeil.Ocr;
using WindowVeil.Ocr.Paddle;
using Xunit;
using Xunit.Abstractions;

namespace WindowVeil.OcrTests;

/// <summary>
/// End-to-end comparison with outputs of the official PaddleOCR pipeline (Fixtures/reference/*.json, see the README there).
/// </summary>
public class PaddleReferenceTests(PaddleEngineFixture fixture, ITestOutputHelper output) : IClassFixture<PaddleEngineFixture>
{
    public static TheoryData<string> Fixtures => new()
    {
        "en_settings", "en_balance", "zh_hans", "zh_hant", "ja", "en_multiline", "en_low_contrast", "blank", "dialog",
    };

    [SkippableTheory]
    [MemberData(nameof(Fixtures))]
    public async Task MatchesOfficialPipeline(string name)
    {
        TestEnvironment.RequireModels();
        var result = await fixture.Engine.RecognizeAsync(TestEnvironment.LoadFixture(name));
        AssertMatchesReference(name, name, result);
    }

    [SkippableTheory]
    [MemberData(nameof(Fixtures))]
    public async Task MatchesOfficialPipeline_WithPipelineDefaultThresholds(string name)
    {
        TestEnvironment.RequireModels();
        var result = await PipelineDefaultsEngine.Value.RecognizeAsync(TestEnvironment.LoadFixture(name));
        AssertMatchesReference(name, name + ".pipeline-defaults", result);
    }

    // Shared by the theory cases; released when the test process exits.
    private static readonly Lazy<PaddleOcrEngine> PipelineDefaultsEngine = new(() =>
        PaddleOcrEngine.Create(PaddleOcrModelOptions.FromDirectory(TestEnvironment.ModelsRoot!).WithPipelineDetectionThresholds()));

    private void AssertMatchesReference(string fixtureName, string referenceName, OcrResult result)
    {
        Assert.Equal(OcrStatus.Succeeded, result.Status);
        using var doc = JsonDocument.Parse(File.ReadAllText(TestEnvironment.FixturePath($"reference/{referenceName}.json")));
        var expected = doc.RootElement.GetProperty("regions").EnumerateArray().Select(r => (
            Text: r.GetProperty("text").GetString()!,
            Score: r.GetProperty("score").GetDouble(),
            Poly: r.GetProperty("poly").EnumerateArray().Select(p => new PointD(p[0].GetDouble(), p[1].GetDouble())).ToArray())).ToArray();

        output.WriteLine($"{fixtureName}: {result}");
        for (int i = 0; i < Math.Max(expected.Length, result.Regions.Count); i++)
        {
            var e = i < expected.Length ? $"'{expected[i].Text}' {expected[i].Score:0.0000} [{string.Join(" ", expected[i].Poly)}]" : "-";
            var a = i < result.Regions.Count ? $"'{result.Regions[i].Text}' {result.Regions[i].Confidence:0.0000} [{string.Join(" ", result.Regions[i].Polygon)}]" : "-";
            output.WriteLine($"  ref {e}\n  got {a}");
        }

        Assert.Equal(expected.Length, result.Regions.Count);
        double maxScoreDelta = expected.Select((e, i) => Math.Abs(e.Score - result.Regions[i].Confidence)).DefaultIfEmpty(0).Max();
        int identicalPolygons = expected.Where((e, i) => e.Poly.SequenceEqual(result.Regions[i].Polygon)).Count();
        output.WriteLine($"PARITY {referenceName}: {expected.Length} regions, identical polygons {identicalPolygons}/{expected.Length}, max |Δconfidence| {maxScoreDelta:0.000000}");
        for (int i = 0; i < expected.Length; i++)
        {
            var got = result.Regions[i];
            Assert.Equal(expected[i].Text, got.Text);
            Assert.InRange(Math.Abs(got.Confidence - expected[i].Score), 0, 0.05);
            double iou = Iou(RectD.FromPoints(expected[i].Poly), got.BoundingBox);
            Assert.True(iou >= 0.85, $"region {i}: IoU {iou:0.000} with the reference polygon");
            Assert.Equal(4, got.Polygon.Count);
        }
    }

    internal static double Iou(RectD a, RectD b)
    {
        double ix = Math.Max(0, Math.Min(a.Right, b.Right) - Math.Max(a.X, b.X));
        double iy = Math.Max(0, Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Y, b.Y));
        double inter = ix * iy, union = a.Width * a.Height + b.Width * b.Height - inter;
        return union <= 0 ? 0 : inter / union;
    }
}
