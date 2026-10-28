using System.Windows.Automation;
using System.Windows.Automation.Text;
using WindowVeil.Geometry;
using WindowVeil.Locators;
using WindowVeil.Text;

namespace WindowVeil.Automation;

/// <summary>Locates text through TextPattern ranges, falling back to element names. Must run on the UIA thread.</summary>
internal static class TextSearch
{
    /// <summary>Control types whose Name is their visible label (used by the Name fallback).</summary>
    private static readonly HashSet<string> LabelControlTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Text", "CheckBox", "Button", "Hyperlink", "RadioButton", "ListItem", "MenuItem", "TabItem", "TreeItem",
        "HeaderItem", "Header", "DataItem", "SplitButton", "ToolTip", "StatusBar", "Group", "TitleBar",
    };

    /// <summary>Result of a text search before it is turned into a <see cref="LocatorResolution"/>.</summary>
    public sealed record Result(LocatorStatus Status, IReadOnlyList<PixelRect> Bounds, string Message, double Confidence);

    public static Result Find(AutomationElement scope, TextLocator locator, UiAutomationOptions options, CacheRequest request,
        IReadOnlySet<nint>? overlays, CancellationToken ct, nint targetWindow = 0, PixelRect windowBounds = default)
    {
        var query = locator.Query;
        var nameCandidates = new List<AutomationElement>();
        var bfs = ElementSearch.Bfs(scope, includeScope: true, options.TextSearchMaxDepth, options.TextSearchMaxNodes, request, overlays, ct,
            visit: el =>
            {
                if (UiaHelpers.CachedSupportsTextPattern(el)) return true;
                var c = el.Cached;
                if (LabelControlTypes.Contains(UiaHelpers.ControlTypeName(c.ControlType)) && query.IsMatch(c.Name))
                    nameCandidates.Add(el);
                return false;
            },
            expandMatches: false);

        var rects = new List<PixelRect>();
        int textElements = bfs.Matches.Count, matchedElements = 0, matchesWithoutGeometry = 0, unverified = 0, truncated = 0, failedElements = 0;
        int totalMatches = 0;
        bool done = false;

        foreach (var element in bfs.Matches)
        {
            if (done) break;
            ct.ThrowIfCancellationRequested();
            try
            {
                if (element.GetCurrentPattern(TextPattern.Pattern) is not TextPattern pattern) continue;
                var document = pattern.DocumentRange;
                string text = document.GetText(options.MaxTextLength) ?? "";
                if (text.Length >= options.MaxTextLength) truncated++;
                var matches = query.FindMatches(text);
                if (matches.Count == 0) continue;
                matchedElements++;

                // Line rectangles are clipped to the element's (visible) bounds so text scrolled out of a viewport is
                // never reported at its virtual position. Offscreen elements have no geometry at all.
                var cached = element.Cached;
                if (cached.IsOffscreen || !UiaHelpers.TryToPixelRect(cached.BoundingRectangle, out var clip) ||
                    (clip = UiaHelpers.ClipToVisible(element, clip, targetWindow, windowBounds)).IsEmpty)
                {
                    matchesWithoutGeometry += matches.Count;
                    continue;
                }
                foreach (var match in matches)
                {
                    ct.ThrowIfCancellationRequested();
                    if (match.Length <= 0) continue;
                    if (++totalMatches > options.MaxTextMatches) { done = true; break; }
                    var range = CreateRange(document, text, match, out bool verified);
                    if (!verified) unverified++;
                    var lineRects = ToPixelRects(range?.GetBoundingRectangles(), clip);
                    if (lineRects.Count == 0) { matchesWithoutGeometry++; continue; }
                    rects.AddRange(lineRects); // one rectangle per line: multi-line matches keep every line
                    if (!locator.AllMatches) { done = true; break; }
                }
            }
            catch (Exception ex) when (UiaHelpers.IsStale(ex))
            {
                failedElements++;
            }
        }

        var notes = new List<string>();
        if (matchesWithoutGeometry > 0) notes.Add($"{matchesWithoutGeometry} match(es) had no on-screen geometry and were skipped");
        if (unverified > 0) notes.Add($"{unverified} range(s) could not be verified against the document text");
        if (truncated > 0) notes.Add($"{truncated} document(s) were truncated at {options.MaxTextLength} characters");
        if (failedElements > 0) notes.Add($"{failedElements} element(s) became unavailable");
        if (totalMatches > options.MaxTextMatches) notes.Add($"stopped after {options.MaxTextMatches} matches");
        if (bfs.HitNodeLimit) notes.Add($"search stopped at the node bound of {options.TextSearchMaxNodes}");
        string Notes() => notes.Count == 0 ? "" : " (" + string.Join("; ", notes) + ")";

        if (rects.Count > 0)
            return new Result(LocatorStatus.Resolved, rects,
                $"Matched {query} via TextPattern in {matchedElements} element(s), {rects.Count} line rectangle(s){Notes()}.",
                unverified > 0 ? 0.8 : 1.0);

        // Fallback: elements whose Name matches (labels, check boxes, buttons, ...), using the element bounds.
        int offscreenNames = 0;
        foreach (var element in nameCandidates)
        {
            var c = element.Cached;
            if (c.IsOffscreen || !UiaHelpers.TryToPixelRect(c.BoundingRectangle, out var r) ||
                (r = UiaHelpers.ClipToVisible(element, r, targetWindow, windowBounds)).IsEmpty) { offscreenNames++; continue; }
            rects.Add(r);
            if (!locator.AllMatches) break;
        }
        if (offscreenNames > 0) notes.Add($"{offscreenNames} element(s) with a matching Name were offscreen");
        if (rects.Count > 0)
            return new Result(LocatorStatus.Resolved, rects,
                $"Matched {query} by element Name in {rects.Count} element(s): matched element Name (no TextPattern geometry){Notes()}.", 0.8);

        if (matchesWithoutGeometry > 0 || offscreenNames > 0)
            return new Result(LocatorStatus.Offscreen, [],
                $"Text {query} was found but is not on screen (searched {bfs.Visited} node(s), {textElements} TextPattern element(s)){Notes()}.", 0);

        return new Result(LocatorStatus.NotFound, [],
            $"Text {query} not found (searched {bfs.Visited} node(s), {textElements} TextPattern element(s), {matchedElements} with matches){Notes()}.", 0);
    }

    private static List<PixelRect> ToPixelRects(System.Windows.Rect[]? rects, PixelRect clip)
    {
        var list = new List<PixelRect>();
        if (rects is null) return list;
        foreach (var r in rects)
        {
            if (!UiaHelpers.TryToPixelRect(r, out var p)) continue;
            p = p.Intersect(clip);
            if (!p.IsEmpty) list.Add(p);
        }
        return list;
    }

    /// <summary>
    /// Builds a text range for <paramref name="match"/> (indices into <paramref name="text"/>). Providers do not agree on
    /// how a CR/LF pair counts in character units, so the range text is verified and, when it differs, alternatives are
    /// tried: offsets with CR/LF counted as one unit, then <see cref="TextPatternRange.FindText"/> for the n-th occurrence.
    /// </summary>
    internal static TextPatternRange? CreateRange(TextPatternRange document, string text, TextRange match, out bool verified)
    {
        string expected = text.Substring(match.Start, match.Length);
        string expectedNormalized = UiaHelpers.CollapseWhitespace(expected);

        var first = RangeAt(document, match.Start, match.Length);
        if (SameText(first, expected.Length, expectedNormalized)) { verified = true; return first; }

        int crlfBefore = CountCrLf(text, 0, match.Start);
        int crlfInside = CountCrLf(text, match.Start, match.End);
        if (crlfBefore + crlfInside > 0)
        {
            var adjusted = RangeAt(document, match.Start - crlfBefore, match.Length - crlfInside);
            if (SameText(adjusted, expected.Length, expectedNormalized)) { verified = true; return adjusted; }
        }

        var found = FindNthOccurrence(document, text, expected, match.Start);
        if (found is not null) { verified = true; return found; }

        verified = false;
        return first;
    }

    private static TextPatternRange RangeAt(TextPatternRange document, int start, int length)
    {
        var range = document.Clone();
        if (start > 0) range.MoveEndpointByUnit(TextPatternRangeEndpoint.Start, TextUnit.Character, start);
        range.MoveEndpointByRange(TextPatternRangeEndpoint.End, range, TextPatternRangeEndpoint.Start); // collapse
        if (length > 0) range.MoveEndpointByUnit(TextPatternRangeEndpoint.End, TextUnit.Character, length);
        return range;
    }

    private static bool SameText(TextPatternRange range, int expectedLength, string expectedNormalized)
    {
        string actual = range.GetText(expectedLength + 16) ?? "";
        return string.Equals(UiaHelpers.CollapseWhitespace(actual), expectedNormalized, StringComparison.Ordinal);
    }

    /// <summary>Counts CR/LF pairs whose CR lies in [start, end) and whose LF lies before <paramref name="end"/>.</summary>
    internal static int CountCrLf(string text, int start, int end)
    {
        int count = 0;
        for (int i = Math.Max(0, start); i + 1 < end && i + 1 < text.Length; i++)
            if (text[i] == '\r' && text[i + 1] == '\n') { count++; i++; }
        return count;
    }

    private static TextPatternRange? FindNthOccurrence(TextPatternRange document, string text, string expected, int matchStart)
    {
        // Which occurrence (0-based) of the exact substring the match is.
        int n = 0;
        for (int i = text.IndexOf(expected, StringComparison.Ordinal); i >= 0 && i < matchStart; i = text.IndexOf(expected, i + expected.Length, StringComparison.Ordinal))
            n++;
        if (n > 64) return null; // keep the number of provider calls bounded

        var search = document.Clone();
        for (int k = 0; k <= n; k++)
        {
            var found = search.FindText(expected, backward: false, ignoreCase: false);
            if (found is null) return null;
            if (k == n) return found;
            search.MoveEndpointByRange(TextPatternRangeEndpoint.Start, found, TextPatternRangeEndpoint.End);
        }
        return null;
    }
}
