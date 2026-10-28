using WindowVeil.Geometry;
using WindowVeil.Imaging;
using WindowVeil.Ocr;
using WindowVeil.Ocr.Paddle;
using Xunit;

namespace WindowVeil.OcrTests;

/// <summary>Model loading, validation, options and cancellation of <see cref="PaddleOcrEngine"/> (needs the real models).</summary>
[Collection(SerialCollection.Name)]
public class PaddleSessionReuseTests
{
    [SkippableFact]
    public async Task SessionsAreCreatedOnce_AndReusedAcrossCalls()
    {
        var root = TestEnvironment.RequireModels();
        int before = PaddleOcrEngine.SessionsCreated;
        using var engine = PaddleOcrEngine.Create(PaddleOcrModelOptions.FromDirectory(root));
        int afterCreate = PaddleOcrEngine.SessionsCreated;
        var image = TestEnvironment.LoadFixture("en_balance");
        for (int i = 0; i < 3; i++)
        {
            var r = await engine.RecognizeAsync(image);
            Assert.Equal("Account Balance $12,345.67", Assert.Single(r.Regions).Text);
        }
        Assert.True(afterCreate - before >= 2);
        Assert.Equal(afterCreate, PaddleOcrEngine.SessionsCreated); // no session was created by RecognizeAsync
        Assert.Equal(3, engine.DetectionRunCount);
        Assert.Equal(3, engine.RecognitionRunCount);
    }
}

/// <summary>Model loading, validation, options and cancellation of <see cref="PaddleOcrEngine"/> (needs the real models).</summary>
public class PaddleEngineTests(PaddleEngineFixture fixture) : IClassFixture<PaddleEngineFixture>
{

    [SkippableFact]
    public void Dictionary_IsParsedExactly_AndMatchesTheModelClassCount()
    {
        TestEnvironment.RequireModels();
        var e = fixture.Engine;
        Assert.Equal("PP-OCRv6_small_det", e.DetectionConfig.ModelName);
        Assert.Equal("PP-OCRv6_small_rec", e.RecognitionConfig.ModelName);
        var d = e.Dictionary;
        Assert.Equal(18708, d.Entries.Count);
        Assert.Equal(18710, d.ClassCount); // blank + 18708 + space = model output classes
        Assert.Equal("\"", d.Entries[1]);
        Assert.Equal("'", d.Entries[6]);   // YAML '''' escape
        Assert.Equal("\\", d.Entries[23]); // plain backslash scalar
        Assert.Equal("😀", d.Entries[18563]); // non-BMP (surrogate pair)
        Assert.Equal("\U0001F6C5", d.Entries[^1]);
        Assert.Contains("账", d.Entries);
        Assert.Equal(d.Entries.Count, d.Entries.Distinct().Count());
        Assert.Equal(48, e.RecognitionConfig.ImageHeight);
        Assert.Equal(new DbParameters(0.2f, 0.45f, 1.4, 3000), e.DetectionParameters); // from the det inference.yml
    }

    [SkippableFact]
    public void TinyRecognitionModel_HasItsOwnDictionary()
    {
        var root = TestEnvironment.ModelsRoot;
        var tiny = root is null ? null : PaddleOcrModelOptions.FromDirectory(root, PaddleOcrModelVariant.Tiny);
        Skip.If(tiny is null || !File.Exists(tiny.RecognitionModelPath), "tiny models not downloaded (tools/download-models.sh --variant tiny)");
        using var engine = PaddleOcrEngine.Create(tiny!);
        Assert.Equal(6904, engine.Dictionary.Entries.Count);
        Assert.Equal(0.4f, engine.DetectionParameters.BoxThreshold);
    }

    [SkippableFact]
    public void MismatchedDictionary_IsAModelError()
    {
        var root = TestEnvironment.RequireModels();
        var small = PaddleOcrModelOptions.FromDirectory(root);
        var dir = Directory.CreateTempSubdirectory("wv-ocr-");
        try
        {
            // The small recognition config with its last dictionary entry removed: 18707 entries instead of 18708.
            var lines = File.ReadAllLines(small.RecognitionConfigPath).ToList();
            int last = lines.FindLastIndex(l => l.StartsWith("  - ", StringComparison.Ordinal));
            lines.RemoveAt(last);
            var yml = Path.Combine(dir.FullName, "inference.yml");
            File.WriteAllLines(yml, lines);
            var options = small with { RecognitionConfigPath = yml };
            var ex = Assert.Throws<PaddleOcrModelException>(() => PaddleOcrEngine.Create(options));
            Assert.Equal(OcrStatus.ModelError, ex.Status);
            Assert.Equal(WindowVeilErrorCode.OcrUnavailable, ex.Code);
            Assert.Contains("outputs 18710 classes", ex.Message);
            Assert.Contains("has 18707 entries", ex.Message);
            Assert.Contains("not interchangeable", ex.Message);
            Assert.False(PaddleOcrEngine.TryCreate(options, out var engine, out var error));
            Assert.Null(engine);
            Assert.Contains("18707", error);
        }
        finally { dir.Delete(true); }

        // The tiny dictionary (6904 entries) with the small model, when the tiny files are present.
        var tiny = PaddleOcrModelOptions.FromDirectory(root, PaddleOcrModelVariant.Tiny);
        if (File.Exists(tiny.RecognitionConfigPath))
            Assert.Contains("6904", Assert.Throws<PaddleOcrModelException>(() => PaddleOcrEngine.Create(small with { RecognitionConfigPath = tiny.RecognitionConfigPath })).Message);
    }

    [SkippableFact]
    public void UseSpaceCharFalse_DoesNotMatchTheModel()
    {
        var root = TestEnvironment.RequireModels();
        var ex = Assert.Throws<PaddleOcrModelException>(() => PaddleOcrEngine.Create(PaddleOcrModelOptions.FromDirectory(root) with { UseSpaceChar = false }));
        Assert.Contains("18709", ex.Message);
    }

    [SkippableFact]
    public void SwappedModels_AreRejected()
    {
        var root = TestEnvironment.RequireModels();
        var o = PaddleOcrModelOptions.FromDirectory(root);
        // Recognition config passed as detection config.
        Assert.Throws<PaddleOcrModelException>(() => PaddleOcrEngine.Create(o with { DetectionConfigPath = o.RecognitionConfigPath }));
        // Detection network passed as recognition network (output rank 4 instead of 3).
        var ex = Assert.Throws<PaddleOcrModelException>(() => PaddleOcrEngine.Create(o with { RecognitionModelPath = o.DetectionModelPath }));
        Assert.Contains("rank 3", ex.Message);
    }

    [SkippableFact]
    public void NonPpOcrV6Config_IsRejected()
    {
        var root = TestEnvironment.RequireModels();
        var o = PaddleOcrModelOptions.FromDirectory(root);
        var dir = Directory.CreateTempSubdirectory("wv-ocr-");
        try
        {
            var yml = Path.Combine(dir.FullName, "inference.yml");
            File.WriteAllText(yml, File.ReadAllText(o.DetectionConfigPath).Replace("PP-OCRv6_small_det", "PP-OCRv5_server_det"));
            var ex = Assert.Throws<PaddleOcrModelException>(() => PaddleOcrEngine.Create(o with { DetectionConfigPath = yml }));
            Assert.Contains("PP-OCRv6", ex.Message);
        }
        finally { dir.Delete(true); }
    }

    [Fact]
    public void MissingModelFile_IsAModelError()
    {
        var o = PaddleOcrModelOptions.FromDirectory(Path.Combine(Path.GetTempPath(), "does-not-exist-" + Guid.NewGuid()));
        var ex = Assert.Throws<PaddleOcrModelException>(() => PaddleOcrEngine.Create(o));
        Assert.Contains("not found", ex.Message);
        Assert.Contains("download-models", ex.Message);
    }

    [SkippableFact]
    public void CorruptModelFile_IsAModelError()
    {
        var root = TestEnvironment.RequireModels();
        var o = PaddleOcrModelOptions.FromDirectory(root);
        var dir = Directory.CreateTempSubdirectory("wv-ocr-");
        try
        {
            var bad = Path.Combine(dir.FullName, "inference.onnx");
            File.WriteAllBytes(bad, Enumerable.Range(0, 4096).Select(i => (byte)(i * 31)).ToArray());
            var ex = Assert.Throws<PaddleOcrModelException>(() => PaddleOcrEngine.Create(o with { RecognitionModelPath = bad }));
            Assert.Contains("could not be loaded", ex.Message);
            var badYml = Path.Combine(dir.FullName, "inference.yml");
            File.WriteAllText(badYml, "Global: [unclosed");
            Assert.Throws<PaddleOcrModelException>(() => PaddleOcrEngine.Create(o with { RecognitionConfigPath = badYml }));
        }
        finally { dir.Delete(true); }
    }

    [SkippableFact]
    public async Task BlankImage_SucceedsWithNoRegions()
    {
        TestEnvironment.RequireModels();
        var r = await fixture.Engine.RecognizeAsync(TestEnvironment.LoadFixture("blank"));
        Assert.Equal(OcrStatus.Succeeded, r.Status);
        Assert.Empty(r.Regions);
        // A tiny image (h + w < 64 triggers the official padding path) also works.
        var tiny = await fixture.Engine.RecognizeAsync(new BgraImage(5, 3));
        Assert.Equal(OcrStatus.Succeeded, tiny.Status);
    }

    [SkippableFact]
    public async Task InvalidRegionOfInterest_IsInvalidImage()
    {
        TestEnvironment.RequireModels();
        var image = TestEnvironment.LoadFixture("en_settings");
        var outside = await fixture.Engine.RecognizeAsync(image, new OcrOptions { RegionOfInterest = new RectD(1000, 1000, 10, 10) });
        Assert.Equal(OcrStatus.InvalidImage, outside.Status);
        var empty = await fixture.Engine.RecognizeAsync(image, new OcrOptions { RegionOfInterest = new RectD(10, 10, 0, 5) });
        Assert.Equal(OcrStatus.InvalidImage, empty.Status);
        var huge = await fixture.Engine.RecognizeAsync(image, new OcrOptions { MaxImagePixels = 100 });
        Assert.Equal(OcrStatus.InvalidImage, huge.Status);
        Assert.Equal(0, fixture.Engine.DetectionRunCount - fixture.Engine.DetectionRunCount); // nothing ran (smoke)
    }

    [SkippableFact]
    public async Task RegionOfInterest_ReturnsFullImageCoordinates()
    {
        TestEnvironment.RequireModels();
        var image = TestEnvironment.LoadFixture("en_settings");
        var full = await fixture.Engine.RecognizeAsync(image);
        var roi = await fixture.Engine.RecognizeAsync(image, new OcrOptions { RegionOfInterest = new RectD(0, 50, 520, 50) });
        var line = Assert.Single(roi.Regions);
        Assert.Equal("Start hidden in the notification area", line.Text);
        var reference = full.Regions.Single(r => r.Text == line.Text);
        // The 50 px high crop is upscaled to 64 px for detection, so the box differs slightly from the full-image run.
        double iou = PaddleReferenceTests.Iou(reference.BoundingBox, line.BoundingBox);
        Assert.True(iou > 0.75, $"IoU {iou:0.000}: {reference.BoundingBox} vs {line.BoundingBox}");
        Assert.InRange(line.BoundingBox.Y, 50, 70);
    }

    [SkippableFact]
    public async Task MinConfidence_FiltersRecognizedLines()
    {
        TestEnvironment.RequireModels();
        var image = TestEnvironment.LoadFixture("en_settings");
        var r = await fixture.Engine.RecognizeAsync(image, new OcrOptions { MinConfidence = 0.995f });
        Assert.Equal("Start automatically when I sign in to Windows", Assert.Single(r.Regions).Text); // 0.9992 kept, 0.9878 dropped
    }

    [SkippableFact]
    public async Task PreCancelledToken_Throws()
    {
        TestEnvironment.RequireModels();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Engine.RecognizeAsync(TestEnvironment.LoadFixture("dialog"), null, cts.Token).AsTask());
    }

    [SkippableFact]
    public async Task CancellationDuringRecognition_Throws_AndTheEngineStaysUsable()
    {
        var root = TestEnvironment.RequireModels();
        using var engine = PaddleOcrEngine.Create(PaddleOcrModelOptions.FromDirectory(root) with { RecognitionBatchSize = 1 });
        using var cts = new CancellationTokenSource();
        int batches = 0;
        engine.Checkpoint = stage => { if (stage == "batch" && ++batches == 2) cts.Cancel(); };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.RecognizeAsync(TestEnvironment.LoadFixture("dialog"), null, cts.Token).AsTask());
        Assert.Equal(1, engine.RecognitionRunCount); // stopped between batches
        engine.Checkpoint = null;
        var ok = await engine.RecognizeAsync(TestEnvironment.LoadFixture("en_balance"));
        Assert.Equal(OcrStatus.Succeeded, ok.Status);
    }

    [SkippableFact]
    public async Task ConcurrentCalls_AreThreadSafe_AndDeterministic()
    {
        TestEnvironment.RequireModels();
        var images = new[] { "dialog", "zh_hans", "en_multiline", "ja" }.Select(TestEnvironment.LoadFixture).ToArray();
        var sequential = new List<string>();
        foreach (var img in images) sequential.Add(Describe(await fixture.Engine.RecognizeAsync(img)));
        var parallel = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => fixture.Engine.RecognizeAsync(images[i % images.Length]).AsTask()));
        for (int i = 0; i < parallel.Length; i++) Assert.Equal(sequential[i % images.Length], Describe(parallel[i]));

        static string Describe(OcrResult r) => string.Join("|", r.Regions.Select(x => $"{x.Text}@{x.BoundingBox}:{x.Confidence:0.0000}"));
    }

    [SkippableFact]
    public async Task Disposed_ThrowsObjectDisposed()
    {
        var root = TestEnvironment.RequireModels();
        var engine = PaddleOcrEngine.Create(PaddleOcrModelOptions.FromDirectory(root));
        engine.Dispose();
        engine.Dispose(); // idempotent
        await Assert.ThrowsAsync<ObjectDisposedException>(() => engine.RecognizeAsync(new BgraImage(10, 10)).AsTask());
    }

    [Fact]
    public void FromDirectory_UsesTheDownloadScriptLayout()
    {
        var o = PaddleOcrModelOptions.FromDirectory("/m", PaddleOcrModelVariant.Medium);
        Assert.Equal(Path.Combine("/m", "PP-OCRv6_medium_det", "inference.onnx"), o.DetectionModelPath);
        Assert.Equal(Path.Combine("/m", "PP-OCRv6_medium_rec", "inference.yml"), o.RecognitionConfigPath);
        Assert.Equal(64, o.DetectionLimitSideLength);
        Assert.Equal(DetectionLimitType.Min, o.DetectionLimitType);
        Assert.Equal(4000, o.DetectionMaxSideLimit);
        Assert.Equal(6, o.RecognitionBatchSize);
        Assert.True(o.UseSpaceChar);
        Assert.Null(o.DetectionThreshold);
        Assert.Equal((0.3f, 0.6f, 1.5), (o.WithPipelineDetectionThresholds().DetectionThreshold!.Value, o.WithPipelineDetectionThresholds().DetectionBoxThreshold!.Value, o.WithPipelineDetectionThresholds().UnclipRatio!.Value));
    }
}
