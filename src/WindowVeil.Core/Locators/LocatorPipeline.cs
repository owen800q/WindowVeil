namespace WindowVeil.Locators;

/// <summary>
/// Dispatches locators to registered resolvers and implements <see cref="FallbackLocator"/> chains.
/// The returned resolution always names the strategy that produced it (<see cref="LocatorResolution.ResolvedBy"/>)
/// and lists every attempt.
/// </summary>
public sealed class LocatorPipeline
{
    private readonly IReadOnlyList<ILocatorResolver> _resolvers;

    /// <summary>Creates a pipeline over the given resolvers (first match wins).</summary>
    public LocatorPipeline(IEnumerable<ILocatorResolver> resolvers)
    {
        ArgumentNullException.ThrowIfNull(resolvers);
        _resolvers = resolvers.ToArray();
    }

    /// <summary>Registered resolvers.</summary>
    public IReadOnlyList<ILocatorResolver> Resolvers => _resolvers;

    /// <summary>Whether some resolver can handle the locator (recursively for fallback chains).</summary>
    public bool Supports(ElementLocator locator) => locator is FallbackLocator f
        ? f.Locators.Any(Supports)
        : _resolvers.Any(r => r.CanResolve(locator));

    /// <summary>Resolves a locator. Never throws except for cancellation.</summary>
    public async ValueTask<LocatorResolution> ResolveAsync(ElementLocator locator, LocatorContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(locator);
        ArgumentNullException.ThrowIfNull(context);
        if (locator is FallbackLocator chain) return await ResolveChainAsync(chain, context, cancellationToken).ConfigureAwait(false);

        var resolver = _resolvers.FirstOrDefault(r => r.CanResolve(locator));
        if (resolver is null)
            return LocatorResolution.Failed(locator, LocatorStatus.Unsupported,
                $"No resolver is registered for '{locator.Kind}' locators. Register the matching WindowVeil package.");
        try
        {
            var result = await resolver.ResolveAsync(locator, context, cancellationToken).ConfigureAwait(false);
            return result.ResolvedBy is null ? result with { ResolvedBy = locator } : result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return LocatorResolution.Failed(locator, LocatorStatus.Error, $"{locator.Kind} resolver failed: {ex.Message}", ex);
        }
    }

    private async ValueTask<LocatorResolution> ResolveChainAsync(FallbackLocator chain, LocatorContext context, CancellationToken ct)
    {
        var attempts = new List<LocatorResolution>(chain.Locators.Count);
        // Reuse the previous winner's state only for the same strategy.
        var previous = context.Previous;
        foreach (var candidate in chain.Locators)
        {
            var ctx = previous is not null && Equals(previous.ResolvedBy, candidate)
                ? context
                : new LocatorContext(context.Window, context.Reason)
                {
                    Capture = context.Capture, Logger = context.Logger, OverlayWindows = context.OverlayWindows, Time = context.Time, RuleId = context.RuleId,
                    Previous = previous?.Attempts.FirstOrDefault(a => Equals(a.ResolvedBy, candidate)),
                };
            var result = await ResolveAsync(candidate, ctx, ct).ConfigureAwait(false);
            attempts.Add(result);
            if (result.IsResolved) return result with { Attempts = attempts };
            if (result.Status == LocatorStatus.Error && !chain.ContinueOnError) break;
        }

        // Report the most informative failure: prefer errors/rejections over plain "not found".
        var worst = attempts.OrderByDescending(a => Severity(a.Status)).First();
        var summary = string.Join("; ", attempts.Select(a => $"{a.ResolvedBy?.Kind}: {a.Status}{(a.Message is null ? "" : " (" + a.Message + ")")}"));
        return LocatorResolution.Failed(chain, worst.Status, "All strategies failed: " + summary, worst.Exception) with { Attempts = attempts };
    }

    private static int Severity(LocatorStatus s) => s switch
    {
        LocatorStatus.Error => 5,
        LocatorStatus.Rejected => 4,
        LocatorStatus.Offscreen => 3,
        LocatorStatus.Unavailable => 2,
        LocatorStatus.NotFound => 1,
        _ => 0,
    };
}
