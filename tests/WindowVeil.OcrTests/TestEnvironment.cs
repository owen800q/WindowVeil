using System.Text.Json;
using WindowVeil.Imaging;
using WindowVeil.Ocr.Paddle;
using Xunit;

namespace WindowVeil.OcrTests;

/// <summary>Locates fixtures and models for the tests.</summary>
internal static class TestEnvironment
{
    private static readonly Lazy<string?> ModelsRootLazy = new(FindModelsRoot);
    private static readonly Lazy<JsonDocument> VectorsLazy = new(() => JsonDocument.Parse(File.ReadAllText(FixturePath("vectors.json"))));

    public static string FixturePath(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    public static BgraImage LoadFixture(string name) => PngCodec.Load(FixturePath(name + ".png"));

    public static JsonElement Vectors => VectorsLazy.Value.RootElement;

    public static string? ModelsRoot => ModelsRootLazy.Value;

    /// <summary>
    /// Skips the calling test when the models are absent, unless WINDOWVEIL_REQUIRE_OCR_MODELS=1 (CI), in which case it fails.
    /// </summary>
    public static string RequireModels(PaddleOcrModelVariant variant = PaddleOcrModelVariant.Small)
    {
        var root = ModelsRoot;
        var opts = root is null ? null : PaddleOcrModelOptions.FromDirectory(root, variant);
        bool present = opts is not null && File.Exists(opts.DetectionModelPath) && File.Exists(opts.RecognitionModelPath)
            && File.Exists(opts.DetectionConfigPath) && File.Exists(opts.RecognitionConfigPath);
        if (present) return root!;
        string message = $"PP-OCRv6 {variant} models not found. Set WINDOWVEIL_OCR_MODELS to a directory containing PP-OCRv6_<variant>_det/ and _rec/, " +
            "or run tools/download-models.sh to populate <repo>/models.";
        if (Environment.GetEnvironmentVariable("WINDOWVEIL_REQUIRE_OCR_MODELS") == "1")
            Assert.Fail(message + " (WINDOWVEIL_REQUIRE_OCR_MODELS=1)");
        Skip.If(true, message);
        return "";
    }

    private static string? FindModelsRoot()
    {
        var env = Environment.GetEnvironmentVariable("WINDOWVEIL_OCR_MODELS");
        if (!string.IsNullOrEmpty(env)) return env;
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "tools", "models.lock.json")))
                return Path.Combine(dir.FullName, "models");
        }
        return null;
    }
}

/// <summary>Tests that read process-wide counters must not run in parallel with other tests.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SerialCollection
{
    public const string Name = "Serial";
}

/// <summary>One engine per test run (loading models takes a moment); tests that need special options create their own.</summary>
public sealed class PaddleEngineFixture : IDisposable
{
    private readonly Lazy<PaddleOcrEngine> _engine = new(() => PaddleOcrEngine.Create(PaddleOcrModelOptions.FromDirectory(TestEnvironment.ModelsRoot!)));

    public PaddleOcrEngine Engine => _engine.Value;

    public void Dispose()
    {
        if (_engine.IsValueCreated) _engine.Value.Dispose();
    }
}
