using System.Globalization;
using WindowVeil.Geometry;
using WindowVeil.Text;

namespace WindowVeil.Ocr;

/// <summary>Which geometry an <see cref="OcrMatch"/> reports.</summary>
public enum OcrMatchGeometry
{
    /// <summary>The union of the whole recognized lines that contain the match (default; never under-covers).</summary>
    Region,

    /// <summary>
    /// An estimate of the matched substring only, obtained by interpolating along each line's polygon in proportion to
    /// character widths (East Asian wide characters count double). Approximate: proportional fonts make it imprecise,
    /// so combine it with an expansion when masking.
    /// </summary>
    ProportionalSubstring,
}

/// <summary>Options for <see cref="OcrTextMatcher"/>.</summary>
public sealed record OcrTextMatcherOptions
{
    /// <summary>Whether phrases split across neighboring detections on the same line are matched. Default true.</summary>
    public bool JoinSplitPhrases { get; init; } = true;

    /// <summary>
    /// Minimum vertical overlap of two detections, as a fraction of the smaller height, for them to count as the same line.
    /// Default 0.5.
    /// </summary>
    public double MinVerticalOverlap { get; init; } = 0.5;

    /// <summary>
    /// Maximum horizontal gap between two detections on the same line, as a multiple of the line height. Default 1.5.
    /// </summary>
    public double MaxGapFactor { get; init; } = 1.5;

    /// <summary>Maximum number of detections joined into one phrase. Default 8.</summary>
    public int MaxJoinedRegions { get; init; } = 8;

    /// <summary>Geometry reported for matches. Default <see cref="OcrMatchGeometry.Region"/>.</summary>
    public OcrMatchGeometry Geometry { get; init; } = OcrMatchGeometry.Region;
}

/// <summary>A match of a <see cref="TextQuery"/> in recognized text.</summary>
public sealed record OcrMatch
{
    /// <summary>The detections involved, left to right (more than one for a phrase joined across detections).</summary>
    public required IReadOnlyList<OcrTextRegion> Regions { get; init; }

    /// <summary>Matched geometry in the same image-local coordinates as the regions (see <see cref="OcrTextMatcherOptions.Geometry"/>).</summary>
    public required RectD BoundingBox { get; init; }

    /// <summary>Lowest confidence among <see cref="Regions"/>.</summary>
    public required float Confidence { get; init; }

    /// <summary>The matched text. Sensitive: do not log it by default.</summary>
    public required string Text { get; init; }

    /// <summary>True when the match spans several detections.</summary>
    public bool IsJoined => Regions.Count > 1;

    /// <summary>Matched character range inside each involved region (end exclusive).</summary>
    internal IReadOnlyList<(OcrTextRegion Region, int Start, int End)> Spans { get; init; } = [];

    /// <summary>Diagnostic string without the matched text.</summary>
    public override string ToString() => $"OcrMatch({Regions.Count} region(s), {Confidence:0.000}, {BoundingBox})";
}

/// <summary>
/// Result of <see cref="OcrTextMatcher.Match"/>. Separates matches that reach the confidence threshold from matches that
/// do not, so callers can distinguish "not found" from "found but below the threshold".
/// </summary>
public sealed record OcrMatchResult
{
    /// <summary>Matches whose confidence is at least <see cref="MinConfidence"/>, in reading order.</summary>
    public IReadOnlyList<OcrMatch> Matches { get; init; } = [];

    /// <summary>Matches below <see cref="MinConfidence"/>, in reading order.</summary>
    public IReadOnlyList<OcrMatch> LowConfidenceMatches { get; init; } = [];

    /// <summary>The threshold that was applied.</summary>
    public double MinConfidence { get; init; }

    /// <summary>True when at least one match reached the threshold.</summary>
    public bool Found => Matches.Count > 0;

    /// <summary>True when the text was only found below the threshold.</summary>
    public bool FoundOnlyBelowThreshold => Matches.Count == 0 && LowConfidenceMatches.Count > 0;

    /// <summary>Highest confidence among <see cref="LowConfidenceMatches"/>, or null when there are none.</summary>
    public float? BestLowConfidence => LowConfidenceMatches.Count == 0 ? null : LowConfidenceMatches.Max(m => m.Confidence);
}

/// <summary>
/// Finds <see cref="TextQuery"/> matches in OCR output. Each detection is matched on its own; in addition, detections on the
/// same visual line (vertical overlap ≥ <see cref="OcrTextMatcherOptions.MinVerticalOverlap"/> of the smaller height and a
/// horizontal gap below <see cref="OcrTextMatcherOptions.MaxGapFactor"/> × line height) are joined left to right, both with a
/// single space (Latin scripts) and without separator (CJK), so phrases split by the detector still match. A joined match is
/// only reported when it really spans several detections; its geometry is the union of the involved boxes.
/// Thread-safe and stateless.
/// </summary>
public sealed class OcrTextMatcher
{
    private static readonly string[] Separators = [" ", ""];

    /// <summary>Creates a matcher.</summary>
    public OcrTextMatcher(OcrTextMatcherOptions? options = null)
    {
        Options = options ?? new OcrTextMatcherOptions();
        if (Options.MaxJoinedRegions < 1) throw new ArgumentOutOfRangeException(nameof(options), "MaxJoinedRegions must be at least 1.");
        if (Options.MaxGapFactor < 0 || Options.MinVerticalOverlap < 0 || Options.MinVerticalOverlap > 1)
            throw new ArgumentOutOfRangeException(nameof(options), "MaxGapFactor must be ≥ 0 and MinVerticalOverlap within 0..1.");
    }

    /// <summary>Options.</summary>
    public OcrTextMatcherOptions Options { get; }

    /// <summary>Matches <paramref name="query"/> against <paramref name="regions"/>.</summary>
    /// <param name="regions">Recognized lines (any order).</param>
    /// <param name="query">What to look for.</param>
    /// <param name="minConfidence">Matches below this confidence go to <see cref="OcrMatchResult.LowConfidenceMatches"/>.</param>
    public OcrMatchResult Match(IReadOnlyList<OcrTextRegion> regions, TextQuery query, double minConfidence)
    {
        ArgumentNullException.ThrowIfNull(regions);
        ArgumentNullException.ThrowIfNull(query);
        var all = FindAll(regions, query);
        return new OcrMatchResult
        {
            Matches = all.Where(m => m.Confidence >= minConfidence).ToArray(),
            LowConfidenceMatches = all.Where(m => m.Confidence < minConfidence).ToArray(),
            MinConfidence = minConfidence,
        };
    }

    /// <summary>Every match regardless of confidence, in reading order.</summary>
    public IReadOnlyList<OcrMatch> FindAll(IReadOnlyList<OcrTextRegion> regions, TextQuery query)
    {
        ArgumentNullException.ThrowIfNull(regions);
        ArgumentNullException.ThrowIfNull(query);
        var items = regions.Where(r => !string.IsNullOrEmpty(r.Text)).ToArray();
        var results = new List<OcrMatch>();

        foreach (var region in items)
        {
            foreach (var range in query.FindMatches(region.Text))
            {
                results.Add(new OcrMatch
                {
                    Regions = [region],
                    BoundingBox = Geometry(region, range.Start, range.End),
                    Confidence = region.Confidence,
                    Text = region.Text.Substring(range.Start, range.Length),
                    Spans = [(region, range.Start, range.End)],
                });
            }
        }

        if (Options.JoinSplitPhrases && items.Length > 1)
        {
            foreach (var chain in BuildLineChains(items))
                MatchChain(chain, query, results);
        }

        return results.OrderBy(m => m.BoundingBox.Y).ThenBy(m => m.BoundingBox.X).ToArray();
    }

    private void MatchChain(OcrTextRegion[] chain, TextQuery query, List<OcrMatch> results)
    {
        var seen = new HashSet<(int First, int Last)>();
        for (int start = 0; start < chain.Length; start++)
        {
            for (int end = start + 1; end < chain.Length && end - start < Options.MaxJoinedRegions; end++)
            {
                foreach (var sep in Separators)
                {
                    var offsets = new int[end - start + 2];
                    var sb = new System.Text.StringBuilder();
                    for (int k = start; k <= end; k++)
                    {
                        if (k > start) sb.Append(sep);
                        offsets[k - start] = sb.Length;
                        sb.Append(chain[k].Text);
                    }
                    offsets[end - start + 1] = sb.Length + sep.Length;
                    var joined = sb.ToString();
                    foreach (var range in query.FindMatches(joined))
                    {
                        if (range.Length == 0) continue;
                        int first = RegionAt(offsets, range.Start), last = RegionAt(offsets, range.End - 1);
                        if (first == last) continue; // a single detection: already reported above
                        if (!seen.Add((start + first, start + last))) continue;
                        var involved = chain[(start + first)..(start + last + 1)];
                        int startInFirst = Math.Clamp(range.Start - offsets[first], 0, involved[0].Text.Length);
                        int endInLast = Math.Clamp(range.End - offsets[last], 0, involved[^1].Text.Length);
                        var box = Geometry(involved[0], startInFirst, involved[0].Text.Length);
                        for (int k = 1; k < involved.Length - 1; k++) box = box.Union(involved[k].BoundingBox);
                        box = box.Union(Geometry(involved[^1], 0, endInLast));
                        var spans = new List<(OcrTextRegion, int, int)>(involved.Length);
                        for (int k = 0; k < involved.Length; k++)
                            spans.Add((involved[k], k == 0 ? startInFirst : 0, k == involved.Length - 1 ? endInLast : involved[k].Text.Length));
                        results.Add(new OcrMatch
                        {
                            Spans = spans,
                            Regions = involved,
                            BoundingBox = box,
                            Confidence = involved.Min(r => r.Confidence),
                            Text = joined.Substring(range.Start, range.Length),
                        });
                    }
                }
            }
        }
    }

    private static int RegionAt(int[] offsets, int charIndex)
    {
        // offsets[k] is the start of region k in the joined string; offsets[^1] is past the end.
        for (int k = offsets.Length - 2; k >= 0; k--)
            if (charIndex >= offsets[k]) return k;
        return 0;
    }

    /// <summary>
    /// Groups detections into same-line chains, ordered left to right. Each detection has at most one right neighbor and one
    /// left neighbor; pairs are linked greedily by increasing gap.
    /// </summary>
    internal IReadOnlyList<OcrTextRegion[]> BuildLineChains(IReadOnlyList<OcrTextRegion> items)
    {
        var pairs = new List<(int A, int B, double Gap)>();
        for (int a = 0; a < items.Count; a++)
        {
            for (int b = 0; b < items.Count; b++)
            {
                if (a == b) continue;
                var ra = items[a].BoundingBox;
                var rb = items[b].BoundingBox;
                if (rb.X + rb.Width / 2 <= ra.X + ra.Width / 2) continue; // b must be to the right of a
                double minH = Math.Min(ra.Height, rb.Height);
                if (minH <= 0) continue;
                double overlap = Math.Min(ra.Bottom, rb.Bottom) - Math.Max(ra.Y, rb.Y);
                if (overlap / minH < Options.MinVerticalOverlap) continue;
                double lineHeight = (ra.Height + rb.Height) / 2;
                double gap = rb.X - ra.Right;
                if (gap >= Options.MaxGapFactor * lineHeight) continue;
                if (gap < -0.5 * minH) continue; // heavily overlapping detections are not neighbors
                pairs.Add((a, b, gap));
            }
        }
        var next = Enumerable.Repeat(-1, items.Count).ToArray();
        var prev = Enumerable.Repeat(-1, items.Count).ToArray();
        foreach (var (a, b, _) in pairs.OrderBy(p => p.Gap).ThenBy(p => p.A).ThenBy(p => p.B))
        {
            if (next[a] >= 0 || prev[b] >= 0) continue;
            // Avoid cycles: walk from b to see whether a is reachable.
            int k = b;
            bool cycle = false;
            while (k >= 0) { if (k == a) { cycle = true; break; } k = next[k]; }
            if (cycle) continue;
            next[a] = b;
            prev[b] = a;
        }
        var chains = new List<OcrTextRegion[]>();
        for (int i = 0; i < items.Count; i++)
        {
            if (prev[i] >= 0 || next[i] < 0) continue;
            var chain = new List<OcrTextRegion>();
            for (int k = i; k >= 0; k = next[k]) chain.Add(items[k]);
            chains.Add(chain.ToArray());
        }
        return chains;
    }

    private RectD Geometry(OcrTextRegion region, int start, int end)
    {
        if (Options.Geometry == OcrMatchGeometry.Region || region.Polygon.Count != 4 || region.Text.Length == 0)
            return region.BoundingBox;
        if (start <= 0 && end >= region.Text.Length) return region.BoundingBox;
        double total = 0, before = 0, upto = 0;
        for (int i = 0; i < region.Text.Length; i++)
        {
            double w = CharWeight(region.Text, i);
            if (i < start) before += w;
            if (i < end) upto += w;
            total += w;
        }
        if (total <= 0) return region.BoundingBox;
        double f0 = before / total, f1 = upto / total;
        var p = region.Polygon; // tl, tr, br, bl
        PointD Lerp(PointD a, PointD b, double t) => new(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);
        return RectD.FromPoints([Lerp(p[0], p[1], f0), Lerp(p[0], p[1], f1), Lerp(p[3], p[2], f1), Lerp(p[3], p[2], f0)]);
    }

    private static double CharWeight(string s, int i)
    {
        char c = s[i];
        if (char.IsLowSurrogate(c)) return 0; // counted with its high surrogate
        int cp = char.IsHighSurrogate(c) && i + 1 < s.Length ? char.ConvertToUtf32(c, s[i + 1]) : c;
        return IsWide(cp) ? 2 : 1;
    }

    private static bool IsWide(int cp) =>
        (cp >= 0x1100 && cp <= 0x115F) || (cp >= 0x2E80 && cp <= 0xA4CF) || (cp >= 0xAC00 && cp <= 0xD7A3) ||
        (cp >= 0xF900 && cp <= 0xFAFF) || (cp >= 0xFE30 && cp <= 0xFE4F) || (cp >= 0xFF00 && cp <= 0xFF60) ||
        (cp >= 0xFFE0 && cp <= 0xFFE6) || (cp >= 0x1F300 && cp <= 0x1FAFF) || (cp >= 0x20000 && cp <= 0x3FFFD) ||
        CharUnicodeInfo.GetUnicodeCategory(cp) == UnicodeCategory.OtherLetter && cp >= 0x3000 && cp < 0xA000;
}
