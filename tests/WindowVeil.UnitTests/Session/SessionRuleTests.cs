using WindowVeil.Appearance;
using WindowVeil.Geometry;
using WindowVeil.Locators;
using WindowVeil.Platform;
using WindowVeil.Rendering;
using WindowVeil.UnitTests.Fakes;
using static WindowVeil.UnitTests.Fakes.EngineHarness;

namespace WindowVeil.UnitTests.Session;

public class SessionAttachTests
{
    [Fact]
    public async Task Attach_MatchingWindow_CreatesSessionInCreatedState()
    {
        await using var h = new EngineHarness();
        h.Windows.Add(processName: "Other", title: "Other");
        var w = h.Windows.Add();

        var session = await h.Engine.AttachAsync(WindowSelector.ByProcessName("RetroCap.exe"));

        Assert.Equal(SessionState.Created, session.State);
        Assert.Equal(w.Handle, session.Target!.Handle);
        Assert.Contains(session, h.Engine.Sessions);
        Assert.Equal(0, h.Events.ActiveCount); // not subscribed before StartAsync
    }

    [Fact]
    public async Task Attach_NoMatchingWindow_ThrowsTargetWindowNotFound()
    {
        await using var h = new EngineHarness();
        h.Windows.Add(processName: "Other", title: "Other");

        var ex = await Assert.ThrowsAsync<WindowVeilException>(() => h.Engine.AttachAsync(WindowSelector.ByProcessName("RetroCap")));

        Assert.Equal(WindowVeilErrorCode.TargetWindowNotFound, ex.Code);
        Assert.Empty(h.Engine.Sessions);
    }

    [Fact]
    public async Task Attach_InvalidHandle_ThrowsInvalidWindowHandle()
    {
        await using var h = new EngineHarness();

        var ex = await Assert.ThrowsAsync<WindowVeilException>(() => h.Engine.AttachAsync((nint)0xDEAD));

        Assert.Equal(WindowVeilErrorCode.InvalidWindowHandle, ex.Code);
    }

    [Fact]
    public async Task Attach_WithTimeout_WaitsForWindowToAppear()
    {
        await using var h = new EngineHarness();
        var appear = Task.Run(async () => { await Task.Delay(150); h.Windows.Add(); });

        var session = await h.Engine.AttachAsync(WindowSelector.ByProcessName("RetroCap"), Quiet with { AttachTimeout = TimeSpan.FromSeconds(5) });

        await appear;
        Assert.NotNull(session.Target);
    }

    [Fact]
    public async Task Attach_Deferred_WaitsForTargetThenShowsOverlays()
    {
        await using var h = new EngineHarness();
        var session = await h.Engine.AttachAsync(WindowSelector.ByProcessName("RetroCap"), Quiet with { AllowDeferredAttach = true });
        Assert.Equal(SessionState.WaitingForTarget, session.State);
        Assert.Null(session.Target);
        session.AddRule(Mask("r1"));

        await session.StartAsync();
        Assert.Equal(SessionState.WaitingForTarget, session.State);
        Assert.Equal(0, h.Resolver.TotalCalls);
        Assert.Empty(h.Renderer.Created);

        var w = h.Windows.Add();
        await session.RefreshAsync();

        Assert.Equal(SessionState.Running, session.State);
        Assert.Equal(w.Handle, session.Target!.Handle);
        var status = session.GetRuleStatus("r1");
        Assert.Equal(RuleState.Active, status.State);
        Assert.True(h.Renderer.LiveFor("r1")!.IsShown);
        Assert.Equal([w.Handle], h.Events.SubscribedHandles);
    }

    [Fact]
    public async Task Attach_Deferred_ReconnectPollFindsWindowWithoutExplicitRefresh()
    {
        await using var h = new EngineHarness();
        var session = await h.Engine.AttachAsync(WindowSelector.ByProcessName("RetroCap"),
            Quiet with { AllowDeferredAttach = true, ReconnectPollInterval = TimeSpan.FromMilliseconds(20) });
        session.AddRule(Mask("r1"));
        await session.StartAsync();

        h.Windows.Add();

        var status = await session.ForStateAsync("r1", RuleState.Active);
        Assert.Equal(SessionState.Running, session.State);
        Assert.Single(status.OverlayBounds);
    }

    [Fact]
    public async Task StateChanged_RaisedForStartAndStop()
    {
        await using var h = new EngineHarness();
        var (_, session) = await h.AttachAsync();
        var changes = new List<(SessionState Old, SessionState New)>();
        session.StateChanged += (_, e) => { lock (changes) changes.Add((e.OldState, e.NewState)); };

        await session.StartAsync();
        await session.StopAsync();

        lock (changes)
        {
            Assert.Equal([(SessionState.Created, SessionState.Running), (SessionState.Running, SessionState.Stopped)], changes);
        }
    }
}

public class SessionRuleTests
{
    [Fact]
    public async Task AddRule_DuplicateId_ThrowsInvalidRule()
    {
        await using var h = new EngineHarness();
        var (_, session) = await h.AttachAsync();
        session.AddRule(Mask("r1"));

        var ex = Assert.Throws<WindowVeilException>(() => session.AddRule(Mask("r1", "b")));

        Assert.Equal(WindowVeilErrorCode.InvalidRule, ex.Code);
        Assert.Contains("r1", ex.Message, StringComparison.Ordinal);
        Assert.Single(session.Rules);
    }

    [Fact]
    public async Task AddRule_UnknownRegisteredAppearance_ThrowsInvalidRule()
    {
        await using var h = new EngineHarness();
        var (_, session) = await h.AttachAsync();

        var ex = Assert.Throws<WindowVeilException>(() => session.AddRule(Mask("r1") with { Appearance = new RegisteredAppearance("nope") }));

        Assert.Equal(WindowVeilErrorCode.InvalidRule, ex.Code);
        Assert.Contains("nope", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AddRule_NoRendererForAppearance_ThrowsRendererNotFound()
    {
        await using var h = new EngineHarness(withRenderer: false);
        var (_, session) = await h.AttachAsync();

        var ex = Assert.Throws<WindowVeilException>(() => session.AddRule(Mask("r1")));

        Assert.Equal(WindowVeilErrorCode.RendererNotFound, ex.Code);
    }

    [Fact]
    public async Task AddRule_RendererRejectsAppearanceKind_ThrowsRendererNotFound()
    {
        await using var h = new EngineHarness();
        h.Renderer.CanRenderPredicate = a => a is MaskAppearance;
        var (_, session) = await h.AttachAsync();

        var ex = Assert.Throws<WindowVeilException>(() => session.AddRule(Mask("r1") with { Appearance = new FakeContentAppearance("x") }));

        Assert.Equal(WindowVeilErrorCode.RendererNotFound, ex.Code);
        Assert.Contains("fakeContent", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AddRule_UnsupportedLocator_ThrowsInvalidRule()
    {
        await using var h = new EngineHarness();
        var (_, session) = await h.AttachAsync();

        // No UI Automation resolver is registered in the harness.
        var ex = Assert.Throws<WindowVeilException>(() => session.AddRule(Mask("r1", ElementLocator.ByAutomationId("x"))));

        Assert.Equal(WindowVeilErrorCode.InvalidRule, ex.Code);
        Assert.Contains("uiAutomation", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AddRule_FallbackWithOneSupportedEntry_IsAccepted()
    {
        await using var h = new EngineHarness();
        var (_, session) = await h.AttachAsync();

        session.AddRule(Mask("r1", ElementLocator.ByAutomationId("x").Or(new FakeLocator("a"))));
        await session.StartAsync();

        var status = session.GetRuleStatus("r1");
        Assert.Equal(RuleState.Active, status.State);
        Assert.Equal(new FakeLocator("a"), status.Resolution!.ResolvedBy);
        Assert.Equal(LocatorStatus.Unsupported, status.Resolution.Attempts[0].Status);
    }

    [Fact]
    public async Task AddRule_InvalidRule_ThrowsInvalidRule()
    {
        await using var h = new EngineHarness();
        var (_, session) = await h.AttachAsync();

        var ex = Assert.Throws<WindowVeilException>(() => session.AddRule(Mask("r1", new UiAutomationLocator())));

        Assert.Equal(WindowVeilErrorCode.InvalidRule, ex.Code);
    }

    [Fact]
    public async Task AddRules_OneInvalid_AddsNone()
    {
        await using var h = new EngineHarness();
        var (_, session) = await h.AttachAsync();

        Assert.Throws<WindowVeilException>(() => session.AddRules([Mask("r1"), Mask("r2", ElementLocator.ByAutomationId("x"))]));
        Assert.Throws<WindowVeilException>(() => session.AddRules([Mask("r1"), Mask("r1", "b")]));

        Assert.Empty(session.Rules);
    }

    [Fact]
    public async Task AddRule_RegisteredAppearance_ResolvedThroughEngineRegistry()
    {
        var parameters = new List<IReadOnlyDictionary<string, string>>();
        await using var h = new EngineHarness(configure: b => b.RegisterAppearance("badge", p =>
        {
            parameters.Add(p);
            return new FakeContentAppearance(p["label"]);
        }));
        var (_, session) = await h.AttachAsync();

        session.AddRule(Mask("r1") with
        {
            Appearance = new RegisteredAppearance("BADGE") { Parameters = new Dictionary<string, string> { ["label"] = "Beta" } },
        });
        await session.StartAsync();

        var visual = h.Renderer.LiveFor("r1")!;
        Assert.Equal(new FakeContentAppearance("Beta"), visual.Request.Appearance);
        Assert.Single(parameters);
        // The rule keeps the reference; only the session-side entry holds the factory output.
        Assert.IsType<RegisteredAppearance>(session.GetRule("r1")!.Appearance);
    }

    [Fact]
    public async Task StartAsync_ResolvesRules_ActiveWithBoundsResolvedByAndVisual()
    {
        await using var h = new EngineHarness();
        var (w, session) = await h.AttachAsync();
        var rule = session.AddRule(Mask("r1") with { InputMode = OverlayInputMode.Interactive, ZOrder = OverlayZOrder.Topmost });

        await session.StartAsync();

        Assert.Equal(SessionState.Running, session.State);
        var expected = ClientRect(w, h.Resolver.DefaultClientRect);
        var status = session.GetRuleStatus("r1");
        Assert.Equal(RuleState.Active, status.State);
        Assert.Equal([expected], status.OverlayBounds);
        Assert.Equal(rule.Target, status.Resolution!.ResolvedBy);
        Assert.Equal("fake", status.Renderer);
        Assert.Null(status.Error);

        var visual = Assert.Single(h.Renderer.Created);
        Assert.Equal(visual.Handle, status.OverlayHandle);
        Assert.Equal(OverlayInputMode.Interactive, visual.Request.InputMode);
        Assert.Equal(OverlayZOrder.Topmost, visual.Request.ZOrder);
        var frame = visual.CurrentFrame!;
        Assert.True(frame.Visible);
        Assert.Equal(expected, frame.Bounds);
        Assert.Equal([expected], frame.TargetRects);
        Assert.Equal(w.Handle, frame.TargetWindow);
        Assert.Equal(w.Bounds, frame.TargetWindowBounds);
        Assert.Equal([w.Handle], h.Events.SubscribedHandles);
    }

    [Fact]
    public async Task StartAsync_FallbackChain_ReportsWinningStrategyAndAttempts()
    {
        await using var h = new EngineHarness();
        h.Resolver.NotFound("missing");
        var (_, session) = await h.AttachAsync();
        session.AddRule(Mask("r1", ElementLocator.FirstOf(new FakeLocator("missing"), new FakeLocator("b"))));

        await session.StartAsync();

        var status = session.GetRuleStatus("r1");
        Assert.Equal(RuleState.Active, status.State);
        Assert.Equal(new FakeLocator("b"), status.Resolution!.ResolvedBy);
        Assert.Equal([LocatorStatus.NotFound, LocatorStatus.Resolved], status.Resolution.Attempts.Select(a => a.Status));
    }

    [Fact]
    public async Task StartAsync_UnresolvedLocator_NoOverlayAndExplicitError()
    {
        await using var h = new EngineHarness();
        h.Resolver.NotFound("a", "StartupCheck is not in the tree.");
        var (_, session) = await h.AttachAsync();
        session.AddRule(Mask("r1"));

        await session.StartAsync();

        var status = session.GetRuleStatus("r1");
        Assert.Equal(RuleState.Unresolved, status.State);
        Assert.Equal(WindowVeilErrorCode.ElementNotFound, status.Error);
        Assert.Equal("StartupCheck is not in the tree.", status.Message);
        Assert.Empty(status.OverlayBounds);
        Assert.Empty(h.Renderer.Created); // no visual, no invented coordinates
    }

    [Fact]
    public async Task ContentChanged_ElementDisappears_OverlayHiddenAndUnresolved()
    {
        await using var h = new EngineHarness();
        var (w, session) = await h.AttachAsync();
        session.AddRule(Mask("r1"));
        await session.StartAsync();
        var visual = h.Renderer.LiveFor("r1")!;
        Assert.True(visual.IsShown);

        h.Resolver.NotFound("a");
        h.Events.Raise(w.Handle, WindowEventKind.ContentChanged);

        var status = await session.ForStateAsync("r1", RuleState.Unresolved);
        Assert.Empty(status.OverlayBounds);
        await Wait.UntilAsync(() => !visual.IsShown, "overlay hidden");
        Assert.False(visual.IsDisposed); // kept for when the element comes back
    }

    [Fact]
    public async Task StartAsync_ResolverThrows_UnresolvedWithMessage()
    {
        await using var h = new EngineHarness();
        h.Resolver.Throw("a", "UIA provider crashed");
        var (_, session) = await h.AttachAsync();
        session.AddRule(Mask("r1"));

        await session.StartAsync();

        var status = session.GetRuleStatus("r1");
        Assert.Equal(RuleState.Unresolved, status.State);
        Assert.Equal(LocatorStatus.Error, status.Resolution!.Status);
        Assert.Contains("UIA provider crashed", status.Message, StringComparison.Ordinal);
        Assert.Empty(h.Renderer.Created);
    }

    [Fact]
    public async Task UpdateRule_PlacementChange_MovesSameVisualWithoutResolving()
    {
        await using var h = new EngineHarness();
        var (w, session) = await h.AttachAsync();
        var rule = session.AddRule(Mask("r1"));
        await session.StartAsync();
        var visual = h.Renderer.LiveFor("r1")!;
        int calls = h.Resolver.TotalCalls;
        var target = ClientRect(w, h.Resolver.DefaultClientRect);

        session.UpdateRule(rule with { Placement = OverlayPlacement.Cover(4) });

        var status = await session.WaitForRuleAsync("r1", s => s.OverlayBounds.SequenceEqual([target.Inflate(4)]), WaitTimeout);
        Assert.Equal(RuleState.Active, status.State);
        Assert.Same(visual, h.Renderer.LiveFor("r1"));
        Assert.Single(h.Renderer.Created);
        Assert.Equal(target.Inflate(4), visual.CurrentFrame!.Bounds);
        Assert.Equal(calls, h.Resolver.TotalCalls);
    }

    [Fact]
    public async Task UpdateRule_AppearanceChange_RecreatesVisual()
    {
        await using var h = new EngineHarness();
        var (_, session) = await h.AttachAsync();
        var rule = session.AddRule(Mask("r1"));
        await session.StartAsync();
        var first = h.Renderer.LiveFor("r1")!;

        session.UpdateRule(rule with { Appearance = HighlightAppearance.Create("#FF0000") });
        await session.RefreshAsync();

        Assert.True(first.IsDisposed);
        var second = h.Renderer.LiveFor("r1")!;
        Assert.NotSame(first, second);
        Assert.IsType<HighlightAppearance>(second.Request.Appearance);
        Assert.True(second.IsShown);
        Assert.Equal(second.Handle, session.GetRuleStatus("r1").OverlayHandle);
    }

    [Fact]
    public async Task UpdateRule_InputModeChange_RecreatesVisual()
    {
        await using var h = new EngineHarness();
        var (_, session) = await h.AttachAsync();
        var rule = session.AddRule(Mask("r1"));
        await session.StartAsync();
        var first = h.Renderer.LiveFor("r1")!;

        session.UpdateRule(rule with { InputMode = OverlayInputMode.Interactive });
        await session.RefreshAsync();

        Assert.True(first.IsDisposed);
        Assert.Equal(OverlayInputMode.Interactive, h.Renderer.LiveFor("r1")!.Request.InputMode);
    }

    [Fact]
    public async Task UpdateRule_LocatorChange_ReResolvesWithNewLocatorAndReleasesOldState()
    {
        await using var h = new EngineHarness();
        h.Resolver.ResolveTo("b", new PixelRect(300, 300, 40, 40));
        var (w, session) = await h.AttachAsync();
        var rule = session.AddRule(Mask("r1"));
        await session.StartAsync();
        var oldState = (FakeState)session.GetRuleStatus("r1").Resolution!.State!;

        session.UpdateRule(rule with { Target = new FakeLocator("b") });

        var status = await session.WaitForRuleAsync("r1", s => s.Resolution?.ResolvedBy == new FakeLocator("b") && s.State == RuleState.Active, WaitTimeout);
        Assert.Equal([ClientRect(w, new PixelRect(300, 300, 40, 40))], status.OverlayBounds);
        Assert.Equal(1, h.Resolver.Calls("b"));
        Assert.True(oldState.IsDisposed);
        Assert.Single(h.Renderer.Created); // same appearance: visual reused
    }

    [Fact]
    public async Task UpdateRule_UnknownId_Throws()
    {
        await using var h = new EngineHarness();
        var (_, session) = await h.AttachAsync();

        Assert.Throws<KeyNotFoundException>(() => session.UpdateRule(Mask("missing")));
        Assert.Throws<KeyNotFoundException>(() => session.DisableRule("missing"));
        Assert.Throws<KeyNotFoundException>(() => session.GetRuleStatus("missing"));
        Assert.Null(session.GetRule("missing"));
    }

    [Fact]
    public async Task DisableEnable_HidesAndShowsSameVisual_RaisesStatusEvents()
    {
        await using var h = new EngineHarness();
        var (_, session) = await h.AttachAsync();
        session.AddRule(Mask("r1"));
        await session.StartAsync();
        var visual = h.Renderer.LiveFor("r1")!;
        var events = new List<RuleState>();
        session.RuleStatusChanged += (_, e) => { lock (events) events.Add(e.Status.State); };

        session.DisableRule("r1");
        Assert.Equal(RuleState.Disabled, session.GetRuleStatus("r1").State);
        await session.RefreshAsync();
        Assert.False(visual.IsShown);
        Assert.False(visual.IsDisposed);
        Assert.False(session.GetRule("r1")!.Enabled);

        session.EnableRule("r1");
        await session.ForStateAsync("r1", RuleState.Active);
        await session.RefreshAsync();

        Assert.True(visual.IsShown);
        Assert.Single(h.Renderer.Created);
        lock (events)
        {
            Assert.Contains(RuleState.Disabled, events);
            Assert.Equal(RuleState.Active, events[^1]);
        }
    }

    [Fact]
    public async Task DisabledRule_IsNotResolved()
    {
        await using var h = new EngineHarness();
        var (_, session) = await h.AttachAsync();
        session.AddRule(Mask("r1") with { Enabled = false });

        await session.StartAsync();

        Assert.Equal(RuleState.Disabled, session.GetRuleStatus("r1").State);
        Assert.Equal(0, h.Resolver.TotalCalls);
        Assert.Empty(h.Renderer.Created);
    }

    [Fact]
    public async Task SetGroupEnabled_AffectsOnlyThatGroup()
    {
        await using var h = new EngineHarness();
        var (_, session) = await h.AttachAsync();
        session.AddRule(Mask("a1") with { Group = "privacy" });
        session.AddRule(Mask("a2", "b") with { Group = "privacy" });
        session.AddRule(Mask("other", "c") with { Group = "tour" });
        await session.StartAsync();

        int affected = session.SetGroupEnabled("privacy", false);
        await session.RefreshAsync();

        Assert.Equal(2, affected);
        Assert.Equal(RuleState.Disabled, session.GetRuleStatus("a1").State);
        Assert.Equal(RuleState.Disabled, session.GetRuleStatus("a2").State);
        Assert.Equal(RuleState.Active, session.GetRuleStatus("other").State);
        Assert.False(h.Renderer.LiveFor("a1")!.IsShown);
        Assert.True(h.Renderer.LiveFor("other")!.IsShown);
        Assert.Equal(0, session.SetGroupEnabled("missing", false));

        session.SetGroupEnabled("privacy", true);
        await session.RefreshAsync();
        Assert.Equal(RuleState.Active, session.GetRuleStatus("a1").State);
    }

    [Fact]
    public async Task RemoveRule_DisposesVisualAndReleasesState()
    {
        await using var h = new EngineHarness();
        var (_, session) = await h.AttachAsync();
        session.AddRule(Mask("r1"));
        session.AddRule(Mask("r2", "b"));
        await session.StartAsync();
        var visual = h.Renderer.LiveFor("r1")!;
        var state = (FakeState)session.GetRuleStatus("r1").Resolution!.State!;

        Assert.True(session.RemoveRule("r1"));
        Assert.False(session.RemoveRule("r1"));
        await session.RefreshAsync();

        Assert.True(visual.IsDisposed);
        Assert.True(state.IsDisposed);
        Assert.Null(session.GetRule("r1"));
        Assert.Equal(["r2"], session.Rules.Select(r => r.Id));
        Assert.True(h.Renderer.LiveFor("r2")!.IsShown);
    }

    [Fact]
    public async Task ClearRules_DisposesEveryVisual()
    {
        await using var h = new EngineHarness();
        var (_, session) = await h.AttachAsync();
        session.AddRules([Mask("r1"), Mask("r2", "b"), Mask("r3", "c")]);
        await session.StartAsync();
        Assert.Equal(3, h.Renderer.Live.Count);

        session.ClearRules();
        await session.RefreshAsync();

        Assert.Empty(session.Rules);
        Assert.Empty(h.Renderer.Live);
        Assert.Equal(0, h.Resolver.LiveStates);
    }

    [Fact]
    public async Task SuspendResume_HidesAllThenShowsSameVisuals()
    {
        await using var h = new EngineHarness();
        var (_, session) = await h.AttachAsync();
        session.AddRules([Mask("r1"), Mask("r2", "b")]);
        await session.StartAsync();
        var visuals = h.Renderer.Live;

        await session.SuspendAsync();

        Assert.Equal(SessionState.Suspended, session.State);
        Assert.All(visuals, v => Assert.False(v.IsShown));
        Assert.All(session.RuleStatuses, s => Assert.Equal(RuleState.Hidden, s.State));
        Assert.All(visuals, v => Assert.False(v.IsDisposed));

        await session.ResumeAsync();

        Assert.Equal(SessionState.Running, session.State);
        Assert.All(visuals, v => Assert.True(v.IsShown));
        Assert.All(session.RuleStatuses, s => Assert.Equal(RuleState.Active, s.State));
        Assert.Equal(2, h.Renderer.Created.Count);
    }

    [Fact]
    public async Task Priority_HigherPriorityRulesResolvedFirst()
    {
        await using var h = new EngineHarness();
        var (_, session) = await h.AttachAsync();
        session.AddRule(Mask("low", "low") with { Priority = -5 });
        session.AddRule(Mask("high", "high") with { Priority = 10 });
        session.AddRule(Mask("mid", "mid"));

        await session.StartAsync();

        Assert.Equal(["high", "mid", "low"], h.Resolver.Log.Select(l => l.Key).Take(3));
    }

    [Fact]
    public async Task ContentSizedPlacement_UsesMeasuredSizeAtTargetDpi()
    {
        await using var h = new EngineHarness();
        var w = h.Windows.Add(dpiPercent: 150);
        var session = await h.Engine.AttachAsync(WindowSelector.ByProcessName("RetroCap"));
        session.AddRule(Mask("badge") with
        {
            Appearance = new FakeContentAppearance("New") { ContentSize = new PixelSize(40, 20) },
            Placement = OverlayPlacement.Right(offsetX: 4),
        });

        await session.StartAsync();

        var target = ClientRect(w, h.Resolver.DefaultClientRect); // 100x30 at (118,151)
        var status = session.GetRuleStatus("badge");
        // 40x20 DIP content at 150% = 60x30 px; offset 4 DIP = 6 px; vertically centered on the 30 px target.
        Assert.Equal([new PixelRect(target.Right + 6, target.Y, 60, 30)], status.OverlayBounds);
        await session.RefreshAsync();
        Assert.Equal(1, h.Renderer.LiveFor("badge")!.MeasureCalls); // cached per DPI
    }

    [Fact]
    public async Task PlacementPadding_ScaledByTargetDpi()
    {
        await using var h = new EngineHarness();
        var w = h.Windows.Add(dpiPercent: 200);
        var session = await h.Engine.AttachAsync(WindowSelector.ByProcessName("RetroCap"));
        session.AddRule(Mask("r1") with { Placement = OverlayPlacement.Cover(new Insets(1, 2, 3, 4)) });

        await session.StartAsync();

        var target = ClientRect(w, h.Resolver.DefaultClientRect);
        Assert.Equal([target.Inflate(2, 4, 6, 8)], session.GetRuleStatus("r1").OverlayBounds);
    }
}
