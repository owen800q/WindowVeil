using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using WindowVeil.Appearance;
using WindowVeil.Geometry;
using WindowVeil.Locators;
using WindowVeil.Platform;
using WindowVeil.UnitTests.Fakes;
using static WindowVeil.UnitTests.Fakes.EngineHarness;

namespace WindowVeil.UnitTests.Session;

public class SessionRobustnessTests
{
    [Fact]
    public async Task RendererCreationFailure_RuleFailed_OtherRulesUnaffected()
    {
        await using var h = new EngineHarness();
        h.Renderer.FailCreate = r => r.RuleId == "bad";
        var (_, session) = await h.AttachAsync();
        session.AddRules([Mask("bad"), Mask("good", "b")]);

        await session.StartAsync();
        await session.RefreshAsync(); // failures are sticky, not retried on every pass

        var bad = session.GetRuleStatus("bad");
        Assert.Equal(RuleState.Failed, bad.State);
        Assert.Equal(WindowVeilErrorCode.OverlayCreationFailed, bad.Error);
        Assert.Contains("Injected creation failure", bad.Message, StringComparison.Ordinal);
        Assert.Equal(RuleState.Active, session.GetRuleStatus("good").State);
        Assert.Equal(SessionState.Running, session.State);
        Assert.Empty(h.Renderer.For("bad"));
    }

    [Fact]
    public async Task RendererCreationFailure_RecoversAfterAppearanceUpdate()
    {
        await using var h = new EngineHarness();
        h.Renderer.FailCreate = r => r.Appearance is HighlightAppearance;
        var (_, session) = await h.AttachAsync();
        var rule = session.AddRule(Mask("r1") with { Appearance = HighlightAppearance.Create("#F00") });
        await session.StartAsync();
        Assert.Equal(RuleState.Failed, session.GetRuleStatus("r1").State);

        session.UpdateRule(rule with { Appearance = MaskAppearance.Solid("#000") });
        await session.RefreshAsync();

        Assert.Equal(RuleState.Active, session.GetRuleStatus("r1").State);
    }

    [Fact]
    public async Task RendererApplyFailure_RuleFailed()
    {
        await using var h = new EngineHarness();
        h.Renderer.FailApply = true;
        var (_, session) = await h.AttachAsync();
        session.AddRule(Mask("r1"));

        await session.StartAsync();

        var status = session.GetRuleStatus("r1");
        Assert.Equal(RuleState.Failed, status.State);
        Assert.Equal(WindowVeilErrorCode.OverlayCreationFailed, status.Error);

        h.Renderer.FailApply = false;
        await session.RefreshAsync();
        Assert.Equal(RuleState.Active, session.GetRuleStatus("r1").State);
    }

    [Fact]
    public async Task ResolveTimeout_UnresolvedWithTimeoutMessage()
    {
        await using var h = new EngineHarness();
        h.Resolver.Block("a");
        var (_, session) = await h.AttachAsync(Quiet with { ResolveTimeout = TimeSpan.FromMilliseconds(100) });
        session.AddRules([Mask("slow"), Mask("fast", "b")]);

        await session.StartAsync();

        var status = session.GetRuleStatus("slow");
        Assert.Equal(RuleState.Unresolved, status.State);
        Assert.Contains("timed out", status.Message, StringComparison.Ordinal);
        Assert.Empty(h.Renderer.For("slow"));
        Assert.Equal(RuleState.Active, session.GetRuleStatus("fast").State);
    }

    [Fact]
    public async Task StartAsync_PreCancelledToken_DoesNotStart()
    {
        await using var h = new EngineHarness();
        var (_, session) = await h.AttachAsync();
        session.AddRule(Mask("r1"));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.StartAsync(new CancellationToken(canceled: true)));

        Assert.Equal(SessionState.Created, session.State);
        Assert.Equal(0, h.Resolver.TotalCalls);
    }

    [Fact]
    public async Task StartAsync_CancelledWhileResolving_ThrowsButSessionKeepsRunning()
    {
        await using var h = new EngineHarness();
        h.Resolver.Block("a");
        var (_, session) = await h.AttachAsync();
        session.AddRule(Mask("r1"));
        using var cts = new CancellationTokenSource();

        var start = session.StartAsync(cts.Token);
        Assert.True(await h.Resolver.BlockedEntered.WaitAsync(WaitTimeout));
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start);
        // Cancellation only abandons the wait: the session is started and finishes the pass.
        Assert.Equal(SessionState.Running, session.State);
        h.Resolver.Release("a");
        await session.ForStateAsync("r1", RuleState.Active);
    }

    [Fact]
    public async Task RefreshAsync_CancelledToken_ThrowsWithoutBreakingSession()
    {
        await using var h = new EngineHarness();
        var (_, session) = await h.AttachAsync();
        session.AddRule(Mask("r1"));
        await session.StartAsync();
        h.Resolver.Block("a");
        using var cts = new CancellationTokenSource();

        var refresh = session.RefreshAsync(cts.Token);
        Assert.True(await h.Resolver.BlockedEntered.WaitAsync(WaitTimeout));
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => refresh);
        h.Resolver.Release("a");
        await session.RefreshAsync();
        Assert.Equal(RuleState.Active, session.GetRuleStatus("r1").State);
    }

    [Fact]
    public async Task DisposeAsync_WhileResolverBlocked_CancelsResolutionPromptly()
    {
        await using var h = new EngineHarness();
        h.Resolver.Block("a");
        var (_, session) = await h.AttachAsync();
        session.AddRule(Mask("r1"));
        _ = session.StartAsync();
        Assert.True(await h.Resolver.BlockedEntered.WaitAsync(WaitTimeout));

        await session.DisposeAsync().AsTask().WaitAsync(WaitTimeout);

        Assert.Equal(SessionState.Disposed, session.State);
        Assert.Empty(h.Renderer.Live);
    }

    [Fact]
    public async Task UpdateRule_WhileOldLocatorResolving_NewLocatorWins()
    {
        await using var h = new EngineHarness();
        h.Resolver.ResolveTo("new", new PixelRect(400, 400, 20, 20));
        var (w, session) = await h.AttachAsync();
        var rule = session.AddRule(Mask("r1", "old"));
        h.Resolver.Block("old");
        _ = session.StartAsync();
        Assert.True(await h.Resolver.BlockedEntered.WaitAsync(WaitTimeout));

        session.UpdateRule(rule with { Target = new FakeLocator("new") });
        h.Resolver.Release("old");

        var status = await session.WaitForRuleAsync("r1", s => s.Resolution?.ResolvedBy == new FakeLocator("new"), WaitTimeout);
        Assert.Equal([ClientRect(w, new PixelRect(400, 400, 20, 20))], status.OverlayBounds);
        await session.RefreshAsync();
        Assert.Equal(new FakeLocator("new"), session.GetRuleStatus("r1").Resolution!.ResolvedBy);
        Assert.Equal(1, h.Resolver.LiveStates); // the stale result's state was released
    }

    [Fact]
    public async Task RemoveRule_WhileResolving_ReleasesLateResultState()
    {
        await using var h = new EngineHarness();
        var (_, session) = await h.AttachAsync();
        session.AddRule(Mask("r1"));
        h.Resolver.Block("a");
        _ = session.StartAsync();
        Assert.True(await h.Resolver.BlockedEntered.WaitAsync(WaitTimeout));

        session.RemoveRule("r1");
        h.Resolver.Release("a");
        await session.RefreshAsync();

        Assert.Single(h.Resolver.States);
        Assert.Equal(0, h.Resolver.LiveStates);
        Assert.Empty(h.Renderer.Live);
    }

    [Fact]
    public async Task ReResolve_ReleasesPreviousStateButKeepsReusedState()
    {
        await using var h = new EngineHarness();
        var shared = new FakeState();
        int call = 0;
        h.Resolver.Script("a", (l, c) =>
        {
            // The resolver hands the same cached state to every resolution (like a cached UI Automation element).
            call++;
            return LocatorResolution.Found(l, new PixelRect(c.Window.ClientBounds.X, c.Window.ClientBounds.Y, 10, 10)) with { State = shared };
        });
        var (_, session) = await h.AttachAsync();
        session.AddRule(Mask("r1"));
        await session.StartAsync();

        await session.RefreshAsync();
        await session.RefreshAsync();

        Assert.True(call >= 3);
        Assert.False(shared.IsDisposed); // still referenced by the current resolution
        await session.DisposeAsync();
        Assert.True(shared.IsDisposed);
    }

    [Fact]
    public async Task FallbackAttemptState_SharedWithNewResult_IsNotDisposed()
    {
        await using var h = new EngineHarness();
        var attemptState = new FakeState();
        var winnerStates = new List<FakeState>();
        h.Resolver.Script("probe", (l, _) => LocatorResolution.Failed(l, LocatorStatus.NotFound, "not yet") with { State = attemptState });
        h.Resolver.Script("b", (l, c) =>
        {
            var s = new FakeState();
            lock (winnerStates) winnerStates.Add(s);
            return LocatorResolution.Found(l, new PixelRect(c.Window.ClientBounds.X, c.Window.ClientBounds.Y, 10, 10)) with { State = s };
        });
        var (_, session) = await h.AttachAsync();
        session.AddRule(Mask("r1", ElementLocator.FirstOf(new FakeLocator("probe"), new FakeLocator("b"))));
        await session.StartAsync();

        await session.RefreshAsync();

        Assert.False(attemptState.IsDisposed); // reused by the probe attempt of the new resolution
        lock (winnerStates)
        {
            Assert.True(winnerStates[0].IsDisposed);
            Assert.False(winnerStates[^1].IsDisposed);
        }
    }

    [Fact]
    public async Task ConcurrentRuleChangesAndEvents_FinalStateIsConsistent()
    {
        var logs = new CapturingLoggerFactory();
        var h = new EngineHarness(configure: b => b.UseLoggerFactory(logs));
        var (w, session) = await h.AttachAsync();
        await session.StartAsync();
        using var stop = new CancellationTokenSource();

        var eventPump = Task.Run(async () =>
        {
            int i = 0;
            while (!stop.IsCancellationRequested)
            {
                if (i % 3 == 0) h.Windows.Move(w.Handle, i % 2 == 0 ? 1 : -1, 0);
                h.Events.Raise(w.Handle, (i++ % 3) switch { 0 => WindowEventKind.LocationChanged, 1 => WindowEventKind.ContentChanged, _ => WindowEventKind.ForegroundChanged });
                await Task.Yield();
            }
        });

        var workers = Enumerable.Range(0, 8).Select(t => Task.Run(() =>
        {
            var rnd = new Random(t);
            for (int i = 0; i < 150; i++)
            {
                var id = $"t{t}-{rnd.Next(10)}";
                var existing = session.GetRule(id);
                try
                {
                    switch (rnd.Next(5))
                    {
                        case 0 when existing is null:
                        case 1 when existing is null:
                            session.AddRule(Mask(id, "k" + rnd.Next(3)));
                            break;
                        case 0:
                            session.UpdateRule(existing with { Placement = OverlayPlacement.Cover(rnd.Next(4)) });
                            break;
                        case 1:
                            session.UpdateRule(existing with { Appearance = rnd.Next(2) == 0 ? MaskAppearance.Solid("#111") : HighlightAppearance.Create("#F00") });
                            break;
                        case 2 when existing is not null:
                            session.UpdateRule(existing with { Target = new FakeLocator("k" + rnd.Next(3)) });
                            break;
                        case 3 when existing is not null:
                            if (existing.Enabled) session.DisableRule(id); else session.EnableRule(id);
                            break;
                        default:
                            session.RemoveRule(id);
                            break;
                    }
                }
                catch (WindowVeilException ex) when (ex.Code == WindowVeilErrorCode.InvalidRule) { } // lost an add race
                catch (KeyNotFoundException) { } // lost a remove race
            }
        })).ToArray();

        await Task.WhenAll(workers).WaitAsync(TimeSpan.FromSeconds(15));
        stop.Cancel();
        await eventPump;
        await session.RefreshAsync();
        await session.RefreshAsync();

        var rules = session.Rules.ToDictionary(r => r.Id);
        var target = h.Windows.Get(w.Handle);
        foreach (var status in session.RuleStatuses)
        {
            var rule = rules[status.RuleId];
            var live = h.Renderer.Live.Where(v => v.RuleId == status.RuleId).ToArray();
            Assert.True(live.Length <= 1, $"{status.RuleId} has {live.Length} live visuals");
            if (!rule.Enabled)
            {
                Assert.Equal(RuleState.Disabled, status.State);
                Assert.All(live, v => Assert.False(v.IsShown));
                continue;
            }
            Assert.Equal(RuleState.Active, status.State);
            Assert.Equal(rule.Target, status.Resolution!.ResolvedBy);
            var visual = Assert.Single(live);
            Assert.True(visual.IsShown);
            Assert.Equal(rule.Appearance, visual.Request.Appearance);
            var expected = PlacementCalculator.Calculate(ClientRect(target, h.Resolver.DefaultClientRect), rule.Placement, target.Dpi);
            Assert.Equal([expected], status.OverlayBounds);
            Assert.Equal(expected, visual.CurrentFrame!.Bounds);
        }
        Assert.All(h.Renderer.Live, v => Assert.True(rules.ContainsKey(v.RuleId), $"visual of removed rule {v.RuleId} is alive"));
        Assert.Empty(logs.Errors);

        await h.Engine.DisposeAsync();
        Assert.Empty(h.Renderer.Live);
        Assert.Equal(0, h.Resolver.LiveStates);
        Assert.Equal(0, h.Events.ActiveCount);
    }

    [Fact]
    public async Task ManyAttachStartDisposeCycles_LeaveNoLiveResources()
    {
        await using var h = new EngineHarness();
        var w = h.Windows.Add();
        for (int i = 0; i < 200; i++)
        {
            var session = await h.Engine.AttachAsync(WindowSelector.ByProcessName("RetroCap"));
            session.AddRules([Mask("a"), Mask("b", "b"), Mask("c", ElementLocator.WindowRelative(1, 2, 3, 4))]);
            await session.StartAsync();
            if (i % 2 == 0) h.Events.Raise(w.Handle, WindowEventKind.LocationChanged);
            await session.DisposeAsync();
        }

        Assert.Equal(600, h.Renderer.Created.Count);
        Assert.Empty(h.Renderer.Live);
        Assert.Equal(0, h.Resolver.LiveStates);
        Assert.Equal(0, h.Events.ActiveCount);
        Assert.Empty(h.Engine.Sessions);
    }
}

/// <summary>Captures error-level log entries so tests can assert that no pass failed internally.</summary>
internal sealed class CapturingLoggerFactory : ILoggerFactory
{
    private readonly ConcurrentQueue<string> _errors = new();

    public IReadOnlyList<string> Errors => _errors.ToArray();

    public ILogger CreateLogger(string categoryName) => new Logger(this);

    public void AddProvider(ILoggerProvider provider) { }

    public void Dispose() { }

    private sealed class Logger(CapturingLoggerFactory owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Error) owner._errors.Enqueue(formatter(state, exception) + (exception is null ? "" : ": " + exception));
        }
    }
}
