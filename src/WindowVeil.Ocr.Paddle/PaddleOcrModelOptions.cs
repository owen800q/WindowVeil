namespace WindowVeil.Ocr.Paddle;

/// <summary>PP-OCRv6 model size.</summary>
public enum PaddleOcrModelVariant
{
    /// <summary>PP-OCRv6_tiny (fastest; recognition dictionary differs from small/medium).</summary>
    Tiny,

    /// <summary>PP-OCRv6_small (default: good accuracy at desktop CPU cost).</summary>
    Small,

    /// <summary>PP-OCRv6_medium (most accurate, slowest).</summary>
    Medium,
}

/// <summary>
/// Model files and pipeline parameters for <see cref="PaddleOcrEngine"/>. Defaults reproduce the official PaddleOCR 3.x
/// OCR pipeline, except that the DB thresholds come from the detection model's own <c>inference.yml</c> (see remarks).
/// </summary>
/// <remarks>
/// The official pipeline config (<c>paddlex/configs/pipelines/OCR.yaml</c>) uses <c>limit_side_len=64</c>,
/// <c>limit_type=min</c>, <c>max_side_limit=4000</c>, recognition <c>batch_size=6</c> and <c>score_thresh=0.0</c>; those are the
/// defaults here. That config also overrides the DB thresholds with 0.3 / 0.6 / 1.5, whereas the model's inference.yml ships
/// 0.2 / 0.45 / 1.4 (tiny: box_thresh 0.4), which are what the stand-alone text detection module uses. WindowVeil defaults to
/// the model's values (better recall on small UI text); <see cref="WithPipelineDetectionThresholds"/> selects the pipeline values.
/// </remarks>
public sealed record PaddleOcrModelOptions
{
    /// <summary>Path of the detection <c>inference.onnx</c>.</summary>
    public required string DetectionModelPath { get; init; }

    /// <summary>Path of the detection <c>inference.yml</c> (model name, DB thresholds, normalization).</summary>
    public required string DetectionConfigPath { get; init; }

    /// <summary>Path of the recognition <c>inference.onnx</c>.</summary>
    public required string RecognitionModelPath { get; init; }

    /// <summary>Path of the recognition <c>inference.yml</c> (model name, input shape, character dictionary).</summary>
    public required string RecognitionConfigPath { get; init; }

    /// <summary>ONNX Runtime intra-op threads. Default min(4, processor count).</summary>
    public int IntraOpNumThreads { get; init; } = Math.Min(4, Environment.ProcessorCount);

    /// <summary>ONNX Runtime inter-op threads (only used by parallel execution mode). Default 1.</summary>
    public int InterOpNumThreads { get; init; } = 1;

    /// <summary>Detection resize limit (<c>text_det_limit_side_len</c>). Default 64 (official pipeline).</summary>
    public int DetectionLimitSideLength { get; init; } = 64;

    /// <summary>Detection resize mode (<c>text_det_limit_type</c>). Default <see cref="Paddle.DetectionLimitType.Min"/> (official pipeline).</summary>
    public DetectionLimitType DetectionLimitType { get; init; } = DetectionLimitType.Min;

    /// <summary>Maximum detection input side (<c>max_side_limit</c>). Default 4000 (official pipeline).</summary>
    public int DetectionMaxSideLimit { get; init; } = 4000;

    /// <summary>DB binarization threshold; null uses the detection inference.yml (0.2 for PP-OCRv6).</summary>
    public float? DetectionThreshold { get; init; }

    /// <summary>DB box score threshold; null uses the detection inference.yml (0.45 for small/medium, 0.4 for tiny).</summary>
    public float? DetectionBoxThreshold { get; init; }

    /// <summary>DB unclip ratio; null uses the detection inference.yml (1.4 for PP-OCRv6).</summary>
    public double? UnclipRatio { get; init; }

    /// <summary>Maximum DB contour candidates; null uses the detection inference.yml (3000 for PP-OCRv6).</summary>
    public int? MaxCandidates { get; init; }

    /// <summary>Recognition batch size. Default 6 (official pipeline). Crops are sorted by aspect ratio before batching, as officially.</summary>
    public int RecognitionBatchSize { get; init; } = 6;

    /// <summary>Whether a space class follows the dictionary (official <c>use_space_char=True</c>).</summary>
    public bool UseSpaceChar { get; init; } = true;

    /// <summary>
    /// Whether <see cref="PaddleOcrEngine.RecognizeAsync"/> runs inference on the thread pool (default true) instead of the
    /// calling thread, so UI threads are never blocked.
    /// </summary>
    public bool RunOnThreadPool { get; init; } = true;

    /// <summary>
    /// Builds options for the layout produced by <c>tools/download-models.sh</c>:
    /// <c>&lt;root&gt;/PP-OCRv6_&lt;variant&gt;_det/inference.onnx</c>, <c>inference.yml</c> and the same for <c>_rec</c>.
    /// </summary>
    public static PaddleOcrModelOptions FromDirectory(string modelsRoot, PaddleOcrModelVariant variant = PaddleOcrModelVariant.Small)
    {
        ArgumentException.ThrowIfNullOrEmpty(modelsRoot);
        string v = variant switch
        {
            PaddleOcrModelVariant.Tiny => "tiny",
            PaddleOcrModelVariant.Small => "small",
            PaddleOcrModelVariant.Medium => "medium",
            _ => throw new ArgumentOutOfRangeException(nameof(variant)),
        };
        string det = Path.Combine(modelsRoot, $"PP-OCRv6_{v}_det"), rec = Path.Combine(modelsRoot, $"PP-OCRv6_{v}_rec");
        return new PaddleOcrModelOptions
        {
            DetectionModelPath = Path.Combine(det, "inference.onnx"),
            DetectionConfigPath = Path.Combine(det, "inference.yml"),
            RecognitionModelPath = Path.Combine(rec, "inference.onnx"),
            RecognitionConfigPath = Path.Combine(rec, "inference.yml"),
        };
    }

    /// <summary>Returns a copy using the official OCR pipeline's DB thresholds (0.3 / 0.6 / 1.5) instead of the model's.</summary>
    public PaddleOcrModelOptions WithPipelineDetectionThresholds() =>
        this with { DetectionThreshold = 0.3f, DetectionBoxThreshold = 0.6f, UnclipRatio = 1.5 };

    internal void Validate()
    {
        ArgumentException.ThrowIfNullOrEmpty(DetectionModelPath);
        ArgumentException.ThrowIfNullOrEmpty(DetectionConfigPath);
        ArgumentException.ThrowIfNullOrEmpty(RecognitionModelPath);
        ArgumentException.ThrowIfNullOrEmpty(RecognitionConfigPath);
        if (IntraOpNumThreads < 0) throw new ArgumentOutOfRangeException(nameof(IntraOpNumThreads), "Must not be negative (0 lets ONNX Runtime decide).");
        if (InterOpNumThreads < 0) throw new ArgumentOutOfRangeException(nameof(InterOpNumThreads), "Must not be negative.");
        if (DetectionLimitSideLength <= 0) throw new ArgumentOutOfRangeException(nameof(DetectionLimitSideLength), "Must be positive.");
        if (DetectionMaxSideLimit < 32) throw new ArgumentOutOfRangeException(nameof(DetectionMaxSideLimit), "Must be at least 32.");
        if (RecognitionBatchSize <= 0) throw new ArgumentOutOfRangeException(nameof(RecognitionBatchSize), "Must be positive.");
        if (DetectionThreshold is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(DetectionThreshold), "Must be within 0..1.");
        if (DetectionBoxThreshold is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(DetectionBoxThreshold), "Must be within 0..1.");
        if (UnclipRatio is <= 0) throw new ArgumentOutOfRangeException(nameof(UnclipRatio), "Must be positive.");
        if (MaxCandidates is <= 0) throw new ArgumentOutOfRangeException(nameof(MaxCandidates), "Must be positive.");
    }
}

/// <summary>
/// Raised by <see cref="PaddleOcrEngine.Create"/> when model files are missing, unreadable, corrupt, or do not belong
/// together (for example a recognition model whose class count does not match its dictionary). Corresponds to
/// <see cref="OcrStatus.ModelError"/>.
/// </summary>
public sealed class PaddleOcrModelException : WindowVeilException
{
    /// <summary>Creates the exception.</summary>
    public PaddleOcrModelException(string message, Exception? innerException = null)
        : base(WindowVeilErrorCode.OcrUnavailable, message, innerException) { }

    /// <summary>Always <see cref="OcrStatus.ModelError"/>.</summary>
    public OcrStatus Status => OcrStatus.ModelError;
}
