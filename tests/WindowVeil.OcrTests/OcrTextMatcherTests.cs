using WindowVeil.Geometry;
using WindowVeil.Ocr;
using WindowVeil.Text;
using Xunit;

namespace WindowVeil.OcrTests;

public class OcrTextMatcherTests
{
    private static OcrTextRegion R(string text, double x, double y, double w = 100, double h = 20, float conf = 0.95f) =>
        OcrTextRegion.FromRect(text, conf, new RectD(x, y, w, h));

    private readonly OcrTextRegion[] _lines =
    [
        R("Start automatically when I sign in to Windows", 40, 20, 340),
        R("Start hidden in the notification area", 40, 60, 270),
        R("Account Balance $12,345.67", 40, 100, 220),
    ];

    [Theory]
    [InlineData("Start hidden in the notification area", TextMatch.Exact, 1)]
    [InlineData("start hidden in the notification area", TextMatch.Exact, 1)]
    [InlineData("notification", TextMatch.Contains, 1)]
    [InlineData("Start", TextMatch.StartsWith, 2)]
    [InlineData("hidden", TextMatch.StartsWith, 0)]
    [InlineData(@"\$\d{1,3}(,\d{3})*\.\d{2}", TextMatch.Regex, 1)]
    [InlineData("Password", TextMatch.Contains, 0)]
    public void MatchModes(string pattern, TextMatch mode, int expected)
    {
        var result = new OcrTextMatcher().Match(_lines, new TextQuery(pattern, mode, ignoreCase: true), 0.5);
        Assert.Equal(expected, result.Matches.Count);
    }

    [Fact]
    public void CaseSensitiveQuery_DoesNotMatchDifferentCase()
    {
        var result = new OcrTextMatcher().Match(_lines, TextQuery.Exactly("start hidden in the notification area"), 0.5);
        Assert.False(result.Found);
    }

    [Fact]
    public void MultipleMatches_AreReturnedInReadingOrder()
    {
        var regions = new[] { R("Total: 5", 10, 80), R("Total: 3", 200, 10), R("Total: 1", 10, 10) };
        var result = new OcrTextMatcher().Match(regions, new TextQuery("total"), 0.5);
        Assert.Equal(["Total: 1", "Total: 3", "Total: 5"], result.Matches.Select(m => m.Regions[0].Text));
    }

    [Fact]
    public void RepeatedSubstring_InOneLine_YieldsOneMatchPerOccurrence()
    {
        var result = new OcrTextMatcher().Match([R("ab ab ab", 0, 0)], new TextQuery("ab"), 0.5);
        Assert.Equal(3, result.Matches.Count);
    }

    [Fact]
    public void PhraseSplitAcrossDetections_IsJoined_WithUnionBox()
    {
        var regions = new[] { R("Account", 10, 100, 70, 20), R("Balance", 88, 102, 70, 18), R("elsewhere", 10, 200) };
        var result = new OcrTextMatcher().Match(regions, new TextQuery("account balance", TextMatch.Exact), 0.5);
        var m = Assert.Single(result.Matches);
        Assert.True(m.IsJoined);
        Assert.Equal(2, m.Regions.Count);
        Assert.Equal(new RectD(10, 100, 148, 20), m.BoundingBox);
    }

    [Fact]
    public void CjkPhraseSplitAcrossDetections_IsJoinedWithoutSpace()
    {
        var regions = new[] { R("账户", 10, 10, 44, 22), R("余额", 60, 10, 44, 22) };
        var result = new OcrTextMatcher().Match(regions, new TextQuery("账户余额", TextMatch.Exact), 0.5);
        Assert.Single(result.Matches);
    }

    [Fact]
    public void DetectionsOnDifferentLines_OrFarApart_AreNotJoined()
    {
        var below = new[] { R("Account", 10, 100, 70, 20), R("Balance", 88, 125, 70, 20) }; // no vertical overlap
        Assert.False(new OcrTextMatcher().Match(below, new TextQuery("account balance"), 0.5).Found);
        var far = new[] { R("Account", 10, 100, 70, 20), R("Balance", 10 + 70 + 31, 100, 70, 20) }; // gap 31 ≥ 1.5 × 20
        Assert.False(new OcrTextMatcher().Match(far, new TextQuery("account balance"), 0.5).Found);
        var near = new[] { R("Account", 10, 100, 70, 20), R("Balance", 10 + 70 + 29, 100, 70, 20) };
        Assert.True(new OcrTextMatcher().Match(near, new TextQuery("account balance"), 0.5).Found);
    }

    [Fact]
    public void JoinedMatch_IsNotReportedWhenASingleDetectionAlreadyMatches()
    {
        var regions = new[] { R("Balance due", 10, 10, 90, 20), R("now", 105, 10, 30, 20) };
        var result = new OcrTextMatcher().Match(regions, new TextQuery("balance"), 0.5);
        var m = Assert.Single(result.Matches);
        Assert.False(m.IsJoined);
    }

    [Fact]
    public void JoiningCanBeDisabled()
    {
        var regions = new[] { R("Account", 10, 100, 70, 20), R("Balance", 88, 100, 70, 20) };
        var matcher = new OcrTextMatcher(new OcrTextMatcherOptions { JoinSplitPhrases = false });
        Assert.False(matcher.Match(regions, new TextQuery("account balance"), 0.5).Found);
    }

    [Fact]
    public void LowConfidenceMatches_AreReportedSeparately()
    {
        var regions = new[] { R("Secret token", 10, 10, conf: 0.41f), R("Secret key", 10, 50, conf: 0.55f), R("Secret", 10, 90, conf: 0.9f) };
        var result = new OcrTextMatcher().Match(regions, new TextQuery("secret"), 0.6);
        Assert.Single(result.Matches);
        Assert.Equal(2, result.LowConfidenceMatches.Count);
        Assert.Equal(0.55f, result.BestLowConfidence);

        var onlyLow = new OcrTextMatcher().Match(regions[..2], new TextQuery("secret"), 0.6);
        Assert.False(onlyLow.Found);
        Assert.True(onlyLow.FoundOnlyBelowThreshold);
        var none = new OcrTextMatcher().Match(regions, new TextQuery("absent"), 0.6);
        Assert.False(none.FoundOnlyBelowThreshold);
    }

    [Fact]
    public void JoinedMatchConfidence_IsTheMinimumOfItsParts()
    {
        var regions = new[] { R("Account", 10, 100, 70, 20, 0.95f), R("Balance", 88, 100, 70, 20, 0.5f) };
        var result = new OcrTextMatcher().Match(regions, new TextQuery("account balance"), 0.6);
        Assert.False(result.Found);
        Assert.Equal(0.5f, Assert.Single(result.LowConfidenceMatches).Confidence);
    }

    [Fact]
    public void ProportionalSubstringGeometry_EstimatesTheMatchedPart()
    {
        var region = R("abcdefghij", 100, 10, 100, 20);
        var matcher = new OcrTextMatcher(new OcrTextMatcherOptions { Geometry = OcrMatchGeometry.ProportionalSubstring });
        var m = Assert.Single(matcher.Match([region], new TextQuery("fgh"), 0.5).Matches);
        Assert.Equal(150, m.BoundingBox.X, 6);
        Assert.Equal(30, m.BoundingBox.Width, 6);
        var whole = Assert.Single(new OcrTextMatcher().Match([region], new TextQuery("fgh"), 0.5).Matches);
        Assert.Equal(region.BoundingBox, whole.BoundingBox);
    }

    [Fact]
    public void ToString_DoesNotLeakText()
    {
        var region = R("password123", 0, 0);
        Assert.DoesNotContain("password", region.ToString());
        var m = Assert.Single(new OcrTextMatcher().Match([region], new TextQuery("password"), 0.5).Matches);
        Assert.DoesNotContain("password", m.ToString());
    }
}
