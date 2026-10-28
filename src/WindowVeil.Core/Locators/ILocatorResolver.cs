using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using WindowVeil.Platform;

namespace WindowVeil.Locators;

/// <summary>Why a resolution was requested; resolvers can use it to skip expensive work.</summary>
public enum ResolveReason
{
    /// <summary>First resolution of a rule.</summary>
    Initial,

    /// <summary>The target window moved or resized.</summary>
    WindowChanged,

    /// <summary>Content inside the window may have changed.</summary>
    ContentChanged,

    /// <summary>Periodic refresh.</summary>
    Poll,

    /// <summary>The target reconnected after a restart.</summary>
    Reconnected,

    /// <summary>Explicit request by the application.</summary>
    Manual,
}

/// <summary>Everything a resolver needs to locate an element in the current target window.</summary>
public sealed class LocatorContext
{
    /// <summary>Creates a context.</summary>
    public LocatorContext(TargetWindowInfo window, ResolveReason reason)
    {
        Window = window ?? throw new ArgumentNullException(nameof(window));
        Reason = reason;
    }

    /// <summary>Current snapshot of the target window.</summary>
    public TargetWindowInfo Window { get; }

    /// <summary>Why resolution is happening.</summary>
    public ResolveReason Reason { get; }

    /// <summary>Id of the rule being resolved, or null for one-off resolutions (inspectors, tests).</summary>
    public string? RuleId { get; init; }

    /// <summary>The previous resolution of the same rule and locator, if any (for caching and stale-element reuse).</summary>
    public LocatorResolution? Previous { get; init; }

    /// <summary>Handles of WindowVeil's own overlay windows, which resolvers must ignore.</summary>
    public IReadOnlySet<nint> OverlayWindows { get; init; } = new HashSet<nint>();

    /// <summary>Optional capture backend for image-based locators.</summary>
    public IScreenCapture? Capture { get; init; }

    /// <summary>Logger.</summary>
    public ILogger Logger { get; init; } = NullLogger.Instance;

    /// <summary>Monotonic clock, overridable for tests.</summary>
    public TimeProvider Time { get; init; } = TimeProvider.System;
}

/// <summary>
/// Resolves one or more <see cref="ElementLocator"/> kinds to screen geometry. Implementations must be thread-safe
/// and must honor cancellation. Expected failures are returned as <see cref="LocatorResolution"/> values, not thrown.
/// </summary>
public interface ILocatorResolver
{
    /// <summary>Whether this resolver handles the locator.</summary>
    bool CanResolve(ElementLocator locator);

    /// <summary>Resolves the locator against the context's target window.</summary>
    ValueTask<LocatorResolution> ResolveAsync(ElementLocator locator, LocatorContext context, CancellationToken cancellationToken);
}
