using WindowVeil.Text;

namespace WindowVeil.UnitTests.Core;

public class TextQueryTests
{
    private static string Slice(string s, TextRange r) => s.Substring(r.Start, r.Length);

    [Fact]
    public void Exact_MatchesWholeNormalizedString_MapsToOriginalIndices()
    {
        const string candidate = "  Save   changes ";
        var q = new TextQuery("Save changes", TextMatch.Exact, ignoreCase: false);

        var m = Assert.Single(q.FindMatches(candidate));

        Assert.Equal("Save   changes", Slice(candidate, m));
        Assert.False(q.IsMatch("Save changes now"));
        Assert.False(q.IsMatch("save changes"));
        Assert.True(new TextQuery("save CHANGES", TextMatch.Exact, ignoreCase: true).IsMatch("Save changes"));
    }

    [Fact]
    public void Contains_FindsEveryNonOverlappingOccurrence()
    {
        var q = TextQuery.Containing("ab");
        Assert.Equal([new TextRange(0, 2), new TextRange(3, 2), new TextRange(6, 2)], q.FindMatches("ab AB ab"));
        Assert.Single(TextQuery.Containing("aa").FindMatches("aaa"));
        Assert.Empty(new TextQuery("AB", TextMatch.Contains, ignoreCase: false).FindMatches("ab"));
    }

    [Fact]
    public void StartsWith_RespectsCaseOption()
    {
        Assert.Equal([new TextRange(0, 5)], new TextQuery("start", TextMatch.StartsWith).FindMatches("Start automatically"));
        Assert.Empty(new TextQuery("start", TextMatch.StartsWith, ignoreCase: false).FindMatches("Start automatically"));
        Assert.Empty(new TextQuery("auto", TextMatch.StartsWith).FindMatches("Start automatically"));
    }

    [Fact]
    public void Regex_ReturnsEveryMatchAndHonorsIgnoreCase()
    {
        const string candidate = "Call 555-1234 or 555-9876";
        var matches = TextQuery.Matching(@"\d{3}-\d{4}").FindMatches(candidate);
        Assert.Equal(["555-1234", "555-9876"], matches.Select(m => Slice(candidate, m)));
        Assert.True(TextQuery.Matching("^call", ignoreCase: true).IsMatch(candidate));
        Assert.False(TextQuery.Matching("^call").IsMatch(candidate));
        Assert.Empty(TextQuery.Matching("x*").FindMatches("abc")); // empty matches are ignored
    }

    [Fact]
    public void WhitespaceNormalization_MatchesAcrossRunsAndMapsBack()
    {
        const string candidate = "Start   automatically\twhen\r\n I sign in";
        var q = TextQuery.Containing("automatically when I");

        var m = Assert.Single(q.FindMatches(candidate));

        Assert.Equal("automatically\twhen\r\n I", Slice(candidate, m));
    }

    [Fact]
    public void WhitespaceNormalization_CanBeDisabled()
    {
        var q = TextQuery.Containing("a b") with { NormalizeWhitespace = false };
        Assert.False(q.IsMatch("a  b"));
        Assert.True(q.IsMatch("xa by"));
    }

    [Fact]
    public void Normalize_CollapsesAndTrimsWithIndexMap()
    {
        var (text, map) = TextQuery.Normalize(" a \t b ");
        Assert.Equal("a b", text);
        Assert.Equal([1, 4, 5], map);
        Assert.Equal("", TextQuery.Normalize("   ").Text);
    }

    [Fact]
    public void InvalidArguments_Throw()
    {
        Assert.ThrowsAny<ArgumentException>(() => TextQuery.Matching("(unclosed"));
        Assert.ThrowsAny<ArgumentException>(() => new TextQuery(""));
        Assert.Throws<ArgumentNullException>(() => TextQuery.Containing("a").FindMatches(null!));
        Assert.False(TextQuery.Containing("a").IsMatch(null));
        Assert.Empty(TextQuery.Containing("a").FindMatches("   "));
    }

    [Fact]
    public void Equality_IsByValueForEveryMode()
    {
        foreach (var mode in Enum.GetValues<TextMatch>())
        {
            var a = new TextQuery("a.c", mode, ignoreCase: true);
            var b = new TextQuery("a.c", mode, ignoreCase: true);
            Assert.Equal(a, b);
            Assert.Equal(a.GetHashCode(), b.GetHashCode());
            Assert.NotEqual(a, new TextQuery("a.c", mode, ignoreCase: false));
            Assert.NotEqual(a, a with { NormalizeWhitespace = false });
        }
        Assert.NotEqual(new TextQuery("a", TextMatch.Regex), new TextQuery("a", TextMatch.Contains));
        Assert.Equal("Regex(i):\"a\"", new TextQuery("a", TextMatch.Regex).ToString());
    }
}
