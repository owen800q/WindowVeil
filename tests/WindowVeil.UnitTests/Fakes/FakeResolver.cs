using System.Collections.Concurrent;
using WindowVeil.Geometry;
using WindowVeil.Locators;

namespace WindowVeil.UnitTests.Fakes;

/// <summary>A locator kind only <see cref="FakeResolver"/> understands (stands in for UI Automation/OCR).</summary>
internal sealed record FakeLocator(string Key) : ElementLocator
{
    public override string Kind => "fake";
}

/// <summary>Disposable resolver state (stands in for a cached UI Automation element).</summary>
internal sealed class FakeState : IDisposable
{
    private int _disposed;

    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    public void Dispose() => Interlocked.Exchange(ref _disposed, 1);
}

/// <summary>
/// Scriptable resolver for <see cref="FakeLocator"/>. By default every key resolves to a rectangle relative to the
/// client area (so it follows the window) and attaches a fresh <see cref="FakeState"/>.
/// </summary>
internal sealed class FakeResolver : ILocatorResolver
{
    private readonly ConcurrentDictionary<string, Func<FakeLocator, LocatorContext, LocatorResolution>> _scripts = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource> _blocks = new();
    private readonly ConcurrentDictionary<string, int> _calls = new();
    private readonly ConcurrentQueue<(string Key, ResolveReason Reason)> _log = new();
    private readonly ConcurrentQueue<FakeState> _states = new();

    /// <summary>Client-relative rectangle used for keys without a script.</summary>
    public PixelRect DefaultClientRect { get; set; } = new(10, 20, 100, 30);

    /// <summary>Whether results carry a <see cref="FakeState"/>.</summary>
    public bool AttachState { get; set; } = true;

    /// <summary>Signalled whenever a blocked resolution starts waiting.</summary>
    public SemaphoreSlim BlockedEntered { get; } = new(0);

    public int TotalCalls => _calls.Values.Sum();

    public IReadOnlyList<(string Key, ResolveReason Reason)> Log => _log.ToArray();

    public IReadOnlyList<FakeState> States => _states.ToArray();

    public int LiveStates => _states.Count(s => !s.IsDisposed);

    public int Calls(string key) => _calls.TryGetValue(key, out var n) ? n : 0;

    public bool CanResolve(ElementLocator locator) => locator is FakeLocator;

    public void Script(string key, Func<FakeLocator, LocatorContext, LocatorResolution> script) => _scripts[key] = script;

    /// <summary>The key resolves to <paramref name="clientRect"/> offset by the current client origin.</summary>
    public void ResolveTo(string key, PixelRect clientRect) => _scripts[key] = (l, c) => Relative(l, c, clientRect);

    public void NotFound(string key, string message = "Element not found.") =>
        _scripts[key] = (l, _) => LocatorResolution.Failed(l, LocatorStatus.NotFound, message);

    public void Throw(string key, string message = "boom") => _scripts[key] = (_, _) => throw new InvalidOperationException(message);

    /// <summary>Resolutions of <paramref name="key"/> wait (honoring cancellation) until <see cref="Release"/>.</summary>
    public void Block(string key) => _blocks[key] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Release(string key)
    {
        if (_blocks.TryRemove(key, out var tcs)) tcs.TrySetResult();
    }

    public async ValueTask<LocatorResolution> ResolveAsync(ElementLocator locator, LocatorContext context, CancellationToken cancellationToken)
    {
        var l = (FakeLocator)locator;
        _calls.AddOrUpdate(l.Key, 1, (_, n) => n + 1);
        _log.Enqueue((l.Key, context.Reason));
        if (_blocks.TryGetValue(l.Key, out var gate))
        {
            BlockedEntered.Release();
            await gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        cancellationToken.ThrowIfCancellationRequested();
        var result = _scripts.TryGetValue(l.Key, out var script) ? script(l, context) : Relative(l, context, DefaultClientRect);
        return result;
    }

    private LocatorResolution Relative(FakeLocator l, LocatorContext c, PixelRect clientRect)
    {
        var bounds = clientRect.Offset(c.Window.ClientBounds.X, c.Window.ClientBounds.Y);
        FakeState? state = null;
        if (AttachState)
        {
            state = new FakeState();
            _states.Enqueue(state);
        }
        return LocatorResolution.Found(l, bounds) with { WindowBounds = c.Window.Bounds, State = state };
    }
}
