using BenchmarkDotNet.Attributes;
using WindowVeil.Imaging;
using WindowVeil.Ocr;
using WindowVeil.Ocr.Paddle;

namespace WindowVeil.Benchmarks;

/// <summary>PP-OCRv6 (small) initialization and recognition on deterministic fixtures. Requires tools/download-models.*.</summary>
[MemoryDiagnoser]
public class OcrBenchmarks
{
    private PaddleOcrEngine _engine = null!;
    private BgraImage _line = null!, _dialog = null!;

    /// <summary>Loads models and fixtures.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _engine = PaddleOcrEngine.Create(PaddleOcrModelOptions.FromDirectory(Shared.ModelsDir()));
        _line = PngCodec.Load(Shared.Fixture("en_balance.png"));
        _dialog = PngCodec.Load(Shared.Fixture("dialog.png"));
    }

    /// <summary>Releases the inference sessions.</summary>
    [GlobalCleanup]
    public void Cleanup() => _engine.Dispose();

    /// <summary>Model loading (both ONNX sessions + dictionary).</summary>
    [Benchmark]
    public void ModelInitialization()
    {
        using var e = PaddleOcrEngine.Create(PaddleOcrModelOptions.FromDirectory(Shared.ModelsDir()));
    }

    /// <summary>Detection + recognition of a single text line.</summary>
    [Benchmark]
    public ValueTask<OcrResult> RecognizeLine() => _engine.RecognizeAsync(_line);

    /// <summary>Detection + recognition of a dialog screenshot.</summary>
    [Benchmark]
    public ValueTask<OcrResult> RecognizeDialog() => _engine.RecognizeAsync(_dialog);
}
