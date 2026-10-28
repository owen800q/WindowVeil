using System.Windows.Automation;
using WindowVeil.Locators;

namespace WindowVeil.Automation;

/// <summary>Outcome of a bounded tree search.</summary>
internal sealed class SearchOutcome
{
    /// <summary>The selected element (with cached search properties), or null.</summary>
    public AutomationElement? Element { get; init; }

    /// <summary>Number of matching elements seen before the search stopped.</summary>
    public int MatchCount { get; init; }

    /// <summary>Number of elements visited.</summary>
    public int Visited { get; init; }

    /// <summary>The search stopped because it reached the node bound.</summary>
    public bool HitNodeLimit { get; init; }

    /// <summary>Elements at the depth bound were not expanded.</summary>
    public bool HitDepthLimit { get; init; }

    /// <summary>Failure description when an ancestor selector could not be resolved.</summary>
    public string? AncestorFailure { get; init; }

    /// <summary>Human-readable explanation of a failed search (selector, nodes searched, bounds hit).</summary>
    public string Describe(UiAutomationLocator selector)
    {
        if (AncestorFailure is not null) return AncestorFailure;
        var sb = new System.Text.StringBuilder();
        if (MatchCount == 0) sb.Append("No element matched ").Append(selector);
        else sb.Append("Only ").Append(MatchCount).Append(" element(s) matched ").Append(selector).Append(" but Index=").Append(selector.Index).Append(" was requested");
        sb.Append(" (searched ").Append(Visited).Append(" node(s)");
        if (HitNodeLimit) sb.Append("; stopped at the MaxNodes bound of ").Append(selector.MaxNodes);
        if (HitDepthLimit) sb.Append("; elements deeper than the MaxDepth bound of ").Append(selector.MaxDepth).Append(" were not searched");
        sb.Append(").");
        return sb.ToString();
    }
}

/// <summary>Bounded breadth-first search over the UI Automation tree. Must run on the UIA thread.</summary>
internal static class ElementSearch
{
    /// <summary>
    /// Finds the <see cref="UiAutomationLocator.Index"/>-th element matching <paramref name="selector"/> below
    /// <paramref name="root"/> in breadth-first order. When the selector has an ancestor, the ancestor is resolved first
    /// (from <paramref name="root"/>) and the search continues within it.
    /// </summary>
    /// <param name="root">The window element.</param>
    /// <param name="selector">Selector.</param>
    /// <param name="request">Cache request (defines the tree view and the cached properties).</param>
    /// <param name="overlays">Overlay window handles to skip.</param>
    /// <param name="cancellationToken">Checked between nodes.</param>
    public static SearchOutcome Find(AutomationElement root, UiAutomationLocator selector, CacheRequest request,
        IReadOnlySet<nint>? overlays, CancellationToken cancellationToken)
    {
        var scope = root;
        bool includeScope = true;
        int visitedAncestors = 0;
        if (selector.Ancestor is { } ancestorSelector)
        {
            var ancestor = Find(root, ancestorSelector, request, overlays, cancellationToken);
            visitedAncestors = ancestor.Visited;
            if (ancestor.Element is null)
                return new SearchOutcome
                {
                    Visited = ancestor.Visited,
                    AncestorFailure = $"Ancestor of {selector} not found: {ancestor.Describe(ancestorSelector)}",
                };
            scope = ancestor.Element;
            includeScope = false;
        }

        var outcome = Bfs(scope, includeScope, selector.MaxDepth, selector.MaxNodes, request, overlays, cancellationToken,
            el => UiaHelpers.MatchesCached(el, selector), stopAfter: selector.Index + 1);
        return new SearchOutcome
        {
            Element = outcome.Matches.Count > selector.Index ? outcome.Matches[selector.Index] : null,
            MatchCount = outcome.Matches.Count,
            Visited = outcome.Visited + visitedAncestors,
            HitNodeLimit = outcome.HitNodeLimit,
            HitDepthLimit = outcome.HitDepthLimit,
        };
    }

    /// <summary>Result of <see cref="Bfs"/>.</summary>
    public sealed record BfsResult(List<AutomationElement> Matches, int Visited, bool HitNodeLimit, bool HitDepthLimit);

    /// <summary>
    /// Generic bounded BFS. <paramref name="visit"/> returns whether the element is a match. When
    /// <paramref name="expandMatches"/> is false, children of matching elements are not searched.
    /// </summary>
    public static BfsResult Bfs(AutomationElement scope, bool includeScope, int maxDepth, int maxNodes, CacheRequest request,
        IReadOnlySet<nint>? overlays, CancellationToken cancellationToken, Func<AutomationElement, bool> visit,
        int stopAfter = int.MaxValue, bool expandMatches = true)
    {
        var matches = new List<AutomationElement>();
        int visited = 0;
        bool hitNodes = false, hitDepth = false;

        AutomationElement start;
        try { start = scope.GetUpdatedCache(request); }
        catch (Exception ex) when (UiaHelpers.IsStale(ex)) { return new BfsResult(matches, 0, false, false); }

        var queue = new Queue<(AutomationElement Element, int Depth)>();
        queue.Enqueue((start, 0));
        while (queue.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (visited >= maxNodes) { hitNodes = true; break; }
            var (element, depth) = queue.Dequeue();
            visited++;

            bool isMatch;
            try
            {
                if (depth > 0 && UiaHelpers.IsOverlay(element, overlays)) continue;
                isMatch = (depth > 0 || includeScope) && visit(element);
            }
            catch (Exception ex) when (UiaHelpers.IsStale(ex))
            {
                continue; // the element disappeared while we were looking at it
            }

            if (isMatch)
            {
                matches.Add(element);
                if (matches.Count >= stopAfter) break;
                if (!expandMatches) continue;
            }

            if (depth >= maxDepth) { hitDepth = true; continue; }
            foreach (var child in UiaHelpers.GetChildren(element, request))
                queue.Enqueue((child, depth + 1));
        }
        return new BfsResult(matches, visited, hitNodes, hitDepth);
    }
}
