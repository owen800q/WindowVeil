using WindowVeil.Geometry;
using WindowVeil.Imaging;
using WindowVeil.Locators;
using WindowVeil.Ocr;
using WindowVeil.Platform;
using WindowVeil.Text;
using Xunit;

namespace WindowVeil.OcrTests;

public class OcrLocatorResolverTests
{
    private const string Secret = "Account Balance $12,345.67";

    private static LocatorContext Context(TargetWindowInfo window, IScreenCapture? capture, TimeProvider? time = null, LocatorResolution? previous = null) =>
        new(window, ResolveReason.Poll) { Capture = capture, Time = time ?? new ManualTime(), Previous = previous };

    private static FakeOcrProvider ProviderWith(params OcrTextRegion[] regions) => new() { Regions = _ => regions };

    [Fact]
    public async Task NoCaptureBackend_IsUnavailable()
    {
        var resolver = new OcrLocatorResolver(ProviderWith());
        var r = await resolver.ResolveAsync(ElementLocator.ByOcrText("x"), Context(Windows.At(0, 0), null), default);
        Assert.Equal(LocatorStatus.Unavailable, r.Status);
        Assert.Contains("No capture backend", r.Message);
    }

    [Fact]
    public async Task WindowCaptureUnsupported_IsUnavailable_AndDesktopIsNotUsedByDefault()
    {
        var capture = new FakeCapture { SupportsWindow = false };
        var provider = ProviderWith(OcrTextRegion.FromRect("hello", 0.9f, new RectD(1, 1, 10, 5)));
        var r = await new OcrLocatorResolver(provider).ResolveAsync(ElementLocator.ByOcrText("hello"), Context(Windows.At(0, 0), capture), default);
        Assert.Equal(LocatorStatus.Unavailable, r.Status);
        Assert.Contains("overlays", r.Message);
        Assert.Empty(capture.Requests);
        Assert.Equal(0, provider.Calls);
    }

    [Fact]
    public async Task WindowCaptureFailure_ReportsTheCaptureError()
    {
        var capture = new FakeCapture { WindowError = CaptureError.AccessDenied };
        var r = await new OcrLocatorResolver(ProviderWith()).ResolveAsync(ElementLocator.ByOcrText("x"), Context(Windows.At(0, 0), capture), default);
        Assert.Equal(LocatorStatus.Unavailable, r.Status);
        Assert.Contains("AccessDenied", r.Message);
        Assert.All(capture.Requests, req => Assert.Equal(CaptureSource.Window, req.Source));
    }

    [Fact]
    public async Task DesktopFallback_IsUsedOnlyWhenAllowed()
    {
        var capture = new FakeCapture { WindowError = CaptureError.Unsupported };
        var provider = ProviderWith(OcrTextRegion.FromRect("hello", 0.9f, new RectD(1, 1, 10, 5)));
        var resolver = new OcrLocatorResolver(provider, new OcrLocatorOptions { AllowDesktopCaptureFallback = true });
        var r = await resolver.ResolveAsync(ElementLocator.ByOcrText("hello"), Context(Windows.At(0, 0), capture), default);
        Assert.Equal(LocatorStatus.Resolved, r.Status);
        Assert.Equal([CaptureSource.Window, CaptureSource.Desktop], capture.Requests.Select(q => q.Source));
    }

    [Fact]
    public async Task CapturesTheClientArea_WithTheTargetWindow()
    {
        var capture = new FakeCapture();
        var window = Windows.At(300, 400);
        await new OcrLocatorResolver(ProviderWith()).ResolveAsync(ElementLocator.ByOcrText("x"), Context(window, capture), default);
        var req = Assert.Single(capture.Requests);
        Assert.Equal(CaptureSource.Window, req.Source);
        Assert.Equal(window.ClientBounds, req.Region);
        Assert.Same(window, req.Window);
    }

    [Fact]
    public async Task SearchRegion_IsClientRelative_AndOutsideIsOffscreen()
    {
        var capture = new FakeCapture();
        var window = Windows.At(300, 400);
        var resolver = new OcrLocatorResolver(ProviderWith());
        await resolver.ResolveAsync(ElementLocator.ByOcrText("x") with { SearchRegion = new PixelRect(10, 20, 100, 30) }, Context(window, capture), default);
        Assert.Equal(new PixelRect(310, 420, 100, 30), capture.Requests[^1].Region);

        var r = await resolver.ResolveAsync(ElementLocator.ByOcrText("x") with { SearchRegion = new PixelRect(5000, 0, 10, 10) }, Context(window, capture), default);
        Assert.Equal(LocatorStatus.Offscreen, r.Status);
    }

    [Fact]
    public async Task MapsImageCoordinatesToScreen_WithNegativeOriginAndScale()
    {
        // Capture of a client area on a monitor left of the primary one, returned at half resolution (2 screen px per image px).
        var window = Windows.At(-1820, -40, 400, 200);
        var capture = new FakeCapture
        {
            ImageFactory = r => new BgraImage(r.Region.Width / 2, r.Region.Height / 2),
            ScreenRegionOverride = (r, _) => r.Region,
        };
        var provider = ProviderWith(OcrTextRegion.FromRect(Secret, 0.95f, new RectD(10, 5, 20.5, 8)));
        var r = await new OcrLocatorResolver(provider).ResolveAsync(ElementLocator.ByOcrText("balance"), Context(window, capture), default);
        Assert.Equal(LocatorStatus.Resolved, r.Status);
        // x: -1820 + 10*2 = -1800, width 41 -> outward rounding; y: -40 + 5*2 = -30, height 16.
        Assert.Equal(new PixelRect(-1800, -30, 41, 16), Assert.Single(r.Bounds));
        Assert.Equal(window.Bounds, r.WindowBounds);
        Assert.Equal(0.95, r.Confidence, 3);
    }

    [Fact]
    public async Task LowConfidence_IsRejected_NotNotFound_AndMessagesDoNotLeakText()
    {
        var provider = ProviderWith(OcrTextRegion.FromRect(Secret, 0.42f, new RectD(1, 1, 50, 10)));
        var resolver = new OcrLocatorResolver(provider);
        var locator = ElementLocator.ByOcrText("$12,345") with { MinConfidence = 0.6 };
        var r = await resolver.ResolveAsync(locator, Context(Windows.At(0, 0), new FakeCapture()), default);
        Assert.Equal(LocatorStatus.Rejected, r.Status);
        Assert.Contains("OCR confidence too low: best 0.420 < threshold 0.600", r.Message);
        Assert.DoesNotContain("Account", r.Message);
        Assert.Empty(r.Bounds);

        var notFound = await resolver.ResolveAsync(ElementLocator.ByOcrText("Password"), Context(Windows.At(0, 0), new FakeCapture()), default);
        Assert.Equal(LocatorStatus.NotFound, notFound.Status);
        Assert.DoesNotContain("Account", notFound.Message);
        Assert.Contains("1 line(s) recognized", notFound.Message);
    }

    [Fact]
    public async Task RecognizedText_AppearsInDiagnostics_OnlyWhenEnabled()
    {
        var provider = ProviderWith(OcrTextRegion.FromRect(Secret, 0.9f, new RectD(1, 1, 50, 10)));
        var quiet = await new OcrLocatorResolver(provider).ResolveAsync(ElementLocator.ByOcrText("balance"), Context(Windows.At(0, 0), new FakeCapture()), default);
        Assert.DoesNotContain("12,345", quiet.Message);
        Assert.Contains("balance", quiet.Message); // the query pattern itself is fine

        var verbose = await new OcrLocatorResolver(provider, new OcrLocatorOptions { IncludeRecognizedTextInDiagnostics = true })
            .ResolveAsync(ElementLocator.ByOcrText("balance"), Context(Windows.At(0, 0), new FakeCapture()), default);
        Assert.Contains("Balance", verbose.Message);
    }

    [Fact]
    public async Task Expand_IsConvertedFromDipsWithWindowDpi()
    {
        var provider = ProviderWith(OcrTextRegion.FromRect("Start hidden", 0.9f, new RectD(40, 10, 100, 20)));
        var window = Windows.At(1000, 500, dpiPercent: 150);
        var locator = ElementLocator.ByOcrText("Start hidden").Expanded(new Insets(20, 2, 4, 0));
        var r = await new OcrLocatorResolver(provider).ResolveAsync(locator, Context(window, new FakeCapture()), default);
        // 20 DIP = 30 px, 2 DIP = 3 px, 4 DIP = 6 px at 150 %.
        Assert.Equal(PixelRect.FromLTRB(1000 + 40 - 30, 500 + 10 - 3, 1000 + 140 + 6, 500 + 30), Assert.Single(r.Bounds));
    }

    [Fact]
    public async Task ExpansionOverlappingForbiddenText_IsRejected()
    {
        var provider = ProviderWith(
            OcrTextRegion.FromRect("Start automatically when I sign in", 0.95f, new RectD(40, 20, 300, 18)),
            OcrTextRegion.FromRect("Start hidden in the notification area", 0.95f, new RectD(40, 58, 260, 18)));
        var resolver = new OcrLocatorResolver(provider);
        var forbid = new TextQuery("Start hidden");

        var ok = await resolver.ResolveAsync(ElementLocator.ByOcrText("automatically").Expanded(new Insets(26, 2, 2, 2), forbid),
            Context(Windows.At(0, 0), new FakeCapture()), default);
        Assert.Equal(LocatorStatus.Resolved, ok.Status);

        var tooBig = await resolver.ResolveAsync(ElementLocator.ByOcrText("automatically").Expanded(new Insets(26, 2, 2, 30), forbid),
            Context(Windows.At(0, 0), new FakeCapture()), default);
        Assert.Equal(LocatorStatus.Rejected, tooBig.Status);
        Assert.Contains("would overlap text matching", tooBig.Message);
        Assert.DoesNotContain("notification", tooBig.Message);
    }

    [Fact]
    public async Task ForbiddenTextThatIsTheTargetItself_IsNotAConflict_ButOtherTextInTheSameLineIs()
    {
        var provider = ProviderWith(OcrTextRegion.FromRect(Secret, 0.95f, new RectD(10, 10, 260, 20)));
        var resolver = new OcrLocatorResolver(provider);
        // "Start"-style overlap with itself: the forbidden query matches the same characters as the target.
        var self = await resolver.ResolveAsync(ElementLocator.ByOcrText("$12,345.67").Expanded(Insets.Zero, new TextQuery("12,345")),
            Context(Windows.At(0, 0), new FakeCapture()), default);
        Assert.Equal(LocatorStatus.Resolved, self.Status);
        // With region geometry the whole line is covered, so other text in the same detection is a conflict.
        var other = await resolver.ResolveAsync(ElementLocator.ByOcrText("$12,345.67").Expanded(Insets.Zero, new TextQuery("Account")),
            Context(Windows.At(0, 0), new FakeCapture()), default);
        Assert.Equal(LocatorStatus.Rejected, other.Status);
    }

    [Fact]
    public async Task IdenticalCapture_IsServedFromCache_WithoutInference()
    {
        var provider = ProviderWith(OcrTextRegion.FromRect("hello", 0.9f, new RectD(1, 1, 10, 5)));
        var capture = new FakeCapture { ImageFactory = r => Windows.Solid(r.Region.Width, r.Region.Height, 7) };
        var resolver = new OcrLocatorResolver(provider);
        var locator = ElementLocator.ByOcrText("hello");
        var a = await resolver.ResolveAsync(locator, Context(Windows.At(0, 0), capture), default);
        var b = await resolver.ResolveAsync(locator, Context(Windows.At(0, 0), capture), default);
        Assert.Equal(LocatorStatus.Resolved, a.Status);
        Assert.Equal(LocatorStatus.Resolved, b.Status);
        Assert.Equal(1, provider.Calls);
        Assert.Equal(1, resolver.CacheHitCount);
        Assert.Equal(2, capture.Requests.Count); // the unchanged window still costs a capture + hash

        capture.ImageFactory = r => Windows.Solid(r.Region.Width, r.Region.Height, 8);
        await resolver.ResolveAsync(locator, Context(Windows.At(0, 0), capture), default);
        Assert.Equal(2, provider.Calls);
    }

    [Fact]
    public async Task CacheIsBounded()
    {
        var provider = ProviderWith();
        byte seed = 0;
        var capture = new FakeCapture { ImageFactory = r => Windows.Solid(8, 8, seed) };
        var resolver = new OcrLocatorResolver(provider, new OcrLocatorOptions { ResultCacheCapacity = 2 });
        var locator = ElementLocator.ByOcrText("x");
        foreach (byte s in new byte[] { 1, 2, 3, 1 })
        {
            seed = s;
            await resolver.ResolveAsync(locator, Context(Windows.At(0, 0), capture), default);
        }
        Assert.Equal(4, provider.Calls); // 1 was evicted by 3
    }

    [Fact]
    public async Task WithinMinInterval_ReturnsCached_AndTranslatesOnMove()
    {
        var time = new ManualTime();
        var provider = ProviderWith(OcrTextRegion.FromRect("hello", 0.9f, new RectD(10, 10, 30, 10)));
        var capture = new FakeCapture();
        var resolver = new OcrLocatorResolver(provider);
        var locator = ElementLocator.ByOcrText("hello") with { MinInterval = TimeSpan.FromSeconds(1) };
        var first = await resolver.ResolveAsync(locator, Context(Windows.At(100, 100), capture, time), default);
        Assert.Equal(LocatorStatus.Resolved, first.Status);

        time.Advance(TimeSpan.FromMilliseconds(300));
        var moved = Windows.At(150, 120); // same size, moved by (50, 20)
        var second = await resolver.ResolveAsync(locator, Context(moved, capture, time, first), default);
        Assert.Equal(LocatorStatus.Cached, second.Status);
        Assert.True(second.IsResolved);
        Assert.Equal(first.Bounds[0].Offset(50, 20), second.Bounds[0]);
        Assert.Equal(moved.Bounds, second.WindowBounds);
        Assert.Single(capture.Requests);
        Assert.Equal(1, provider.Calls);

        time.Advance(TimeSpan.FromSeconds(1));
        var third = await resolver.ResolveAsync(locator, Context(moved, capture, time, second), default);
        Assert.Equal(LocatorStatus.Resolved, third.Status);
        Assert.Equal(2, capture.Requests.Count);
    }

    [Fact]
    public async Task ResizedWindow_BypassesMinInterval()
    {
        var time = new ManualTime();
        var capture = new FakeCapture();
        var resolver = new OcrLocatorResolver(ProviderWith(OcrTextRegion.FromRect("hello", 0.9f, new RectD(10, 10, 30, 10))));
        var locator = ElementLocator.ByOcrText("hello");
        var first = await resolver.ResolveAsync(locator, Context(Windows.At(100, 100), capture, time), default);
        var resized = await resolver.ResolveAsync(locator, Context(Windows.At(100, 100, width: 600), capture, time, first), default);
        Assert.Equal(LocatorStatus.Resolved, resized.Status);
        Assert.Equal(2, capture.Requests.Count);
    }

    [Fact]
    public async Task AtMostConfiguredInferencesRunConcurrently()
    {
        foreach (int limit in new[] { 1, 2 })
        {
            var provider = new FakeOcrProvider { Gate = _ => Task.Delay(40) };
            var counter = new int[1];
            var capture = new FakeCapture { ImageFactory = _ => Windows.Solid(4, 4, (byte)Interlocked.Increment(ref counter[0])) };
            var resolver = new OcrLocatorResolver(provider, new OcrLocatorOptions { MaxConcurrentRecognitions = limit });
            var tasks = Enumerable.Range(0, 6).Select(i => resolver.ResolveAsync(ElementLocator.ByOcrText("t" + i), Context(Windows.At(0, 0), capture), default).AsTask()).ToArray();
            await Task.WhenAll(tasks);
            Assert.Equal(6, provider.Calls);
            Assert.True(provider.MaxConcurrency <= limit, $"max concurrency {provider.MaxConcurrency} > {limit}");
        }
    }

    [Fact]
    public async Task NewerRequestForSameLocator_DiscardsTheOlderResult()
    {
        var release = new TaskCompletionSource();
        var provider = new FakeOcrProvider
        {
            Regions = _ => [OcrTextRegion.FromRect("hello", 0.9f, new RectD(1, 1, 10, 5))],
            Gate = call => call == 1 ? release.Task : Task.CompletedTask,
        };
        byte seed = 1;
        var capture = new FakeCapture { ImageFactory = _ => Windows.Solid(4, 4, seed++) };
        var resolver = new OcrLocatorResolver(provider, new OcrLocatorOptions { MaxConcurrentRecognitions = 2 });
        var locator = ElementLocator.ByOcrText("hello");
        var older = resolver.ResolveAsync(locator, Context(Windows.At(0, 0), capture), default).AsTask();
        await WaitUntil(() => provider.Calls == 1);
        var newer = await resolver.ResolveAsync(locator, Context(Windows.At(0, 0), capture), default);
        release.SetResult();
        var olderResult = await older;
        Assert.Equal(LocatorStatus.Resolved, newer.Status);
        Assert.Equal(LocatorStatus.Unavailable, olderResult.Status);
        Assert.Contains("Superseded", olderResult.Message);
        Assert.Equal(1, resolver.SupersededCount);
    }

    [Fact]
    public async Task QueuedRequestSupersededWhileWaiting_IsAbandonedWithoutInference()
    {
        var release = new TaskCompletionSource();
        var provider = new FakeOcrProvider { Gate = call => call == 1 ? release.Task : Task.CompletedTask };
        byte seed = 1;
        var capture = new FakeCapture { ImageFactory = _ => Windows.Solid(4, 4, seed++) };
        var resolver = new OcrLocatorResolver(provider); // one slot
        var busy = resolver.ResolveAsync(ElementLocator.ByOcrText("other"), Context(Windows.At(0, 0), capture), default).AsTask();
        await WaitUntil(() => provider.Calls == 1);
        var locator = ElementLocator.ByOcrText("hello");
        var waiting = resolver.ResolveAsync(locator, Context(Windows.At(0, 0), capture), default).AsTask();
        var newest = resolver.ResolveAsync(locator, Context(Windows.At(0, 0), capture), default).AsTask();
        var abandoned = await waiting.WaitAsync(TimeSpan.FromSeconds(5)); // returns before the slot frees up
        Assert.Contains("Superseded", abandoned.Message);
        release.SetResult();
        await Task.WhenAll(busy, newest);
        Assert.Equal(2, provider.Calls); // "other" + newest; the abandoned request never ran
    }

    [Fact]
    public async Task SupersededRequest_KeepsPreviousGeometryWhenThereIsOne()
    {
        var release = new TaskCompletionSource();
        var provider = new FakeOcrProvider
        {
            Regions = _ => [OcrTextRegion.FromRect("hello", 0.9f, new RectD(1, 1, 10, 5))],
            Gate = call => call == 2 ? release.Task : Task.CompletedTask,
        };
        byte seed = 1;
        var capture = new FakeCapture { ImageFactory = _ => Windows.Solid(4, 4, seed++) };
        var resolver = new OcrLocatorResolver(provider, new OcrLocatorOptions { MaxConcurrentRecognitions = 2 });
        var locator = ElementLocator.ByOcrText("hello") with { MinInterval = TimeSpan.Zero };
        var previous = await resolver.ResolveAsync(locator, Context(Windows.At(0, 0), capture), default);
        var older = resolver.ResolveAsync(locator, Context(Windows.At(0, 0), capture, previous: previous), default).AsTask();
        await WaitUntil(() => provider.Calls == 2);
        await resolver.ResolveAsync(locator, Context(Windows.At(0, 0), capture, previous: previous), default);
        release.SetResult();
        var r = await older;
        Assert.Equal(LocatorStatus.Cached, r.Status);
        Assert.Equal(previous.Bounds, r.Bounds);
    }

    [Fact]
    public async Task Cancellation_Propagates()
    {
        var resolver = new OcrLocatorResolver(new FakeOcrProvider { Gate = _ => Task.Delay(Timeout.Infinite) });
        using var pre = new CancellationTokenSource();
        pre.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => resolver.ResolveAsync(ElementLocator.ByOcrText("x"), Context(Windows.At(0, 0), new FakeCapture()), pre.Token).AsTask());

        using var during = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => resolver.ResolveAsync(ElementLocator.ByOcrText("x"), Context(Windows.At(0, 0), new FakeCapture()), during.Token).AsTask());

        // The slot was released: a later request still runs.
        var ok = new OcrLocatorResolver(ProviderWith());
        Assert.Equal(LocatorStatus.NotFound, (await ok.ResolveAsync(ElementLocator.ByOcrText("x"), Context(Windows.At(0, 0), new FakeCapture()), default)).Status);
    }

    [Theory]
    [InlineData(OcrStatus.ModelError, LocatorStatus.Unavailable)]
    [InlineData(OcrStatus.InvalidImage, LocatorStatus.Unavailable)]
    [InlineData(OcrStatus.Failed, LocatorStatus.Error)]
    public async Task ProviderFailures_MapToLocatorStatus(OcrStatus status, LocatorStatus expected)
    {
        var provider = new FakeOcrProvider { ResultOverride = img => OcrResult.Failure(status, "boom", img.Size) };
        var resolver = new OcrLocatorResolver(provider);
        var r = await resolver.ResolveAsync(ElementLocator.ByOcrText("x"), Context(Windows.At(0, 0), new FakeCapture()), default);
        Assert.Equal(expected, r.Status);
        Assert.Contains("boom", r.Message);
        // Failures are not cached.
        await resolver.ResolveAsync(ElementLocator.ByOcrText("x"), Context(Windows.At(0, 0), new FakeCapture()), default);
        Assert.Equal(2, provider.Calls);
    }

    [Fact]
    public async Task ProviderException_IsAnError()
    {
        var provider = new FakeOcrProvider { ResultOverride = _ => throw new InvalidOperationException("kaputt") };
        var r = await new OcrLocatorResolver(provider).ResolveAsync(ElementLocator.ByOcrText("x"), Context(Windows.At(0, 0), new FakeCapture()), default);
        Assert.Equal(LocatorStatus.Error, r.Status);
        Assert.IsType<InvalidOperationException>(r.Exception);
    }

    [Fact]
    public async Task AllMatchesFalse_ReturnsOnlyTheFirstMatch()
    {
        var provider = ProviderWith(
            OcrTextRegion.FromRect("Total 10", 0.9f, new RectD(10, 50, 60, 10)),
            OcrTextRegion.FromRect("Total 20", 0.9f, new RectD(10, 10, 60, 10)));
        var resolver = new OcrLocatorResolver(provider);
        var all = await resolver.ResolveAsync(ElementLocator.ByOcrText("total"), Context(Windows.At(0, 0), new FakeCapture()), default);
        Assert.Equal(2, all.Bounds.Count);
        var first = await resolver.ResolveAsync(ElementLocator.ByOcrText("total") with { AllMatches = false }, Context(Windows.At(0, 0), new FakeCapture()), default);
        Assert.Equal(new PixelRect(10, 10, 60, 10), Assert.Single(first.Bounds)); // reading order
    }

    [Fact]
    public async Task UseOcr_RegistersTheResolver_AndTheEngineResolvesThroughIt()
    {
        var window = Windows.At(200, 300);
        var provider = ProviderWith(OcrTextRegion.FromRect("hello", 0.9f, new RectD(5, 6, 20, 10)));
        await using var engine = WindowVeilEngine.CreateBuilder()
            .UseWindowSystem(new FakeWindowSystem(window))
            .UseCapture(new FakeCapture())
            .UseOcr(provider)
            .Build();
        Assert.Contains(engine.Locators.Resolvers, r => r is OcrLocatorResolver);
        var r = await engine.ResolveAsync(window, ElementLocator.ByOcrText("hello"));
        Assert.Equal(LocatorStatus.Resolved, r.Status);
        Assert.Equal(new PixelRect(205, 306, 20, 10), Assert.Single(r.Bounds));
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (int i = 0; i < 500 && !condition(); i++) await Task.Delay(10);
        Assert.True(condition(), "condition not reached");
    }
}
