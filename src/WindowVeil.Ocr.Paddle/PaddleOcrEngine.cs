using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OrtSessionOptions = Microsoft.ML.OnnxRuntime.SessionOptions;
using WindowVeil.Geometry;
using WindowVeil.Imaging;

namespace WindowVeil.Ocr.Paddle;

/// <summary>
/// <see cref="IOcrProvider"/> running the official PaddleOCR PP-OCRv6 text detection and recognition ONNX models on ONNX
/// Runtime (CPU). Everything runs locally; no text or pixels leave the process.
/// </summary>
/// <remarks>
/// <para><b>Lifetime.</b> <see cref="Create"/> loads both models once (and validates them); the two
/// <see cref="InferenceSession"/>s are reused by every call. The engine is thread-safe (ONNX Runtime sessions support
/// concurrent <c>Run</c>); <see cref="Dispose"/> waits for running calls before releasing the sessions.</para>
/// <para><b>Errors.</b> Model problems are detected eagerly: <see cref="Create"/> throws <see cref="PaddleOcrModelException"/>
/// (missing/corrupt files, unexpected inputs/outputs, dictionary/class-count mismatch, non-PP-OCRv6 models). After that,
/// <see cref="RecognizeAsync"/> reports problems as results: <see cref="OcrStatus.InvalidImage"/> for unusable input,
/// <see cref="OcrStatus.ModelError"/> for unexpected model outputs and <see cref="OcrStatus.Failed"/> for runtime failures.
/// Cancellation throws <see cref="OperationCanceledException"/>; it is checked before detection, after detection and between
/// recognition batches, and it also terminates a running ONNX Runtime call.</para>
/// <para><b>Pipeline.</b> BGRA → BGR; <c>DetResizeForTest</c> + <c>NormalizeImage</c>; DB post-processing; reading-order sort;
/// min-area-rectangle perspective crops; aspect-ratio-sorted recognition batches; greedy CTC decoding. See docs/OCR.md for the
/// mapping to the official PaddleX code.</para>
/// </remarks>
public sealed partial class PaddleOcrEngine : IOcrProvider, IDisposable
{
    private static int s_sessionsCreated;

    private readonly InferenceSession _det;
    private readonly InferenceSession _rec;
    private readonly string _detInput, _detOutput, _recInput, _recOutput;
    private readonly DbParameters _db;
    private int _active;
    private long _detRuns, _recRuns;
    private volatile bool _disposed;

    private PaddleOcrEngine(PaddleOcrModelOptions options, DetectionModelConfig det, RecognitionModelConfig rec, CharacterDictionary dictionary,
        InferenceSession detSession, InferenceSession recSession, string detIn, string detOut, string recIn, string recOut)
    {
        Options = options;
        DetectionConfig = det;
        RecognitionConfig = rec;
        Dictionary = dictionary;
        _det = detSession;
        _rec = recSession;
        _detInput = detIn;
        _detOutput = detOut;
        _recInput = recIn;
        _recOutput = recOut;
        _db = new DbParameters(
            options.DetectionThreshold ?? det.Threshold,
            options.DetectionBoxThreshold ?? det.BoxThreshold,
            options.UnclipRatio ?? det.UnclipRatio,
            options.MaxCandidates ?? det.MaxCandidates);
        Name = $"PaddleOCR {det.ModelName}+{rec.ModelName}";
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <summary>Options the engine was created with.</summary>
    public PaddleOcrModelOptions Options { get; }

    /// <summary>Settings read from the detection inference.yml.</summary>
    public DetectionModelConfig DetectionConfig { get; }

    /// <summary>Settings read from the recognition inference.yml.</summary>
    public RecognitionModelConfig RecognitionConfig { get; }

    /// <summary>The recognition character table (validated against the model's class count).</summary>
    public CharacterDictionary Dictionary { get; }

    /// <summary>Effective DB post-processing parameters.</summary>
    public DbParameters DetectionParameters => _db;

    /// <summary>Number of detection inferences run by this engine.</summary>
    public long DetectionRunCount => Interlocked.Read(ref _detRuns);

    /// <summary>Number of recognition batch inferences run by this engine.</summary>
    public long RecognitionRunCount => Interlocked.Read(ref _recRuns);

    /// <summary>Total ONNX Runtime sessions created by all engines in this process (diagnostics/tests).</summary>
    public static int SessionsCreated => Volatile.Read(ref s_sessionsCreated);

    /// <summary>Test hook invoked at pipeline checkpoints ("detected", "batch").</summary>
    internal Action<string>? Checkpoint { get; set; }

    [GeneratedRegex(@"^PP-OCRv6_(tiny|small|medium)_(det|rec)$")]
    private static partial Regex ModelNamePattern();

    /// <summary>
    /// Loads and validates the detection and recognition models.
    /// </summary>
    /// <exception cref="PaddleOcrModelException">A file is missing or corrupt, or the models/configs do not match.</exception>
    public static PaddleOcrEngine Create(PaddleOcrModelOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        foreach (var (path, what) in new[] { (options.DetectionModelPath, "detection model"), (options.DetectionConfigPath, "detection config"),
                     (options.RecognitionModelPath, "recognition model"), (options.RecognitionConfigPath, "recognition config") })
        {
            if (!File.Exists(path))
                throw new PaddleOcrModelException($"The {what} file was not found: {path}. Run tools/download-models.sh (or .ps1) to fetch the pinned PP-OCRv6 models.");
        }

        var det = PaddleModelConfig.LoadDetection(options.DetectionConfigPath);
        var rec = PaddleModelConfig.LoadRecognition(options.RecognitionConfigPath);
        CheckModelName(det.ModelName, "det", options.DetectionConfigPath);
        CheckModelName(rec.ModelName, "rec", options.RecognitionConfigPath);
        var dictionary = new CharacterDictionary(rec.Characters, options.UseSpaceChar);

        InferenceSession? detSession = null, recSession = null;
        try
        {
            detSession = CreateSession(options.DetectionModelPath, options, "detection");
            recSession = CreateSession(options.RecognitionModelPath, options, "recognition");

            var (detIn, detOut) = CheckIo(detSession, options.DetectionModelPath, expectedOutputRank: 4);
            var (recIn, recOut) = CheckIo(recSession, options.RecognitionModelPath, expectedOutputRank: 3);
            var recInDims = recSession.InputMetadata[recIn].Dimensions;
            if (recInDims[2] > 0 && recInDims[2] != rec.ImageHeight)
                throw new PaddleOcrModelException($"{options.RecognitionModelPath}: the model expects input height {recInDims[2]} but {options.RecognitionConfigPath} says {rec.ImageHeight}.");

            int classes = recSession.OutputMetadata[recOut].Dimensions[^1];
            if (classes <= 0) classes = ProbeClassCount(recSession, recIn, recOut, rec);
            if (classes != dictionary.ClassCount)
            {
                throw new PaddleOcrModelException(
                    $"The recognition model {options.RecognitionModelPath} outputs {classes} classes, but the dictionary in {options.RecognitionConfigPath} " +
                    $"has {dictionary.Entries.Count} entries, which implies {dictionary.ClassCount} classes (blank + entries{(options.UseSpaceChar ? " + space" : "")}). " +
                    "Recognition models and dictionaries are not interchangeable: use the inference.yml shipped with the same model.");
            }
            return new PaddleOcrEngine(options, det, rec, dictionary, detSession, recSession, detIn, detOut, recIn, recOut);
        }
        catch
        {
            detSession?.Dispose();
            recSession?.Dispose();
            throw;
        }
    }

    /// <summary>Like <see cref="Create"/> but returns false with an error message instead of throwing for model problems.</summary>
    public static bool TryCreate(PaddleOcrModelOptions options, out PaddleOcrEngine? engine, out string? error)
    {
        try
        {
            engine = Create(options);
            error = null;
            return true;
        }
        catch (PaddleOcrModelException ex)
        {
            engine = null;
            error = ex.Message;
            return false;
        }
    }

    private static void CheckModelName(string name, string kind, string path)
    {
        var m = ModelNamePattern().Match(name);
        if (!m.Success || m.Groups[2].Value != kind)
            throw new PaddleOcrModelException($"{path}: model_name '{name}' is not a PP-OCRv6 text {(kind == "det" ? "detection" : "recognition")} model (expected PP-OCRv6_<tiny|small|medium>_{kind}).");
    }

    private static InferenceSession CreateSession(string path, PaddleOcrModelOptions options, string what)
    {
        using var so = new OrtSessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            IntraOpNumThreads = options.IntraOpNumThreads,
            InterOpNumThreads = options.InterOpNumThreads,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
        };
        try
        {
            var session = new InferenceSession(path, so);
            Interlocked.Increment(ref s_sessionsCreated);
            return session;
        }
        catch (OnnxRuntimeException ex)
        {
            throw new PaddleOcrModelException($"The {what} model {path} could not be loaded (corrupt or not an ONNX model): {ex.Message}", ex);
        }
    }

    private static (string Input, string Output) CheckIo(InferenceSession s, string path, int expectedOutputRank)
    {
        if (s.InputMetadata.Count != 1 || s.OutputMetadata.Count < 1)
            throw new PaddleOcrModelException($"{path}: expected one input and at least one output, found {s.InputMetadata.Count} input(s) and {s.OutputMetadata.Count} output(s).");
        var (inName, inMeta) = s.InputMetadata.First();
        var (outName, outMeta) = s.OutputMetadata.First();
        if (inMeta.ElementDataType != TensorElementType.Float || inMeta.Dimensions.Length != 4 || (inMeta.Dimensions[1] > 0 && inMeta.Dimensions[1] != 3))
            throw new PaddleOcrModelException($"{path}: input '{inName}' must be a float32 tensor [N, 3, H, W] (found {inMeta.ElementDataType} [{string.Join(", ", inMeta.Dimensions)}]).");
        if (outMeta.ElementDataType != TensorElementType.Float || outMeta.Dimensions.Length != expectedOutputRank)
            throw new PaddleOcrModelException($"{path}: output '{outName}' must be a float32 tensor of rank {expectedOutputRank} (found {outMeta.ElementDataType} rank {outMeta.Dimensions.Length}). Is this the right kind of model?");
        return (inName, outName);
    }

    private static int ProbeClassCount(InferenceSession s, string input, string output, RecognitionModelConfig rec)
    {
        var data = new float[3 * rec.ImageHeight * rec.ImageWidth];
        using var x = OrtValue.CreateTensorValueFromMemory(data, [1, 3, rec.ImageHeight, rec.ImageWidth]);
        using var ro = new RunOptions();
        using var outs = s.Run(ro, [input], [x], [output]);
        var shape = outs[0].GetTensorTypeAndShape().Shape;
        return shape.Length == 3 ? (int)shape[2] : -1;
    }

    /// <inheritdoc />
    public async ValueTask<OcrResult> RecognizeAsync(BgraImage image, OcrOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        ObjectDisposedException.ThrowIf(_disposed, this);
        options ??= OcrOptions.Default;
        cancellationToken.ThrowIfCancellationRequested();
        var region = options.Validate(image, out var error);
        if (region is null) return OcrResult.Failure(OcrStatus.InvalidImage, error!, image.Size, provider: Name);
        var r = region.Value;
        if (Options.RunOnThreadPool)
            return await Task.Run(() => Run(image, r, options, cancellationToken), cancellationToken).ConfigureAwait(false);
        return Run(image, r, options, cancellationToken);
    }

    private OcrResult Run(BgraImage image, PixelRect region, OcrOptions options, CancellationToken ct)
    {
        Enter();
        try
        {
            var total = Stopwatch.StartNew();
            ct.ThrowIfCancellationRequested();
            var bgr = BgrImage.FromBgra(image, region);
            var boxes = Detect(bgr, ct);
            var detTime = total.Elapsed;
            Checkpoint?.Invoke("detected");
            ct.ThrowIfCancellationRequested();

            var recognized = RecognizeBoxes(bgr, boxes, ct);
            var regions = new List<OcrTextRegion>(recognized.Count);
            foreach (var (box, text, score) in recognized)
            {
                if (score < options.MinConfidence) continue;
                var poly = box.Points.Select(p => new PointD(p.X + region.X, p.Y + region.Y)).ToArray();
                regions.Add(OcrTextRegion.FromPolygon(text, score, poly));
            }
            return OcrResult.Success(regions, image.Size, detTime, total.Elapsed - detTime, Name);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OnnxRuntimeException) when (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException(ct);
        }
        catch (ModelOutputException ex)
        {
            return OcrResult.Failure(OcrStatus.ModelError, ex.Message, image.Size, ex, Name);
        }
        catch (OnnxRuntimeException ex)
        {
            return OcrResult.Failure(OcrStatus.Failed, $"ONNX Runtime inference failed: {ex.Message}", image.Size, ex, Name);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return OcrResult.Failure(OcrStatus.Failed, $"OCR failed: {ex.Message}", image.Size, ex, Name);
        }
        finally
        {
            Exit();
        }
    }

    /// <summary>Text detection on a BGR image: boxes in that image's coordinates, in official reading order.</summary>
    internal IReadOnlyList<DetectedBox> Detect(BgrImage bgr, CancellationToken ct)
    {
        var size = DetectionPreprocessor.ComputeResize(bgr.Width, bgr.Height, Options.DetectionLimitSideLength, Options.DetectionLimitType, Options.DetectionMaxSideLimit);
        var tensor = DetectionPreprocessor.Prepare(bgr, size, DetectionConfig.Normalization);
        using var input = OrtValue.CreateTensorValueFromMemory(tensor, [1, 3, size.Height, size.Width]);
        using var ro = new RunOptions();
        using var reg = ct.Register(() => ro.Terminate = true);
        Interlocked.Increment(ref _detRuns);
        using var outputs = _det.Run(ro, [_detInput], [input], [_detOutput]);
        var shape = outputs[0].GetTensorTypeAndShape().Shape;
        if (shape.Length != 4 || shape[0] != 1 || shape[1] != 1 || shape[2] != size.Height || shape[3] != size.Width)
            throw new ModelOutputException($"The detection model returned shape [{string.Join(", ", shape)}]; expected [1, 1, {size.Height}, {size.Width}].");
        var pred = outputs[0].GetTensorDataAsSpan<float>();
        var boxes = DbPostProcessor.Process(pred, size.Width, size.Height, bgr.Width, bgr.Height, _db);
        return BoxSorter.Sort(boxes);
    }

    /// <summary>Crops and recognizes boxes; returns (box, text, score) in box order, skipping empty crops.</summary>
    internal IReadOnlyList<(DetectedBox Box, string Text, float Score)> RecognizeBoxes(BgrImage bgr, IReadOnlyList<DetectedBox> boxes, CancellationToken ct)
    {
        var items = new List<(DetectedBox Box, BgrImage Crop)>(boxes.Count);
        foreach (var b in boxes)
        {
            var crop = TextLineCropper.Crop(bgr, b.Points);
            if (crop is not null) items.Add((b, crop));
        }
        var texts = new (string Text, float Score)[items.Count];
        // Official pipeline: sort crops by width/height (stable), then batch in that order.
        var order = Enumerable.Range(0, items.Count).OrderBy(i => items[i].Crop.Width / (double)items[i].Crop.Height).ToArray();
        int batchSize = Options.RecognitionBatchSize;
        for (int start = 0; start < order.Length; start += batchSize)
        {
            Checkpoint?.Invoke("batch");
            ct.ThrowIfCancellationRequested();
            var idx = order.Skip(start).Take(batchSize).ToArray();
            var crops = idx.Select(i => items[i].Crop).ToArray();
            var (data, width) = RecognitionPreprocessor.PrepareBatch(crops, RecognitionConfig.ImageHeight, RecognitionConfig.ImageWidth);
            using var input = OrtValue.CreateTensorValueFromMemory(data, [crops.Length, 3, RecognitionConfig.ImageHeight, width]);
            using var ro = new RunOptions();
            using var reg = ct.Register(() => ro.Terminate = true);
            Interlocked.Increment(ref _recRuns);
            using var outputs = _rec.Run(ro, [_recInput], [input], [_recOutput]);
            var shape = outputs[0].GetTensorTypeAndShape().Shape;
            if (shape.Length != 3 || shape[0] != crops.Length || shape[2] != Dictionary.ClassCount)
                throw new ModelOutputException($"The recognition model returned shape [{string.Join(", ", shape)}]; expected [{crops.Length}, T, {Dictionary.ClassCount}].");
            var probs = outputs[0].GetTensorDataAsSpan<float>();
            int steps = (int)shape[1], classes = (int)shape[2];
            for (int k = 0; k < idx.Length; k++)
                texts[idx[k]] = CtcDecoder.Decode(probs.Slice(k * steps * classes, steps * classes), steps, classes, Dictionary);
        }
        return items.Select((it, i) => (it.Box, texts[i].Text, texts[i].Score)).ToArray();
    }

    private void Enter()
    {
        Interlocked.Increment(ref _active);
        if (_disposed)
        {
            Interlocked.Decrement(ref _active);
            throw new ObjectDisposedException(nameof(PaddleOcrEngine));
        }
    }

    private void Exit() => Interlocked.Decrement(ref _active);

    /// <summary>Waits (up to 30 s) for running recognitions, then releases both ONNX Runtime sessions.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        SpinWait.SpinUntil(() => Volatile.Read(ref _active) == 0, TimeSpan.FromSeconds(30));
        _det.Dispose();
        _rec.Dispose();
    }

    private sealed class ModelOutputException(string message) : Exception(message);
}
