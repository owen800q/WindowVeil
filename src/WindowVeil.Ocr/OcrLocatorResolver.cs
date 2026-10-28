using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Hashing;
using Microsoft.Extensions.Logging;
using WindowVeil.Geometry;
using WindowVeil.Imaging;
using WindowVeil.Locators;
using WindowVeil.Platform;

namespace WindowVeil.Ocr;

/// <summary>
/// Resolves <see cref="OcrTextLocator"/>s: captures the target window, runs an <see cref="IOcrProvider"/>, matches the query
/// and maps the matched boxes back to physical screen coordinates.
/// </summary>
/// <remarks>
/// <para><b>Capture.</b> The client area (or <see cref="OcrTextLocator.SearchRegion"/>, client-relative) is captured with
/// <see cref="CaptureSource.Window"/>, which excludes WindowVeil's overlays and so prevents a feedback loop where OCR reads
/// its own masks. If window capture is unsupported or fails the result is <see cref="LocatorStatus.Unavailable"/> unless
/// <see cref="OcrLocatorOptions.AllowDesktopCaptureFallback"/> is set.</para>
/// <para><b>Coordinates.</b> Providers return image-local pixels; <see cref="CapturedFrame.ImageToScreen"/> maps them to the
/// physical virtual-desktop space (origin and scale of the frame, negative coordinates allowed). <see cref="OcrTextLocator.Expand"/>
/// is in DIPs and converted with the window's DPI.</para>
/// <para><b>CPU budget.</b> Providers are reused; results are cached by an XxHash64 of the captured pixels (bounded LRU) so
/// an unchanged window costs a capture and a hash; a locator is not re-recognized before <see cref="OcrTextLocator.MinInterval"/>
/// while its window keeps its size (the previous geometry is returned as <see cref="LocatorStatus.Cached"/>, translated
/// when the window moved); at most <see cref="OcrLocatorOptions.MaxConcurrentRecognitions"/> inferences run at once, and for
/// the same window and locator the newest request wins: older requests still queued are abandoned and an older result
/// that completes after a newer request started is discarded.</para>
/// <para><b>Privacy.</b> Messages and logs contain the query, counts and confidences only, never recognized text, unless
/// <see cref="OcrLocatorOptions.IncludeRecognizedTextInDiagnostics"/> is set. Nothing is sent off the machine.</para>
/// </remarks>
public sealed class OcrLocatorResolver : ILocatorResolver, IDisposable
{
    private readonly IOcrProvider _provider;
    private readonly OcrLocatorOptions _options;
    private readonly OcrTextMatcher _matcher;
    private readonly SemaphoreSlim _slots;
    private readonly LruCache<CacheKey, OcrResult> _cache;
    private readonly ConcurrentDictionary<RequestKey, Request> _latest = new();
    private int _queued;
    private long _inferences, _cacheHits, _superseded, _rateLimited;
    private volatile bool _disposed;

    /// <summary>Creates a resolver over a provider. The provider is not disposed by the resolver.</summary>
    public OcrLocatorResolver(IOcrProvider provider, OcrLocatorOptions? options = null)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _options = options ?? new OcrLocatorOptions();
        _options.Validate();
        _matcher = new OcrTextMatcher(_options.Matching);
        _slots = new SemaphoreSlim(_options.MaxConcurrentRecognitions, _options.MaxConcurrentRecognitions);
        _cache = new LruCache<CacheKey, OcrResult>(_options.ResultCacheCapacity);
    }

    /// <summary>The OCR provider.</summary>
    public IOcrProvider Provider => _provider;

    /// <summary>Options.</summary>
    public OcrLocatorOptions Options => _options;

    /// <summary>Number of provider inferences started.</summary>
    public long InferenceCount => Interlocked.Read(ref _inferences);

    /// <summary>Number of resolutions served from the pixel-hash cache.</summary>
    public long CacheHitCount => Interlocked.Read(ref _cacheHits);

    /// <summary>Number of requests abandoned or discarded because a newer request for the same locator started.</summary>
    public long SupersededCount => Interlocked.Read(ref _superseded);

    /// <summary>Number of resolutions skipped because of <see cref="OcrTextLocator.MinInterval"/>.</summary>
    public long RateLimitedCount => Interlocked.Read(ref _rateLimited);

    /// <summary>Drops every cached OCR result.</summary>
    public void ClearCache() => _cache.Clear();

    /// <inheritdoc />
    public bool CanResolve(ElementLocator locator) => locator is OcrTextLocator;

    /// <inheritdoc />
    public async ValueTask<LocatorResolution> ResolveAsync(ElementLocator locator, LocatorContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(locator);
        ArgumentNullException.ThrowIfNull(context);
        if (locator is not OcrTextLocator ocr)
            return LocatorResolution.Failed(locator, LocatorStatus.Unsupported, $"OcrLocatorResolver cannot resolve '{locator.Kind}' locators.");
        cancellationToken.ThrowIfCancellationRequested();
        if (_disposed) return LocatorResolution.Failed(ocr, LocatorStatus.Unavailable, "The OCR resolver has been disposed.");

        var window = context.Window;
        var time = context.Time;
        long now = time.GetTimestamp();

        // 1. Rate limit: same locator, same window size and DPI, within MinInterval -> keep the previous result.
        if (context.Previous is { } previous && previous.State is OcrResolutionState prev && Equals(prev.Locator, ocr)
            && ocr.MinInterval > TimeSpan.Zero && time.GetElapsedTime(prev.Timestamp, now) < ocr.MinInterval
            && prev.ClientSize == window.ClientBounds.Size && prev.Dpi == window.Dpi)
        {
            Interlocked.Increment(ref _rateLimited);
            return previous.IsResolved ? CachedFrom(previous, window) : previous;
        }

        // 2. Newest request wins for this window + locator.
        var key = new RequestKey(window.Handle, ocr);
        var request = new Request();
        _latest.AddOrUpdate(key, request, (_, old) => { old.Supersede(); return request; });
        try
        {
            return await ResolveCoreAsync(ocr, context, request, now, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _latest.TryRemove(new KeyValuePair<RequestKey, Request>(key, request));
            request.Dispose();
        }
    }

    private async ValueTask<LocatorResolution> ResolveCoreAsync(OcrTextLocator ocr, LocatorContext context, Request request, long now, CancellationToken ct)
    {
        var window = context.Window;
        var logger = context.Logger;
        var state = new OcrResolutionState(ocr, now, window.ClientBounds.Size, window.Dpi);

        // 3. Capture.
        if (context.Capture is not { } capture)
            return Fail(ocr, window, state, LocatorStatus.Unavailable, "No capture backend is registered (IScreenCapture); OCR locators need one.");
        var client = window.ClientBounds;
        if (client.IsEmpty || !window.IsPresentable)
            return Fail(ocr, window, state, LocatorStatus.Unavailable, "The target window has no visible client area to capture.");
        var region = client;
        if (ocr.SearchRegion is { } search)
        {
            region = CoordinateConverter.RelativeToScreen(search, client.Location).Intersect(client);
            if (region.IsEmpty)
                return Fail(ocr, window, state, LocatorStatus.Offscreen, $"The OCR search region {search} lies outside the {client.Width}x{client.Height} client area.");
        }

        var (frame, captureError) = await CaptureAsync(capture, window, region, ct).ConfigureAwait(false);
        if (frame is null)
            return Fail(ocr, window, state, LocatorStatus.Unavailable, captureError!);

        // 4. Content cache.
        var cacheKey = new CacheKey(Hash(frame.Image), frame.Image.Width, frame.Image.Height, _options.Recognition);
        bool cacheHit = _cache.TryGet(cacheKey, out var result);
        if (cacheHit)
        {
            Interlocked.Increment(ref _cacheHits);
        }
        else
        {
            if (request.IsSuperseded) return Superseded(ocr, context);
            // 5. Bounded queue + concurrency limit; abandon the wait when a newer request for the same locator arrives.
            if (Interlocked.Increment(ref _queued) > _options.MaxQueuedRecognitions + _options.MaxConcurrentRecognitions)
            {
                Interlocked.Decrement(ref _queued);
                return context.Previous is { IsResolved: true } busyPrev
                    ? CachedFrom(busyPrev, window)
                    : Fail(ocr, window, state, LocatorStatus.Unavailable, "OCR is busy: too many queued recognitions.");
            }
            bool acquired = false;
            try
            {
                using (var waitCts = CancellationTokenSource.CreateLinkedTokenSource(ct, request.Token))
                {
                    try
                    {
                        await _slots.WaitAsync(waitCts.Token).ConfigureAwait(false);
                        acquired = true;
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        return Superseded(ocr, context);
                    }
                }
                if (request.IsSuperseded) return Superseded(ocr, context);
                if (_cache.TryGet(cacheKey, out result))
                {
                    cacheHit = true;
                    Interlocked.Increment(ref _cacheHits);
                }
                else
                {
                    Interlocked.Increment(ref _inferences);
                    try
                    {
                        result = await _provider.RecognizeAsync(frame.Image, _options.Recognition, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "OCR provider {Provider} threw while resolving {Query}", _provider.Name, ocr.Query);
                        return Fail(ocr, window, state, LocatorStatus.Error, $"OCR provider '{_provider.Name}' failed: {ex.Message}", ex);
                    }
                    if (result is null)
                        return Fail(ocr, window, state, LocatorStatus.Error, $"OCR provider '{_provider.Name}' returned no result.");
                    if (result.Succeeded) _cache.Set(cacheKey, result);
                }
            }
            finally
            {
                if (acquired) _slots.Release();
                Interlocked.Decrement(ref _queued);
            }
            // Stale-result rejection: a newer request for the same locator started while this one was running.
            if (request.IsSuperseded) return Superseded(ocr, context);
        }

        ct.ThrowIfCancellationRequested();
        if (!result.Succeeded)
        {
            var status = result.Status == OcrStatus.Failed ? LocatorStatus.Error : LocatorStatus.Unavailable;
            return Fail(ocr, window, state, status, $"OCR failed ({result.Status}): {result.Error}", result.Exception);
        }

        var resolution = Match(ocr, window, frame, result, state);
        logger.LogDebug("OCR {Query}: {Status}, {Regions} line(s), cache {CacheHit}, det {Det} ms, rec {Rec} ms",
            ocr.Query, resolution.Status, result.Regions.Count, cacheHit, result.DetectionTime.TotalMilliseconds, result.RecognitionTime.TotalMilliseconds);
        return resolution;
    }

    private async ValueTask<(CapturedFrame? Frame, string? Error)> CaptureAsync(IScreenCapture capture, TargetWindowInfo window, PixelRect region, CancellationToken ct)
    {
        string windowError;
        if (capture.Supports(CaptureSource.Window))
        {
            var r = await capture.CaptureAsync(new CaptureRequest(region, CaptureSource.Window) { Window = window }, ct).ConfigureAwait(false);
            if (r.Succeeded) return (r.Frame, null);
            windowError = $"Window capture failed ({r.Error}): {r.Message}";
        }
        else
        {
            windowError = "The capture backend does not support window capture.";
        }

        if (!_options.AllowDesktopCaptureFallback)
            return (null, windowError + " Desktop capture is not used because it would include WindowVeil's own overlays (set AllowDesktopCaptureFallback to override).");
        if (!capture.Supports(CaptureSource.Desktop))
            return (null, windowError + " Desktop capture is not supported either.");
        var d = await capture.CaptureAsync(new CaptureRequest(region, CaptureSource.Desktop) { Window = window }, ct).ConfigureAwait(false);
        return d.Succeeded ? (d.Frame, null) : (null, $"{windowError} Desktop capture failed ({d.Error}): {d.Message}");
    }

    private LocatorResolution Match(OcrTextLocator ocr, TargetWindowInfo window, CapturedFrame frame, OcrResult result, OcrResolutionState state)
    {
        var regions = result.Regions;
        var query = ocr.Query;
        var matched = _matcher.Match(regions, query, ocr.MinConfidence);
        if (!matched.Found)
        {
            if (matched.FoundOnlyBelowThreshold)
                return Fail(ocr, window, state, LocatorStatus.Rejected,
                    string.Create(CultureInfo.InvariantCulture, $"OCR confidence too low: best {matched.BestLowConfidence:0.000} < threshold {ocr.MinConfidence:0.000} for {query} ({matched.LowConfidenceMatches.Count} low-confidence match(es)).")
                    + Recognized(matched.LowConfidenceMatches));
            return Fail(ocr, window, state, LocatorStatus.NotFound,
                $"No OCR text matched {query} ({regions.Count} line(s) recognized).");
        }

        var used = ocr.AllMatches ? matched.Matches : [matched.Matches[0]];
        var (l, t, r, b) = CoordinateConverter.InsetsToPhysical(ocr.Expand, window.Dpi);
        var forbidden = ocr.MustNotOverlap.Select(q => (Query: q, Matches: _matcher.FindAll(regions, q))).ToArray();
        var bounds = new List<PixelRect>(used.Count);
        foreach (var m in used)
        {
            var expanded = frame.ImageToScreen(m.BoundingBox).Inflate(l, t, r, b);
            foreach (var (fq, fms) in forbidden)
            {
                foreach (var f in fms)
                {
                    if (!Conflicts(m, f, expanded, frame)) continue;
                    return Fail(ocr, window, state, LocatorStatus.Rejected,
                        $"The OCR region for {query} (expanded by {FormatInsets(ocr.Expand)} DIP) would overlap text matching {fq}; the expansion was rejected to avoid covering neighboring content."
                        + Recognized([f]));
                }
            }
            bounds.Add(expanded);
        }

        float confidence = used.Min(m => m.Confidence);
        var message = string.Create(CultureInfo.InvariantCulture,
            $"OCR matched {query}: {used.Count} match(es){(matched.Matches.Count > used.Count ? $" of {matched.Matches.Count}" : "")}, confidence {confidence:0.000}, {regions.Count} line(s) recognized by {_provider.Name}.")
            + Recognized(used);
        return LocatorResolution.Found(ocr, bounds, confidence, message) with { WindowBounds = window.Bounds, State = state };
    }

    /// <summary>
    /// Whether a forbidden match <paramref name="f"/> is covered by the expanded box of <paramref name="m"/>. Text that is part of
    /// the target match itself is never a conflict; other text inside the same detection is (with region geometry the whole
    /// detection is covered).
    /// </summary>
    private bool Conflicts(OcrMatch m, OcrMatch f, PixelRect expanded, CapturedFrame frame)
    {
        foreach (var (fr, fs, fe) in f.Spans)
        {
            foreach (var (mr, ms, me) in m.Spans)
            {
                if (!ReferenceEquals(fr, mr)) continue;
                bool sameText = fs < me && ms < fe;
                if (sameText) return false;
                if (_options.Matching.Geometry == OcrMatchGeometry.Region) return true;
            }
        }
        return expanded.IntersectsWith(frame.ImageToScreen(f.BoundingBox));
    }

    private string Recognized(IEnumerable<OcrMatch> matches) => _options.IncludeRecognizedTextInDiagnostics
        ? " Recognized: " + string.Join(", ", matches.Select(m => $"\"{m.Text}\""))
        : "";

    private static string FormatInsets(Insets i) => string.Create(CultureInfo.InvariantCulture, $"({i.Left:0.##}, {i.Top:0.##}, {i.Right:0.##}, {i.Bottom:0.##})");

    private static LocatorResolution CachedFrom(LocatorResolution previous, TargetWindowInfo window)
    {
        var moved = previous;
        if (previous.WindowBounds is { } wb && wb.Location != window.Bounds.Location)
            moved = previous.Translate(window.Bounds.X - wb.X, window.Bounds.Y - wb.Y);
        return moved with { Status = LocatorStatus.Cached, WindowBounds = window.Bounds };
    }

    private LocatorResolution Superseded(OcrTextLocator ocr, LocatorContext context)
    {
        Interlocked.Increment(ref _superseded);
        return context.Previous is { IsResolved: true } prev
            ? CachedFrom(prev, context.Window)
            : LocatorResolution.Failed(ocr, LocatorStatus.Unavailable, "Superseded by a newer OCR request for the same locator; this result was discarded.");
    }

    private static LocatorResolution Fail(OcrTextLocator ocr, TargetWindowInfo window, OcrResolutionState state, LocatorStatus status, string message, Exception? ex = null) =>
        LocatorResolution.Failed(ocr, status, message, ex) with { WindowBounds = window.Bounds, State = state };

    /// <summary>XxHash64 of the visible pixels (stride padding excluded).</summary>
    internal static ulong Hash(BgraImage image)
    {
        int row = image.Width * 4;
        if (image.Stride == row) return XxHash64.HashToUInt64(image.Pixels.AsSpan(0, row * image.Height));
        var h = new XxHash64();
        for (int y = 0; y < image.Height; y++) h.Append(image.Pixels.AsSpan(y * image.Stride, row));
        return h.GetCurrentHashAsUInt64();
    }

    /// <summary>Stops accepting work. In-flight requests complete; later calls return Unavailable.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var r in _latest.Values) r.Supersede();
        _cache.Clear();
    }

    private readonly record struct RequestKey(nint Window, OcrTextLocator Locator);

    private readonly record struct CacheKey(ulong Hash, int Width, int Height, OcrOptions Options);

    private sealed class Request : IDisposable
    {
        private readonly CancellationTokenSource _cts = new();
        private int _superseded;

        public CancellationToken Token => _cts.Token;

        public bool IsSuperseded => Volatile.Read(ref _superseded) != 0;

        public void Supersede()
        {
            if (Interlocked.Exchange(ref _superseded, 1) != 0) return;
            try { _cts.Cancel(); } catch (ObjectDisposedException) { }
        }

        public void Dispose() => _cts.Dispose();
    }
}

/// <summary>Resolver state stored in <see cref="LocatorResolution.State"/> (used for rate limiting).</summary>
/// <param name="Locator">The locator that produced the resolution.</param>
/// <param name="Timestamp">When the OCR pass was requested (<see cref="TimeProvider.GetTimestamp"/> of the context clock).</param>
/// <param name="ClientSize">Client size of the target window at that time.</param>
/// <param name="Dpi">DPI of the target window at that time.</param>
public sealed record OcrResolutionState(OcrTextLocator Locator, long Timestamp, PixelSize ClientSize, DpiScale Dpi);
