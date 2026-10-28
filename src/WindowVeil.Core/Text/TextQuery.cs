using System.Text.RegularExpressions;

namespace WindowVeil.Text;

/// <summary>How a <see cref="TextQuery"/> compares its pattern with candidate text.</summary>
public enum TextMatch
{
    /// <summary>The whole candidate must equal the pattern (after whitespace normalization).</summary>
    Exact,

    /// <summary>The candidate must contain the pattern.</summary>
    Contains,

    /// <summary>The candidate must start with the pattern.</summary>
    StartsWith,

    /// <summary>The pattern is a .NET regular expression.</summary>
    Regex,
}

/// <summary>
/// A text search: pattern, match mode and case sensitivity. Used by UI Automation text locators,
/// OCR locators and the profile format.
/// </summary>
public sealed record TextQuery
{
    private Regex? _regex;

    /// <summary>Creates a query.</summary>
    public TextQuery(string pattern, TextMatch mode = TextMatch.Contains, bool ignoreCase = true)
    {
        ArgumentException.ThrowIfNullOrEmpty(pattern);
        Pattern = pattern;
        Mode = mode;
        IgnoreCase = ignoreCase;
        if (mode == TextMatch.Regex) _ = GetRegex(); // validate eagerly
    }

    /// <summary>The text or regular expression to search for.</summary>
    public string Pattern { get; }

    /// <summary>Match mode.</summary>
    public TextMatch Mode { get; }

    /// <summary>Whether comparison ignores case (culture-invariant). Default true.</summary>
    public bool IgnoreCase { get; }

    /// <summary>
    /// Whether runs of whitespace are collapsed to a single space before comparison (default true).
    /// OCR and accessibility text frequently differ in whitespace.
    /// </summary>
    public bool NormalizeWhitespace { get; init; } = true;

    /// <summary>Exact, case-sensitive match.</summary>
    public static TextQuery Exactly(string text) => new(text, TextMatch.Exact, ignoreCase: false);

    /// <summary>Case-insensitive substring match.</summary>
    public static TextQuery Containing(string text) => new(text, TextMatch.Contains, ignoreCase: true);

    /// <summary>Regular expression match.</summary>
    public static TextQuery Matching(string regex, bool ignoreCase = false) => new(regex, TextMatch.Regex, ignoreCase);

    /// <summary>True when the candidate matches the query.</summary>
    public bool IsMatch(string? candidate) => candidate is not null && FindMatches(candidate).Count > 0;

    /// <summary>
    /// Returns every match as a character range into <paramref name="candidate"/> (original, un-normalized indices).
    /// <see cref="TextMatch.Exact"/> returns at most one range covering the whole string.
    /// </summary>
    public IReadOnlyList<TextRange> FindMatches(string candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var (text, map) = NormalizeWhitespace ? Normalize(candidate) : (candidate, null);
        var pattern = NormalizeWhitespace && Mode != TextMatch.Regex ? Normalize(Pattern).Text : Pattern;
        var comparison = IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var results = new List<TextRange>();
        switch (Mode)
        {
            case TextMatch.Exact:
                if (string.Equals(text, pattern, comparison)) results.Add(new TextRange(0, text.Length));
                break;
            case TextMatch.StartsWith:
                if (text.StartsWith(pattern, comparison)) results.Add(new TextRange(0, pattern.Length));
                break;
            case TextMatch.Contains:
                if (pattern.Length == 0) break;
                for (int i = text.IndexOf(pattern, comparison); i >= 0; i = text.IndexOf(pattern, i + pattern.Length, comparison))
                    results.Add(new TextRange(i, pattern.Length));
                break;
            case TextMatch.Regex:
                foreach (Match m in GetRegex().Matches(text))
                    if (m.Length > 0) results.Add(new TextRange(m.Index, m.Length));
                break;
        }
        return map is null ? results : results.Select(r => MapBack(r, map, candidate.Length)).ToList();
    }

    private Regex GetRegex() => _regex ??= new Regex(Pattern,
        RegexOptions.CultureInvariant | (IgnoreCase ? RegexOptions.IgnoreCase : RegexOptions.None),
        TimeSpan.FromMilliseconds(250));

    /// <summary>Collapses whitespace runs to one space and trims, returning a map from normalized to original indices.</summary>
    internal static (string Text, int[] Map) Normalize(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        var map = new List<int>(s.Length);
        bool pendingSpace = false;
        for (int i = 0; i < s.Length; i++)
        {
            if (char.IsWhiteSpace(s[i])) { pendingSpace = sb.Length > 0; continue; }
            if (pendingSpace) { sb.Append(' '); map.Add(i - 1); pendingSpace = false; }
            sb.Append(s[i]);
            map.Add(i);
        }
        return (sb.ToString(), map.ToArray());
    }

    private static TextRange MapBack(TextRange r, int[] map, int originalLength)
    {
        if (map.Length == 0) return new TextRange(0, 0);
        int start = map[Math.Min(r.Start, map.Length - 1)];
        int endIdx = r.Start + r.Length - 1;
        int end = endIdx < map.Length ? map[endIdx] + 1 : originalLength;
        return new TextRange(start, Math.Max(0, end - start));
    }

    /// <inheritdoc />
    public override string ToString() => $"{Mode}{(IgnoreCase ? "(i)" : "")}:\"{Pattern}\"";
}

/// <summary>A character range within a string.</summary>
public readonly record struct TextRange(int Start, int Length)
{
    /// <summary>Exclusive end index.</summary>
    public int End => Start + Length;
}
