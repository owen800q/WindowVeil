using WindowVeil.Geometry;
using WindowVeil.Locators;
using WindowVeil.Platform;
using WindowVeil.Text;
using WindowVeil.UnitTests.Fakes;

namespace WindowVeil.UnitTests.Core;

public class WindowSelectorTests
{
    private static TargetWindowInfo Win(string process = "RetroCap", string title = "RetroCap", string cls = "Main", bool visible = true,
        bool minimized = false, bool foreground = false, int size = 100, nint handle = 1, int pid = 10) => new()
    {
        Handle = handle, ProcessId = pid, ProcessName = process, Title = title, ClassName = cls,
        Bounds = new PixelRect(0, 0, size, size), ClientBounds = new PixelRect(0, 0, size, size),
        IsVisible = visible, IsMinimized = minimized, IsForeground = foreground,
    };

    [Fact]
    public void ByProcessName_IsCaseInsensitiveAndStripsExe()
    {
        var s = WindowSelector.ByProcessName("retrocap.EXE");
        Assert.Equal("retrocap", s.ProcessName);
        Assert.True(s.Matches(Win()));
        Assert.False(s.Matches(Win(process: "RetroCapHelper")));
        Assert.Throws<ArgumentException>(() => WindowSelector.ByProcessName(" "));
    }

    [Fact]
    public void Conditions_AllMustMatch()
    {
        var s = WindowSelector.ByProcessName("RetroCap").WithTitle("settings").WithClassName("Main") with { TitlePattern = @"^Retro\w+ - " };
        Assert.True(s.Matches(Win(title: "RetroCap - Settings")));
        Assert.False(s.Matches(Win(title: "RetroCap - Settings", cls: "main"))); // class name is case-sensitive
        Assert.False(s.Matches(Win(title: "Settings")));                         // pattern fails
        Assert.False(s.Matches(Win(title: "RetroCap - About")));                  // contains fails
    }

    [Fact]
    public void VisibilityAndTitleRequirements()
    {
        var s = WindowSelector.ByProcessName("RetroCap");
        Assert.False(s.Matches(Win(visible: false)));
        Assert.False(s.Matches(Win(size: 0)));
        Assert.False(s.Matches(Win(title: " ")));
        Assert.True((s with { RequireVisible = false, RequireTitle = false }).Matches(Win(visible: false, title: "")));
    }

    [Fact]
    public void HandleProcessIdAndPredicate()
    {
        Assert.True(WindowSelector.ByHandle(7).Matches(Win(handle: 7, visible: false, title: "")));
        Assert.False(WindowSelector.ByHandle(7).Matches(Win(handle: 8)));
        Assert.True(WindowSelector.ByProcessId(10).Matches(Win()));
        Assert.False(WindowSelector.ByProcessId(11).Matches(Win()));
        Assert.False((WindowSelector.ByTitle("Retro") with { Predicate = w => w.Bounds.Width > 500 }).Matches(Win()));
    }

    [Fact]
    public void SelectBest_PrefersForegroundThenRestoredThenLargest()
    {
        var s = WindowSelector.ByProcessName("RetroCap");
        var small = Win(size: 50, handle: 1);
        var large = Win(size: 500, handle: 2);
        var minimizedHuge = Win(size: 900, minimized: true, handle: 3);
        var foregroundSmall = Win(size: 20, foreground: true, handle: 4);
        var other = Win(process: "Other", size: 2000, foreground: true, handle: 5);

        Assert.Equal(large, s.SelectBest([small, large, minimizedHuge, other]));
        Assert.Equal(foregroundSmall, s.SelectBest([small, large, minimizedHuge, foregroundSmall, other]));
        Assert.Null(s.SelectBest([other]));
    }

    [Fact]
    public void ToString_DescribesConditions()
    {
        var s = WindowSelector.ByProcessName("RetroCap").WithTitle("Settings") with { TitlePattern = "x", ClassName = "C" };
        Assert.Equal("Window(process=RetroCap, title~'Settings', title=/x/, class=C)", s.ToString());
    }
}

public class LocatorPipelineTests
{
    private sealed record TestLocator(string Name) : ElementLocator
    {
        public override string Kind => "test";
    }

    private sealed record OtherLocator : ElementLocator
    {
        public override string Kind => "other";
    }

    /// <summary>Resolver driven by a function; records the contexts it saw.</summary>
    private sealed class FuncResolver(Func<TestLocator, LocatorContext, CancellationToken, ValueTask<LocatorResolution>> resolve) : ILocatorResolver
    {
        public List<(string Name, LocatorContext Context)> Calls { get; } = [];

        public bool CanResolve(ElementLocator locator) => locator is TestLocator;

        public ValueTask<LocatorResolution> ResolveAsync(ElementLocator locator, LocatorContext context, CancellationToken cancellationToken)
        {
            var l = (TestLocator)locator;
            lock (Calls) Calls.Add((l.Name, context));
            return resolve(l, context, cancellationToken);
        }
    }

    private static readonly LocatorContext Context = new(new TargetWindowInfo
    {
        Handle = 1, ProcessId = 1, Bounds = new PixelRect(0, 0, 100, 100), ClientBounds = new PixelRect(0, 0, 100, 100),
    }, ResolveReason.Initial);

    private static FuncResolver ByName(Dictionary<string, LocatorStatus> outcomes) => new((l, _, _) =>
    {
        var status = outcomes[l.Name];
        return ValueTask.FromResult(status == LocatorStatus.Resolved
            ? new LocatorResolution { Status = LocatorStatus.Resolved, Bounds = [new PixelRect(1, 2, 3, 4)] } // ResolvedBy omitted on purpose
            : LocatorResolution.Failed(l, status, $"{l.Name} {status}"));
    });

    [Fact]
    public async Task UnsupportedKind_ReturnsUnsupportedWithoutThrowing()
    {
        var pipeline = new LocatorPipeline([ByName([])]);

        var r = await pipeline.ResolveAsync(new OtherLocator(), Context, default);

        Assert.Equal(LocatorStatus.Unsupported, r.Status);
        Assert.Contains("other", r.Message, StringComparison.Ordinal);
        Assert.False(r.IsResolved);
        Assert.Empty(r.Bounds);
        Assert.False(pipeline.Supports(new OtherLocator()));
        Assert.True(pipeline.Supports(new FallbackLocator([new OtherLocator(), new TestLocator("a")])));
    }

    [Fact]
    public async Task ResolverException_BecomesErrorWithException()
    {
        var pipeline = new LocatorPipeline([new FuncResolver((_, _, _) => throw new InvalidOperationException("kaboom"))]);

        var r = await pipeline.ResolveAsync(new TestLocator("a"), Context, default);

        Assert.Equal(LocatorStatus.Error, r.Status);
        Assert.IsType<InvalidOperationException>(r.Exception);
        Assert.Contains("kaboom", r.Message, StringComparison.Ordinal);
        Assert.Equal(new TestLocator("a"), r.ResolvedBy);
    }

    [Fact]
    public async Task Success_FillsResolvedByWhenResolverOmitsIt()
    {
        var pipeline = new LocatorPipeline([ByName(new() { ["a"] = LocatorStatus.Resolved })]);

        var r = await pipeline.ResolveAsync(new TestLocator("a"), Context, default);

        Assert.True(r.IsResolved);
        Assert.Equal(new TestLocator("a"), r.ResolvedBy);
    }

    [Fact]
    public async Task Fallback_PicksFirstResolved_StopsThere_AndRecordsAttempts()
    {
        var resolver = ByName(new() { ["a"] = LocatorStatus.NotFound, ["b"] = LocatorStatus.Resolved, ["c"] = LocatorStatus.Resolved });
        var pipeline = new LocatorPipeline([resolver]);

        var r = await pipeline.ResolveAsync(ElementLocator.FirstOf(new TestLocator("a"), new TestLocator("b"), new TestLocator("c")), Context, default);

        Assert.True(r.IsResolved);
        Assert.Equal(new TestLocator("b"), r.ResolvedBy);
        Assert.Equal([LocatorStatus.NotFound, LocatorStatus.Resolved], r.Attempts.Select(a => a.Status));
        Assert.Equal(["a", "b"], resolver.Calls.Select(c => c.Name));
    }

    [Fact]
    public async Task Fallback_AllFail_ReportsMostSevereFailure()
    {
        var pipeline = new LocatorPipeline([ByName(new()
        {
            ["nf"] = LocatorStatus.NotFound, ["rej"] = LocatorStatus.Rejected, ["un"] = LocatorStatus.Unavailable, ["off"] = LocatorStatus.Offscreen,
        })]);
        var chain = ElementLocator.FirstOf(new TestLocator("nf"), new TestLocator("un"), new TestLocator("rej"), new TestLocator("off"), new OtherLocator());

        var r = await pipeline.ResolveAsync(chain, Context, default);

        Assert.Equal(LocatorStatus.Rejected, r.Status);
        Assert.Equal(chain, r.ResolvedBy);
        Assert.Equal(5, r.Attempts.Count);
        Assert.StartsWith("All strategies failed: test: NotFound (nf NotFound); test: Unavailable", r.Message, StringComparison.Ordinal);
        Assert.Contains("other: Unsupported", r.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Fallback_ErrorOutranksEverything_ContinueOnErrorTriesNext()
    {
        var resolver = new FuncResolver((l, _, _) => l.Name == "boom"
            ? throw new InvalidOperationException("x")
            : ValueTask.FromResult(LocatorResolution.Failed(l, LocatorStatus.Rejected, "r")));
        var pipeline = new LocatorPipeline([resolver]);

        var r = await pipeline.ResolveAsync(ElementLocator.FirstOf(new TestLocator("boom"), new TestLocator("rej")), Context, default);

        Assert.Equal(LocatorStatus.Error, r.Status);
        Assert.IsType<InvalidOperationException>(r.Exception);
        Assert.Equal(2, resolver.Calls.Count);
    }

    [Fact]
    public async Task Fallback_ContinueOnErrorFalse_StopsAtErrorButNotAtNotFound()
    {
        var resolver = new FuncResolver((l, _, _) => l.Name switch
        {
            "boom" => throw new InvalidOperationException("x"),
            "nf" => ValueTask.FromResult(LocatorResolution.Failed(l, LocatorStatus.NotFound, "nf")),
            _ => ValueTask.FromResult(LocatorResolution.Found(l, new PixelRect(0, 0, 1, 1))),
        });
        var pipeline = new LocatorPipeline([resolver]);

        var stopped = await pipeline.ResolveAsync(new FallbackLocator([new TestLocator("boom"), new TestLocator("ok")]) { ContinueOnError = false }, Context, default);
        Assert.Equal(LocatorStatus.Error, stopped.Status);
        Assert.Equal(["boom"], resolver.Calls.Select(c => c.Name));

        var continued = await pipeline.ResolveAsync(new FallbackLocator([new TestLocator("nf"), new TestLocator("ok")]) { ContinueOnError = false }, Context, default);
        Assert.True(continued.IsResolved);
    }

    [Fact]
    public async Task Cancellation_PropagatesInsteadOfBecomingError()
    {
        var pipeline = new LocatorPipeline([new FuncResolver(async (_, _, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("unreachable");
        })]);
        using var cts = new CancellationTokenSource(50);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await pipeline.ResolveAsync(ElementLocator.FirstOf(new TestLocator("a"), new TestLocator("b")), Context, cts.Token));
    }

    [Fact]
    public async Task UnrelatedOperationCanceled_IsReportedAsError()
    {
        var pipeline = new LocatorPipeline([new FuncResolver((_, _, _) => throw new OperationCanceledException("internal"))]);

        var r = await pipeline.ResolveAsync(new TestLocator("a"), Context, CancellationToken.None);

        Assert.Equal(LocatorStatus.Error, r.Status);
    }

    [Fact]
    public async Task Fallback_PassesEachStrategyItsOwnPreviousAttempt()
    {
        var resolver = ByName(new() { ["a"] = LocatorStatus.NotFound, ["b"] = LocatorStatus.Resolved });
        var pipeline = new LocatorPipeline([resolver]);
        var chain = ElementLocator.FirstOf(new TestLocator("a"), new TestLocator("b"));
        var first = await pipeline.ResolveAsync(chain, Context, default);
        resolver.Calls.Clear();

        await pipeline.ResolveAsync(chain, new LocatorContext(Context.Window, ResolveReason.Poll) { Previous = first }, default);

        Assert.Same(first.Attempts[0], resolver.Calls[0].Context.Previous);
        Assert.Same(first, resolver.Calls[1].Context.Previous); // the previous winner gets the full previous resolution
        Assert.All(resolver.Calls, c => Assert.Equal(ResolveReason.Poll, c.Context.Reason));
    }
}

public class GeometricLocatorResolverTests
{
    private static readonly TargetWindowInfo Window = new()
    {
        Handle = 1, ProcessId = 1,
        Bounds = new PixelRect(-1000, 200, 800, 600),
        ClientBounds = new PixelRect(-992, 231, 784, 561),
    };

    private static async Task<LocatorResolution> Resolve(ElementLocator l) =>
        await new GeometricLocatorResolver().ResolveAsync(l, new LocatorContext(Window, ResolveReason.Initial), default);

    [Fact]
    public async Task Rectangle_IsAbsolute()
    {
        var r = await Resolve(ElementLocator.Rectangle(-50, -60, 10, 20));
        Assert.Equal([new PixelRect(-50, -60, 10, 20)], r.Bounds);
        Assert.Equal(Window.Bounds, r.WindowBounds);
        Assert.False(ElementLocator.Rectangle(0, 0, 1, 1).IsWindowDependent);
    }

    [Fact]
    public async Task WindowRelative_ClientVersusWindowAnchor()
    {
        Assert.Equal([new PixelRect(-982, 251, 30, 40)], (await Resolve(ElementLocator.WindowRelative(10, 20, 30, 40))).Bounds);
        Assert.Equal([new PixelRect(-990, 220, 30, 40)], (await Resolve(ElementLocator.WindowRelative(10, 20, 30, 40, WindowAnchor.Window))).Bounds);
    }

    [Fact]
    public async Task Normalized_ScalesWithAnchor()
    {
        Assert.Equal([new PixelRect(-600, 350, 400, 150)], (await Resolve(ElementLocator.Normalized(0.5, 0.25, 0.5, 0.25, WindowAnchor.Window))).Bounds);
        Assert.Equal([Window.Bounds], (await Resolve(ElementLocator.Window())).Bounds);
        Assert.Equal([Window.ClientBounds], (await Resolve(ElementLocator.Window(WindowAnchor.Client))).Bounds);
    }

    [Fact]
    public async Task EmptyAndUnsupported_AreExplicitFailures()
    {
        var empty = await Resolve(ElementLocator.Rectangle(0, 0, 0, 10));
        Assert.Equal(LocatorStatus.Rejected, empty.Status);
        Assert.Empty(empty.Bounds);
        var unsupported = await Resolve(ElementLocator.ByAutomationId("x"));
        Assert.Equal(LocatorStatus.Unsupported, unsupported.Status);
        Assert.False(new GeometricLocatorResolver().CanResolve(ElementLocator.ByAutomationId("x")));
    }

    [Fact]
    public async Task EngineAlwaysIncludesGeometricResolver()
    {
        await using var h = new EngineHarness();
        Assert.Contains(h.Engine.Locators.Resolvers, r => r is GeometricLocatorResolver);
        var r = await h.Engine.ResolveAsync(Window, ElementLocator.WindowRelative(1, 1, 5, 5, WindowAnchor.Window));
        Assert.Equal([new PixelRect(-999, 201, 5, 5)], r.Bounds);
    }
}

public class LocatorModelTests
{
    [Fact]
    public void Smart_BuildsChainInDocumentedOrder()
    {
        var chain = ElementLocator.Smart(automationId: "id", name: "Name", text: "t", ocrText: "o", windowRelativeFallback: new PixelRect(1, 2, 3, 4));
        Assert.Equal(["uiAutomation", "uiAutomation", "text", "ocrText", "windowRelative"], chain.Locators.Select(l => l.Kind));
        Assert.Throws<ArgumentException>(() => ElementLocator.Smart());
    }

    [Fact]
    public void Or_FlattensChains()
    {
        var a = ElementLocator.ByAutomationId("a");
        var chain = a.Or(ElementLocator.ByName("b")).Or(ElementLocator.FirstOf(ElementLocator.ByText("c"), ElementLocator.ByOcrText("d")));
        Assert.Equal(4, chain.Locators.Count);
        Assert.Throws<ArgumentException>(() => new FallbackLocator([]));
    }

    [Fact]
    public void Locators_HaveValueEquality()
    {
        Assert.Equal(ElementLocator.FirstOf(ElementLocator.ByAutomationId("a"), ElementLocator.ByName("b", TextMatch.Regex)),
                     ElementLocator.FirstOf(ElementLocator.ByAutomationId("a"), ElementLocator.ByName("b", TextMatch.Regex)));
        Assert.NotEqual(ElementLocator.FirstOf(ElementLocator.ByAutomationId("a")),
                        new FallbackLocator([ElementLocator.ByAutomationId("a")]) { ContinueOnError = false });

        OcrTextLocator Ocr() => ElementLocator.ByOcrText("Start automatically").Expanded(new Insets(20, 2, 2, 2), TextQuery.Containing("Minimize"), TextQuery.Matching("^Close$"));
        Assert.Equal(Ocr(), Ocr());
        Assert.Equal(Ocr().GetHashCode(), Ocr().GetHashCode());
        Assert.NotEqual(Ocr(), Ocr() with { MustNotOverlap = [TextQuery.Containing("Minimize")] });
        Assert.Equal(ElementLocator.ByText("x") with { Scope = ElementLocator.ByControlType("Pane") }, ElementLocator.ByText("x") with { Scope = ElementLocator.ByControlType("Pane") });
    }

    [Fact]
    public void UiAutomationLocator_DescribesItself()
    {
        var l = ElementLocator.ByAutomationId("StartupCheck").WithControlType("CheckBox").At(1).Within(ElementLocator.ByName("Settings"));
        Assert.Equal("UIA(Name Exact:\"Settings\") > UIA(AutomationId='StartupCheck', ControlType=CheckBox, Index=1)", l.ToString());
        Assert.True(new UiAutomationLocator().IsEmpty);
    }

    [Fact]
    public void LocatorResolution_FactoriesAndTranslate()
    {
        var l = ElementLocator.Rectangle(0, 0, 1, 1);
        var empty = LocatorResolution.Found(l, [PixelRect.Empty]);
        Assert.Equal(LocatorStatus.Offscreen, empty.Status);
        Assert.Throws<ArgumentException>(() => LocatorResolution.Failed(l, LocatorStatus.Resolved, "no"));
        var found = LocatorResolution.Found(l, [new PixelRect(0, 0, 2, 2), new PixelRect(5, 5, 1, 1)]) with { WindowBounds = new PixelRect(0, 0, 10, 10) };
        var moved = found.Translate(-3, 4);
        Assert.Equal([new PixelRect(-3, 4, 2, 2), new PixelRect(2, 9, 1, 1)], moved.Bounds);
        Assert.Equal(new PixelRect(-3, 4, 10, 10), moved.WindowBounds);
        Assert.Equal(new PixelRect(-3, 4, 6, 6), moved.UnionBounds);
        Assert.True(new LocatorResolution { Status = LocatorStatus.Cached, Bounds = [new PixelRect(0, 0, 1, 1)] }.IsResolved);
    }
}
