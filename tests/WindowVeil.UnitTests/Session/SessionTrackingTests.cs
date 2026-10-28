using WindowVeil.Geometry;
using WindowVeil.Locators;
using WindowVeil.Platform;
using WindowVeil.UnitTests.Fakes;
using static WindowVeil.UnitTests.Fakes.EngineHarness;

namespace WindowVeil.UnitTests.Session;

public class SessionTrackingTests
{
    [Fact]
    public async Task WindowMoveWithoutResize_TranslatesGeometryWithoutResolving()
    {
        await using var h = new EngineHarness();
        var (w, session) = await h.AttachAsync();
        session.AddRule(Mask("r1"));
        await session.StartAsync();
        int calls = h.Resolver.TotalCalls;
        var before = session.GetRuleStatus("r1").OverlayBounds[0];
        var visual = h.Renderer.LiveFor("r1")!;

        h.Windows.Move(w.Handle, -250, 40);
        h.Events.Raise(w.Handle, WindowEventKind.LocationChanged);

        var expected = before.Offset(-250, 40);
        var status = await session.WaitForRuleAsync("r1", s => s.OverlayBounds.SequenceEqual([expected]), WaitTimeout);
        Assert.Equal(RuleState.Active, status.State);
        Assert.Equal(expected, visual.CurrentFrame!.Bounds);
        Assert.Equal(w.Bounds.Offset(-250, 40), visual.CurrentFrame.TargetWindowBounds);
        await Task.Delay(50); // a resolve would happen in the same pass, right after rendering
        Assert.Equal(calls, h.Resolver.TotalCalls);
        Assert.Equal(expected, session.GetRuleStatus("r1").Resolution!.UnionBounds);
    }

    [Fact]
    public async Task WindowResize_ReResolves()
    {
        await using var h = new EngineHarness();
        var (w, session) = await h.AttachAsync();
        session.AddRule(Mask("r1"));
        await session.StartAsync();
        int calls = h.Resolver.Calls("a");

        h.Windows.Resize(w.Handle, 1000, 700);
        h.Events.Raise(w.Handle, WindowEventKind.LocationChanged);

        await Wait.UntilAsync(() => h.Resolver.Calls("a") == calls + 1, "re-resolve after resize");
        Assert.Equal(ResolveReason.WindowChanged, h.Resolver.Log[^1].Reason);
        await session.RefreshAsync();
        Assert.Equal(RuleState.Active, session.GetRuleStatus("r1").State);
    }

    [Fact]
    public async Task WindowRelativeLocator_FollowsWindowMove()
    {
        await using var h = new EngineHarness();
        var (w, session) = await h.AttachAsync();
        session.AddRule(Mask("r1", ElementLocator.WindowRelative(5, 6, 50, 20, WindowAnchor.Window)));
        await session.StartAsync();
        Assert.Equal([new PixelRect(w.Bounds.X + 5, w.Bounds.Y + 6, 50, 20)], session.GetRuleStatus("r1").OverlayBounds);

        h.Windows.Move(w.Handle, 30, -20);
        h.Events.Raise(w.Handle, WindowEventKind.LocationChanged);

        await session.WaitForRuleAsync("r1", s => s.OverlayBounds.SequenceEqual([new PixelRect(w.Bounds.X + 35, w.Bounds.Y - 14, 50, 20)]), WaitTimeout);
    }

    [Fact]
    public async Task ScreenRectangle_DoesNotFollowWindow()
    {
        await using var h = new EngineHarness();
        var (w, session) = await h.AttachAsync();
        session.AddRule(Mask("r1", ElementLocator.Rectangle(-500, 10, 50, 20)));
        await session.StartAsync();

        h.Windows.Move(w.Handle, 100, 100);
        h.Events.Raise(w.Handle, WindowEventKind.LocationChanged);
        await session.RefreshAsync();

        Assert.Equal([new PixelRect(-500, 10, 50, 20)], session.GetRuleStatus("r1").OverlayBounds);
    }

    [Fact]
    public async Task FollowTargetFalse_StaysAtInitialScreenPosition()
    {
        await using var h = new EngineHarness();
        var (w, session) = await h.AttachAsync();
        session.AddRule(Mask("r1") with { FollowTarget = false });
        await session.StartAsync();
        var initial = session.GetRuleStatus("r1").OverlayBounds[0];
        int calls = h.Resolver.TotalCalls;

        h.Windows.Move(w.Handle, 100, 100);
        h.Events.Raise(w.Handle, WindowEventKind.LocationChanged);
        h.Events.Raise(w.Handle, WindowEventKind.ContentChanged);
        await session.RefreshAsync();

        Assert.Equal([initial], session.GetRuleStatus("r1").OverlayBounds);
        Assert.Equal(calls, h.Resolver.TotalCalls);
    }

    [Fact]
    public async Task MinimizeRestore_HidesThenShows()
    {
        await using var h = new EngineHarness();
        var (w, session) = await h.AttachAsync();
        session.AddRule(Mask("r1"));
        await session.StartAsync();
        var visual = h.Renderer.LiveFor("r1")!;

        h.Windows.SetMinimized(w.Handle, true);
        h.Events.Raise(w.Handle, WindowEventKind.Minimized);
        var hidden = await session.ForStateAsync("r1", RuleState.Hidden);

        Assert.Equal("Target minimized", hidden.Message);
        Assert.Empty(hidden.OverlayBounds);
        Assert.False(visual.IsShown);

        h.Windows.SetMinimized(w.Handle, false);
        h.Events.Raise(w.Handle, WindowEventKind.Restored);
        await session.ForStateAsync("r1", RuleState.Active);

        Assert.True(visual.IsShown);
        Assert.Single(h.Renderer.Created);
    }

    [Fact]
    public async Task WhenTargetForeground_HiddenWhileAnotherWindowIsForeground()
    {
        await using var h = new EngineHarness();
        var (w, session) = await h.AttachAsync();
        session.AddRule(Mask("fg") with { Visibility = OverlayVisibility.WhenTargetForeground });
        session.AddRule(Mask("always", "b"));
        await session.StartAsync();
        Assert.Equal(RuleState.Active, session.GetRuleStatus("fg").State);

        h.Windows.SetForeground(w.Handle, false);
        h.Events.Raise((nint)0x1234, WindowEventKind.ForegroundChanged);
        var status = await session.ForStateAsync("fg", RuleState.Hidden);

        Assert.Equal("Target is not in the foreground", status.Message);
        Assert.Equal(RuleState.Active, session.GetRuleStatus("always").State);
        Assert.False(h.Renderer.LiveFor("fg")!.IsShown);

        h.Windows.SetForeground(w.Handle, true);
        h.Events.Raise(w.Handle, WindowEventKind.ForegroundChanged);
        await session.ForStateAsync("fg", RuleState.Active);
        Assert.True(h.Renderer.LiveFor("fg")!.IsShown);
    }

    [Fact]
    public async Task ContentChanged_ReResolvesAndMovesOverlay()
    {
        await using var h = new EngineHarness();
        var (w, session) = await h.AttachAsync();
        session.AddRule(Mask("r1"));
        await session.StartAsync();

        h.Resolver.ResolveTo("a", new PixelRect(10, 200, 100, 30)); // the element scrolled
        h.Events.Raise(w.Handle, WindowEventKind.ContentChanged);

        var expected = ClientRect(w, new PixelRect(10, 200, 100, 30));
        await session.WaitForRuleAsync("r1", s => s.OverlayBounds.SequenceEqual([expected]), WaitTimeout);
        Assert.Equal(ResolveReason.ContentChanged, h.Resolver.Log[^1].Reason);
    }

    [Fact]
    public async Task ElementRefreshInterval_PollsElementLocators()
    {
        await using var h = new EngineHarness();
        var (_, session) = await h.AttachAsync(Quiet with { ElementRefreshInterval = TimeSpan.FromMilliseconds(20) });
        session.AddRule(Mask("r1"));
        await session.StartAsync();

        h.Resolver.ResolveTo("a", new PixelRect(0, 0, 10, 10));

        await session.WaitForRuleAsync("r1", s => s.OverlayBounds.Count == 1 && s.OverlayBounds[0].Width == 10, WaitTimeout);
        Assert.Contains(h.Resolver.Log, l => l.Reason == ResolveReason.Poll);
    }

    [Fact]
    public async Task NoEventSource_GeometryPollingTracksMoves()
    {
        await using var h = new EngineHarness(withEventSource: false);
        var (w, session) = await h.AttachAsync(Quiet with { GeometryPollInterval = TimeSpan.FromMilliseconds(15) });
        session.AddRule(Mask("r1"));
        await session.StartAsync();
        var before = session.GetRuleStatus("r1").OverlayBounds[0];

        h.Windows.Move(w.Handle, 7, 9);

        await session.WaitForRuleAsync("r1", s => s.OverlayBounds.SequenceEqual([before.Offset(7, 9)]), WaitTimeout);
    }

    [Fact]
    public async Task TargetDestroyed_HidesOverlaysWaitsAndReleasesState()
    {
        await using var h = new EngineHarness();
        var (w, session) = await h.AttachAsync();
        session.AddRules([Mask("r1"), Mask("r2", "b")]);
        await session.StartAsync();
        var visuals = h.Renderer.Live;
        var states = session.RuleStatuses.Select(s => (FakeState)s.Resolution!.State!).ToArray();
        var stateChanges = new List<SessionStateChangedEventArgs>();
        session.StateChanged += (_, e) => { lock (stateChanges) stateChanges.Add(e); };

        h.Windows.Destroy(w.Handle);
        h.Events.Raise(w.Handle, WindowEventKind.Destroyed);

        await session.ForSessionStateAsync(SessionState.WaitingForTarget);
        await session.ForStateAsync("r1", RuleState.Hidden);
        await session.ForStateAsync("r2", RuleState.Hidden);
        Assert.All(visuals, v => Assert.False(v.IsShown));
        Assert.All(visuals, v => Assert.False(v.IsDisposed)); // kept for reconnection
        Assert.All(states, s => Assert.True(s.IsDisposed));
        Assert.All(session.RuleStatuses, s => Assert.Null(s.Resolution));
        Assert.Null(session.Target);
        Assert.Equal(0, h.Events.ActiveCount);
        lock (stateChanges)
        {
            var change = Assert.Single(stateChanges);
            Assert.Equal(SessionState.Running, change.OldState);
            Assert.Equal("Target window was destroyed", change.Reason);
        }
    }

    [Fact]
    public async Task TargetRelaunched_ReconnectsReResolvesAndReusesVisuals()
    {
        await using var h = new EngineHarness();
        var (w, session) = await h.AttachAsync();
        session.AddRules([Mask("r1"), Mask("r2", "b")]);
        await session.StartAsync();
        var visuals = h.Renderer.Live;
        h.Windows.Destroy(w.Handle);
        h.Events.Raise(w.Handle, WindowEventKind.Destroyed);
        await session.ForSessionStateAsync(SessionState.WaitingForTarget);

        var relaunched = h.Windows.Relaunch(w, new PixelRect(400, 300, 800, 600));
        await session.RefreshAsync();

        Assert.Equal(SessionState.Running, session.State);
        Assert.Equal(relaunched.Handle, session.Target!.Handle);
        Assert.NotEqual(w.Handle, relaunched.Handle);
        var expected = ClientRect(relaunched, h.Resolver.DefaultClientRect);
        var status = session.GetRuleStatus("r1");
        Assert.Equal(RuleState.Active, status.State);
        Assert.Equal([expected], status.OverlayBounds);
        Assert.Equal(visuals.Select(v => v.Handle).OrderBy(x => x), h.Renderer.Live.Select(v => v.Handle).OrderBy(x => x));
        Assert.All(visuals, v => Assert.True(v.IsShown));
        Assert.Equal(relaunched.Handle, visuals[0].CurrentFrame!.TargetWindow);
        Assert.Equal([relaunched.Handle], h.Events.SubscribedHandles);
        Assert.Contains(h.Resolver.Log, l => l.Reason == ResolveReason.Reconnected);
    }

    [Fact]
    public async Task TargetRelaunched_ReconnectPollReconnectsAutomatically()
    {
        await using var h = new EngineHarness();
        var (w, session) = await h.AttachAsync(Quiet with { ReconnectPollInterval = TimeSpan.FromMilliseconds(20) });
        session.AddRule(Mask("r1"));
        await session.StartAsync();

        h.Windows.Destroy(w.Handle);
        h.Events.Raise(w.Handle, WindowEventKind.Destroyed);
        await session.ForStateAsync("r1", RuleState.Hidden);
        h.Windows.Relaunch(w);

        await session.ForStateAsync("r1", RuleState.Active);
        Assert.Equal(SessionState.Running, session.State);
    }

    [Fact]
    public async Task TargetDestroyed_AutoReconnectDisabled_TargetLost()
    {
        await using var h = new EngineHarness();
        var (w, session) = await h.AttachAsync(Quiet with { AutoReconnect = false });
        session.AddRule(Mask("r1"));
        await session.StartAsync();

        h.Windows.Destroy(w.Handle);
        h.Events.Raise(w.Handle, WindowEventKind.Destroyed);
        await session.ForSessionStateAsync(SessionState.TargetLost);
        h.Windows.Relaunch(w);
        await session.RefreshAsync();

        Assert.Equal(SessionState.TargetLost, session.State);
        Assert.Equal(RuleState.Hidden, session.GetRuleStatus("r1").State);
        Assert.False(h.Renderer.LiveFor("r1")!.IsShown);
    }

    [Fact]
    public async Task HandleSelector_NeverReconnectsToAnotherWindow()
    {
        await using var h = new EngineHarness();
        var w = h.Windows.Add();
        var session = await h.Engine.AttachAsync(w.Handle);
        session.AddRule(Mask("r1"));
        await session.StartAsync();

        h.Windows.Destroy(w.Handle);
        h.Events.Raise(w.Handle, WindowEventKind.Destroyed);
        await session.ForSessionStateAsync(SessionState.TargetLost);
        h.Windows.Relaunch(w);
        await session.RefreshAsync();

        Assert.Equal(SessionState.TargetLost, session.State);
    }

    [Fact]
    public async Task TargetProcessExited_DetectedByTargetCheck()
    {
        await using var h = new EngineHarness();
        var (w, session) = await h.AttachAsync(Quiet with { TargetCheckInterval = TimeSpan.FromMilliseconds(20) });
        session.AddRule(Mask("r1"));
        await session.StartAsync();

        var reasons = new List<string?>();
        session.StateChanged += (_, e) => { lock (reasons) reasons.Add(e.Reason); };

        // The process dies but its window handle still answers and no event arrives.
        h.Windows.KillProcess(w.ProcessId);

        await Wait.UntilAsync(() => { lock (reasons) return reasons.Contains("Target process exited"); }, "process exit detected");
        h.Windows.Destroy(w.Handle); // the OS destroys the zombie window shortly after
        await session.ForSessionStateAsync(SessionState.WaitingForTarget);
        await session.ForStateAsync("r1", RuleState.Hidden);
    }

    [Fact]
    public async Task StopAsync_DisposesVisuals_StartAsyncRecreatesThem()
    {
        await using var h = new EngineHarness();
        var (_, session) = await h.AttachAsync();
        session.AddRules([Mask("r1"), Mask("r2", "b")]);
        await session.StartAsync();
        var first = h.Renderer.Live;

        await session.StopAsync();

        Assert.Equal(SessionState.Stopped, session.State);
        Assert.Empty(h.Renderer.Live);
        Assert.All(first, v => Assert.True(v.IsDisposed));
        Assert.Equal(0, h.Events.ActiveCount);
        Assert.Equal(2, session.Rules.Count);

        await session.StartAsync();

        Assert.Equal(SessionState.Running, session.State);
        Assert.Equal(2, h.Renderer.Live.Count);
        Assert.All(session.RuleStatuses, s => Assert.Equal(RuleState.Active, s.State));
    }

    [Fact]
    public async Task DisposeAsync_IsIdempotentAndDetachesFromEngine()
    {
        await using var h = new EngineHarness();
        var (_, session) = await h.AttachAsync();
        session.AddRule(Mask("r1"));
        await session.StartAsync();

        await session.DisposeAsync();
        await session.DisposeAsync();

        Assert.Equal(SessionState.Disposed, session.State);
        Assert.Empty(h.Renderer.Live);
        Assert.Equal(0, h.Resolver.LiveStates);
        Assert.DoesNotContain(session, h.Engine.Sessions);
        Assert.Throws<ObjectDisposedException>(() => session.AddRule(Mask("r2")));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => session.StartAsync());
        await session.RefreshAsync(); // no-op
    }

    [Fact]
    public async Task EngineDispose_DisposesSessionsRenderersAndPlatformServices()
    {
        var h = new EngineHarness();
        var (_, s1) = await h.AttachAsync();
        var s2 = await h.Engine.AttachAsync(WindowSelector.ByProcessName("RetroCap"));
        s1.AddRule(Mask("r1"));
        s2.AddRule(Mask("r2"));
        await s1.StartAsync();
        await s2.StartAsync();

        await h.Engine.DisposeAsync();
        await h.Engine.DisposeAsync();

        Assert.Equal(SessionState.Disposed, s1.State);
        Assert.Equal(SessionState.Disposed, s2.State);
        Assert.Empty(h.Engine.Sessions);
        Assert.Empty(h.Renderer.Live);
        Assert.True(h.Renderer.IsDisposed);
        Assert.True(h.Events.Disposed);
        Assert.True(h.Windows.Disposed);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => h.Engine.AttachAsync(WindowSelector.ByProcessName("RetroCap")));
    }
}
