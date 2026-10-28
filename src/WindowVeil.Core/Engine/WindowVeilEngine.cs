using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using WindowVeil.Appearance;
using WindowVeil.Locators;
using WindowVeil.Platform;
using WindowVeil.Rendering;

namespace WindowVeil;

/// <summary>Services used by <see cref="WindowVeilEngine"/>. Usually built with <see cref="WindowVeilBuilder"/>.</summary>
public sealed record WindowVeilEngineOptions
{
    /// <summary>Window enumeration and inspection (required).</summary>
    public required IWindowSystem WindowSystem { get; init; }

    /// <summary>Window change notifications. When null, sessions fall back to polling.</summary>
    public IWindowEventSource? EventSource { get; init; }

    /// <summary>Capture backend for image-based locators (OCR).</summary>
    public IScreenCapture? Capture { get; init; }

    /// <summary>Locator resolvers. The geometric resolver is always added.</summary>
    public IReadOnlyList<ILocatorResolver> Resolvers { get; init; } = [];

    /// <summary>Overlay renderers, in priority order.</summary>
    public IReadOnlyList<IOverlayRenderer> Renderers { get; init; } = [];

    /// <summary>Appearance factories for <see cref="RegisteredAppearance"/> and profiles.</summary>
    public AppearanceRegistry Appearances { get; init; } = new();

    /// <summary>Logging.</summary>
    public ILoggerFactory LoggerFactory { get; init; } = NullLoggerFactory.Instance;

    /// <summary>Default options for new sessions.</summary>
    public SessionOptions DefaultSessionOptions { get; init; } = new();

    /// <summary>Clock (overridable in tests).</summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    /// <summary>Additional components owned and disposed by the engine.</summary>
    public IReadOnlyList<object> OwnedComponents { get; init; } = [];
}

/// <summary>
/// Entry point of WindowVeil. Owns renderers and resolvers and creates <see cref="OverlaySession"/> instances,
/// one per attached target window. Thread-safe. Dispose it to remove every overlay.
/// </summary>
/// <example>
/// <code>
/// await using var engine = WindowVeilEngine.CreateBuilder().UseWindowsDesktop().Build();
/// var session = await engine.AttachAsync(WindowSelector.ByProcessName("RetroCap"));
/// session.AddRule(MaskRule.Create(ElementLocator.ByAutomationId("StartupCheck"), MaskAppearance.Solid("#202020")));
/// await session.StartAsync();
/// </code>
/// </example>
public sealed class WindowVeilEngine : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly List<OverlaySession> _sessions = [];
    private readonly ILogger _logger;
    private bool _disposed;

    /// <summary>Creates an engine from explicit services.</summary>
    public WindowVeilEngine(WindowVeilEngineOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.WindowSystem is null) throw new ArgumentException("A window system is required.", nameof(options));
        Options = options;
        _logger = options.LoggerFactory.CreateLogger<WindowVeilEngine>();
        var resolvers = options.Resolvers.ToList();
        if (!resolvers.Any(r => r is GeometricLocatorResolver)) resolvers.Add(new GeometricLocatorResolver());
        Locators = new LocatorPipeline(resolvers);
    }

    /// <summary>Starts building an engine; platform packages add extension methods such as <c>UseWindowsDesktop()</c>.</summary>
    public static WindowVeilBuilder CreateBuilder() => new();

    /// <summary>Engine services.</summary>
    public WindowVeilEngineOptions Options { get; }

    /// <summary>The locator pipeline (also usable directly, for example by an inspector UI).</summary>
    public LocatorPipeline Locators { get; }

    /// <summary>Appearance factories.</summary>
    public AppearanceRegistry Appearances => Options.Appearances;

    /// <summary>Window system.</summary>
    public IWindowSystem WindowSystem => Options.WindowSystem;

    /// <summary>Live sessions.</summary>
    public IReadOnlyList<OverlaySession> Sessions { get { lock (_gate) return _sessions.ToArray(); } }

    /// <summary>Lists windows matching a selector, best match first.</summary>
    public IReadOnlyList<TargetWindowInfo> FindWindows(WindowSelector selector)
    {
        ArgumentNullException.ThrowIfNull(selector);
        var all = WindowSystem.EnumerateWindows();
        var best = selector.SelectBest(all);
        return all.Where(selector.Matches).OrderByDescending(w => w == best).ThenByDescending(w => w.Bounds.Area).ToArray();
    }

    /// <summary>
    /// Attaches to the best window matching <paramref name="selector"/>. Waits up to <see cref="SessionOptions.AttachTimeout"/>.
    /// Throws <see cref="WindowVeilException"/> (<see cref="WindowVeilErrorCode.TargetWindowNotFound"/>) when nothing matches,
    /// unless <see cref="SessionOptions.AllowDeferredAttach"/> is set.
    /// </summary>
    public async Task<OverlaySession> AttachAsync(WindowSelector selector, SessionOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selector);
        ThrowIfDisposed();
        options ??= Options.DefaultSessionOptions;
        var deadline = Options.TimeProvider.GetTimestamp();
        TargetWindowInfo? window;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            window = selector.SelectBest(WindowSystem.EnumerateWindows());
            if (window is not null) break;
            if (Options.TimeProvider.GetElapsedTime(deadline) >= options.AttachTimeout) break;
            await Task.Delay(TimeSpan.FromMilliseconds(100), Options.TimeProvider, cancellationToken).ConfigureAwait(false);
        }

        if (window is null && !options.AllowDeferredAttach)
            throw new WindowVeilException(WindowVeilErrorCode.TargetWindowNotFound, $"No window matches {selector}.");

        var session = new OverlaySession(this, selector, window, options, Options.LoggerFactory.CreateLogger<OverlaySession>());
        lock (_gate)
        {
            ThrowIfDisposed();
            _sessions.Add(session);
        }
        _logger.LogInformation("Attached session to {Window}", window?.ToString() ?? "(deferred) " + selector);
        return session;
    }

    /// <summary>Attaches to a specific window handle (no reconnection to new instances).</summary>
    public Task<OverlaySession> AttachAsync(nint windowHandle, SessionOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (WindowSystem.GetWindow(windowHandle) is null)
            throw new WindowVeilException(WindowVeilErrorCode.InvalidWindowHandle, $"0x{windowHandle:X} is not a valid window handle.");
        return AttachAsync(WindowSelector.ByHandle(windowHandle), options, cancellationToken);
    }

    /// <summary>Resolves a locator against a window once, without creating overlays (used by inspectors and tests).</summary>
    public ValueTask<LocatorResolution> ResolveAsync(TargetWindowInfo window, ElementLocator locator, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(locator);
        var ctx = new LocatorContext(window, ResolveReason.Manual)
        {
            Capture = Options.Capture,
            Logger = _logger,
            OverlayWindows = OverlayWindowRegistry.Snapshot(),
            Time = Options.TimeProvider,
        };
        return Locators.ResolveAsync(locator, ctx, cancellationToken);
    }

    internal void OnSessionDisposed(OverlaySession session)
    {
        lock (_gate) _sessions.Remove(session);
    }

    internal IOverlayRenderer? FindRenderer(OverlayAppearance appearance) => Options.Renderers.FirstOrDefault(r => r.CanRender(appearance));

    /// <summary>Disposes every session, then renderers, resolvers and owned components.</summary>
    public async ValueTask DisposeAsync()
    {
        OverlaySession[] sessions;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            sessions = _sessions.ToArray();
        }
        foreach (var s in sessions)
        {
            try { await s.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to dispose session"); }
        }
        var owned = new List<object>();
        owned.AddRange(Options.Renderers);
        owned.AddRange(Options.Resolvers);
        if (Options.EventSource is not null) owned.Add(Options.EventSource);
        if (Options.Capture is not null) owned.Add(Options.Capture);
        owned.Add(Options.WindowSystem);
        owned.AddRange(Options.OwnedComponents);
        foreach (var o in owned.Distinct())
        {
            try
            {
                if (o is IAsyncDisposable ad) await ad.DisposeAsync().ConfigureAwait(false);
                else if (o is IDisposable d) d.Dispose();
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to dispose {Component}", o.GetType().Name); }
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}

/// <summary>Fluent builder for <see cref="WindowVeilEngine"/>.</summary>
public sealed class WindowVeilBuilder
{
    private readonly List<ILocatorResolver> _resolvers = [];
    private readonly List<IOverlayRenderer> _renderers = [];
    private readonly List<object> _owned = [];

    /// <summary>Window system.</summary>
    public IWindowSystem? WindowSystem { get; set; }

    /// <summary>Window event source.</summary>
    public IWindowEventSource? EventSource { get; set; }

    /// <summary>Capture backend.</summary>
    public IScreenCapture? Capture { get; set; }

    /// <summary>Appearance registry.</summary>
    public AppearanceRegistry Appearances { get; } = new();

    /// <summary>Logger factory.</summary>
    public ILoggerFactory LoggerFactory { get; set; } = NullLoggerFactory.Instance;

    /// <summary>Default session options.</summary>
    public SessionOptions SessionOptions { get; set; } = new();

    /// <summary>Clock.</summary>
    public TimeProvider TimeProvider { get; set; } = TimeProvider.System;

    /// <summary>Registered resolvers.</summary>
    public IReadOnlyList<ILocatorResolver> Resolvers => _resolvers;

    /// <summary>Registered renderers.</summary>
    public IReadOnlyList<IOverlayRenderer> Renderers => _renderers;

    /// <summary>Sets the window system.</summary>
    public WindowVeilBuilder UseWindowSystem(IWindowSystem windowSystem) { WindowSystem = windowSystem ?? throw new ArgumentNullException(nameof(windowSystem)); return this; }

    /// <summary>Sets the window event source.</summary>
    public WindowVeilBuilder UseEventSource(IWindowEventSource eventSource) { EventSource = eventSource ?? throw new ArgumentNullException(nameof(eventSource)); return this; }

    /// <summary>Sets the capture backend.</summary>
    public WindowVeilBuilder UseCapture(IScreenCapture capture) { Capture = capture ?? throw new ArgumentNullException(nameof(capture)); return this; }

    /// <summary>Adds a resolver (earlier resolvers win for the same locator kind).</summary>
    public WindowVeilBuilder AddResolver(ILocatorResolver resolver) { _resolvers.Add(resolver ?? throw new ArgumentNullException(nameof(resolver))); return this; }

    /// <summary>Adds a renderer. Renderers added first take precedence for appearances several can draw.</summary>
    public WindowVeilBuilder AddRenderer(IOverlayRenderer renderer) { _renderers.Add(renderer ?? throw new ArgumentNullException(nameof(renderer))); return this; }

    /// <summary>Registers a component the engine should dispose.</summary>
    public WindowVeilBuilder AddOwnedComponent(object component) { _owned.Add(component ?? throw new ArgumentNullException(nameof(component))); return this; }

    /// <summary>Sets the logger factory.</summary>
    public WindowVeilBuilder UseLoggerFactory(ILoggerFactory loggerFactory) { LoggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory)); return this; }

    /// <summary>Configures default session options.</summary>
    public WindowVeilBuilder ConfigureSessions(Func<SessionOptions, SessionOptions> configure) { SessionOptions = configure(SessionOptions); return this; }

    /// <summary>Registers an appearance factory.</summary>
    public WindowVeilBuilder RegisterAppearance(string id, Func<IReadOnlyDictionary<string, string>, OverlayAppearance> factory) { Appearances.Register(id, factory); return this; }

    /// <summary>Builds the engine.</summary>
    /// <exception cref="WindowVeilException">No window system was configured.</exception>
    public WindowVeilEngine Build()
    {
        if (WindowSystem is null)
            throw new WindowVeilException(WindowVeilErrorCode.PlatformUnavailable,
                "No window system is configured. On Windows call UseWindowsDesktop() from the WindowVeil.Tracking package, or UseWindowSystem(...).");
        return new WindowVeilEngine(new WindowVeilEngineOptions
        {
            WindowSystem = WindowSystem,
            EventSource = EventSource,
            Capture = Capture,
            Resolvers = _resolvers.ToArray(),
            Renderers = _renderers.ToArray(),
            Appearances = Appearances,
            LoggerFactory = LoggerFactory,
            DefaultSessionOptions = SessionOptions,
            TimeProvider = TimeProvider,
            OwnedComponents = _owned.ToArray(),
        });
    }
}
