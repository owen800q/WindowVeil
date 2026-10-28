using WindowVeil.Appearance;
using WindowVeil.Geometry;
using WindowVeil.Platform;

namespace WindowVeil.UnitTests.Fakes;

/// <summary>An engine wired to in-memory fakes. Timers are effectively disabled so tests drive the session explicitly.</summary>
internal sealed class EngineHarness : IAsyncDisposable
{
    /// <summary>Options that disable every periodic timer; tests use RefreshAsync, events and WaitForRuleAsync instead.</summary>
    public static readonly SessionOptions Quiet = new()
    {
        ElementRefreshInterval = TimeSpan.Zero,
        TargetCheckInterval = TimeSpan.FromHours(1),
        ReconnectPollInterval = TimeSpan.FromHours(1),
        GeometryPollInterval = TimeSpan.FromHours(1),
        ResolveTimeout = TimeSpan.FromSeconds(10),
    };

    public static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(5);

    public EngineHarness(bool withEventSource = true, bool withRenderer = true, Action<WindowVeilBuilder>? configure = null)
    {
        Windows = new FakeWindowSystem();
        Events = new FakeEventSource();
        Renderer = new FakeRenderer();
        Resolver = new FakeResolver();
        var builder = WindowVeilEngine.CreateBuilder().UseWindowSystem(Windows).AddResolver(Resolver).ConfigureSessions(_ => Quiet);
        if (withEventSource) builder.UseEventSource(Events);
        if (withRenderer) builder.AddRenderer(Renderer);
        configure?.Invoke(builder);
        Engine = builder.Build();
    }

    public FakeWindowSystem Windows { get; }
    public FakeEventSource Events { get; }
    public FakeRenderer Renderer { get; }
    public FakeResolver Resolver { get; }
    public WindowVeilEngine Engine { get; }

    public static OverlayRule Mask(string id, string key = "a") => new()
    {
        Id = id,
        Target = new FakeLocator(key),
        Appearance = MaskAppearance.Solid("#202020"),
    };

    public static OverlayRule Mask(string id, Locators.ElementLocator locator) => new()
    {
        Id = id,
        Target = locator,
        Appearance = MaskAppearance.Solid("#202020"),
    };

    /// <summary>Adds a default RetroCap window and attaches a session to it.</summary>
    public async Task<(TargetWindowInfo Window, OverlaySession Session)> AttachAsync(SessionOptions? options = null)
    {
        var w = Windows.Add();
        var s = await Engine.AttachAsync(WindowSelector.ByProcessName("RetroCap"), options ?? Quiet);
        return (w, s);
    }

    public static PixelRect ClientRect(TargetWindowInfo w, PixelRect relative) => relative.Offset(w.ClientBounds.X, w.ClientBounds.Y);

    public ValueTask DisposeAsync() => Engine.DisposeAsync();
}

internal static class Wait
{
    /// <summary>Polls a condition (for effects that have no completion signal, such as a background pass finishing).</summary>
    public static async Task UntilAsync(Func<bool> condition, string because, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? EngineHarness.WaitTimeout);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Condition not met: " + because);
            await Task.Delay(5);
        }
    }

    public static Task<RuleStatus> ForStateAsync(this OverlaySession session, string ruleId, RuleState state) =>
        session.WaitForRuleAsync(ruleId, s => s.State == state, EngineHarness.WaitTimeout);

    public static Task ForSessionStateAsync(this OverlaySession session, SessionState state) =>
        UntilAsync(() => session.State == state, $"session state {state} (is {session.State})");
}
