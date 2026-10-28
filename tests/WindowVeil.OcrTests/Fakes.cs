using WindowVeil.Geometry;
using WindowVeil.Imaging;
using WindowVeil.Ocr;
using WindowVeil.Platform;

namespace WindowVeil.OcrTests;

/// <summary>Capture backend returning a fixed image (or a per-call image) for the requested region.</summary>
internal sealed class FakeCapture : IScreenCapture
{
    private readonly object _gate = new();

    public Func<CaptureRequest, BgraImage> ImageFactory { get; set; } = r => new BgraImage(Math.Max(1, r.Region.Width), Math.Max(1, r.Region.Height));

    /// <summary>Overrides the frame's screen region (to simulate scaled captures or negative origins).</summary>
    public Func<CaptureRequest, BgraImage, PixelRect>? ScreenRegionOverride { get; set; }

    public bool SupportsWindow { get; set; } = true;

    public bool SupportsDesktop { get; set; } = true;

    public CaptureError WindowError { get; set; } = CaptureError.None;

    public List<CaptureRequest> Requests { get; } = [];

    public bool Supports(CaptureSource source) => source == CaptureSource.Window ? SupportsWindow : SupportsDesktop;

    public ValueTask<CaptureResult> CaptureAsync(CaptureRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate) Requests.Add(request);
        if (request.Source == CaptureSource.Window && WindowError != CaptureError.None)
            return ValueTask.FromResult(CaptureResult.Failure(WindowError, "PrintWindow returned FALSE"));
        var image = ImageFactory(request);
        var region = ScreenRegionOverride?.Invoke(request, image) ?? request.Region;
        return ValueTask.FromResult(CaptureResult.Success(new CapturedFrame(image, region, request.Source)));
    }
}

/// <summary>OCR provider returning scripted regions; counts calls and tracks concurrency.</summary>
internal sealed class FakeOcrProvider : IOcrProvider
{
    private int _running, _maxRunning, _calls;

    public string Name => "fake";

    public Func<BgraImage, IReadOnlyList<OcrTextRegion>> Regions { get; set; } = _ => [];

    public Func<BgraImage, OcrResult>? ResultOverride { get; set; }

    /// <summary>When set, each call awaits this gate (to simulate slow inference).</summary>
    public Func<int, Task>? Gate { get; set; }

    public int Calls => Volatile.Read(ref _calls);

    public int MaxConcurrency => Volatile.Read(ref _maxRunning);

    public async ValueTask<OcrResult> RecognizeAsync(BgraImage image, OcrOptions? options = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        int call = Interlocked.Increment(ref _calls);
        int now = Interlocked.Increment(ref _running);
        int seen;
        while ((seen = Volatile.Read(ref _maxRunning)) < now && Interlocked.CompareExchange(ref _maxRunning, now, seen) != seen) { }
        try
        {
            if (Gate is { } gate) await gate(call).WaitAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return ResultOverride?.Invoke(image) ?? OcrResult.Success(Regions(image), image.Size, provider: Name);
        }
        finally
        {
            Interlocked.Decrement(ref _running);
        }
    }
}

/// <summary>Manually advanced clock.</summary>
internal sealed class ManualTime : TimeProvider
{
    private long _ticks = 1_000_000;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => Interlocked.Read(ref _ticks);

    public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
}

internal sealed class FakeWindowSystem(TargetWindowInfo window) : IWindowSystem
{
    public IReadOnlyList<TargetWindowInfo> EnumerateWindows() => [window];

    public TargetWindowInfo? GetWindow(nint handle) => handle == window.Handle ? window : null;

    public bool IsProcessAlive(int processId) => true;

    public PixelRect VirtualScreenBounds => new(-1920, 0, 3840, 1080);
}

internal static class Windows
{
    /// <summary>A window whose client area starts at <paramref name="clientX"/>, <paramref name="clientY"/>.</summary>
    public static TargetWindowInfo At(int clientX, int clientY, int width = 520, int height = 100, double dpiPercent = 100, nint handle = 0x1234) => new()
    {
        Handle = handle,
        ProcessId = 42,
        ProcessName = "RetroCap",
        Title = "RetroCap",
        Bounds = new PixelRect(clientX - 8, clientY - 31, width + 16, height + 39),
        ClientBounds = new PixelRect(clientX, clientY, width, height),
        Dpi = DpiScale.FromPercent(dpiPercent),
    };

    /// <summary>An image whose pixels encode <paramref name="seed"/> (distinct content → distinct hash).</summary>
    public static BgraImage Solid(int width, int height, byte seed)
    {
        var img = new BgraImage(width, height);
        for (int i = 0; i < img.Pixels.Length; i += 4) { img.Pixels[i] = seed; img.Pixels[i + 1] = seed; img.Pixels[i + 2] = seed; img.Pixels[i + 3] = 255; }
        return img;
    }
}
