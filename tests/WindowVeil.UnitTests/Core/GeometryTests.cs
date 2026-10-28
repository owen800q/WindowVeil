using WindowVeil.Geometry;

namespace WindowVeil.UnitTests.Core;

public class PixelRectTests
{
    [Fact]
    public void Edges_AreExclusive_AndAreaIsZeroWhenEmpty()
    {
        var r = new PixelRect(-10, -20, 30, 40);
        Assert.Equal(20, r.Right);
        Assert.Equal(20, r.Bottom);
        Assert.Equal(new PixelPoint(5, 0), r.Center);
        Assert.Equal(1200, r.Area);
        Assert.Equal(0, new PixelRect(0, 0, -5, 10).Area);
        Assert.True(new PixelRect(5, 5, 0, 3).IsEmpty);
        Assert.True(r.Contains(new PixelPoint(-10, -20)));
        Assert.False(r.Contains(new PixelPoint(20, 0)));
    }

    [Fact]
    public void FromLTRB_ClampsInvertedEdgesToEmpty()
    {
        Assert.Equal(new PixelRect(-5, -5, 10, 15), PixelRect.FromLTRB(-5, -5, 5, 10));
        Assert.True(PixelRect.FromLTRB(10, 10, 5, 5).IsEmpty);
    }

    [Fact]
    public void Union_IgnoresEmptyAndSpansNegativeCoordinates()
    {
        var a = new PixelRect(-1920, 100, 200, 50); // monitor left of the primary
        var b = new PixelRect(10, -30, 20, 20);     // monitor above
        Assert.Equal(PixelRect.FromLTRB(-1920, -30, 30, 150), a.Union(b));
        Assert.Equal(a, a.Union(PixelRect.Empty));
        Assert.Equal(b, PixelRect.Empty.Union(b));
        Assert.Equal(PixelRect.FromLTRB(-1920, -30, 30, 150), PixelRect.UnionAll([PixelRect.Empty, a, b, new PixelRect(0, 0, 0, 0)]));
        Assert.Equal(PixelRect.Empty, PixelRect.UnionAll([]));
    }

    [Fact]
    public void Intersect_OverlappingTouchingAndDisjoint()
    {
        var a = new PixelRect(-10, -10, 20, 20);
        Assert.Equal(new PixelRect(0, 0, 10, 10), a.Intersect(new PixelRect(0, 0, 50, 50)));
        Assert.False(a.IntersectsWith(new PixelRect(10, -10, 5, 5))); // touches the exclusive right edge
        Assert.Equal(PixelRect.Empty, a.Intersect(new PixelRect(10, -10, 5, 5)));
        Assert.Equal(PixelRect.Empty, a.Intersect(PixelRect.Empty));
        Assert.True(a.Contains(new PixelRect(-10, -10, 20, 20)));
        Assert.False(a.Contains(new PixelRect(-10, -10, 21, 20)));
        Assert.False(a.Contains(PixelRect.Empty));
    }

    [Fact]
    public void Inflate_GrowsPerSideAndShrinksToEmpty()
    {
        var r = new PixelRect(-5, -5, 10, 10);
        Assert.Equal(PixelRect.FromLTRB(-6, -7, 8, 9), r.Inflate(1, 2, 3, 4));
        Assert.Equal(new PixelRect(-3, -3, 6, 6), r.Inflate(-2));
        Assert.True(r.Inflate(-6).IsEmpty);
        Assert.Equal(new PixelRect(-15, 15, 10, 10), r.Offset(-10, 20));
    }

    [Fact]
    public void FromRectDOutward_CoversFractionalRectangles()
    {
        Assert.Equal(PixelRect.FromLTRB(-2, 1, 4, 4), PixelRect.FromRectDOutward(new RectD(-1.5, 1.2, 5.1, 2.6)));
        Assert.Equal(new PixelRect(1, 2, 3, 4), PixelRect.FromRectDOutward(new RectD(1, 2, 3, 4)));
    }

    [Fact]
    public void RectD_UnionFromPointsAndIntersects()
    {
        Assert.Equal(new RectD(-1, -2, 4, 6), RectD.FromPoints([new PointD(-1, 4), new PointD(3, -2), new PointD(0, 0)]));
        Assert.Equal(default, RectD.FromPoints([]));
        Assert.Equal(new RectD(0, 0, 3, 3), new RectD(0, 0, 1, 1).Union(new RectD(2, 2, 1, 1)));
        Assert.True(new RectD(0, 0, 1, 1).IntersectsWith(new RectD(0.5, 0.5, 1, 1)));
        Assert.False(new RectD(0, 0, 1, 1).IntersectsWith(new RectD(1, 0, 1, 1)));
    }
}

public class DpiScaleTests
{
    [Theory]
    [InlineData(100, 96, 1.0)]
    [InlineData(125, 120, 1.25)]
    [InlineData(150, 144, 1.5)]
    [InlineData(200, 192, 2.0)]
    public void FromPercent_MapsToDpiAndScale(double percent, double dpi, double scale)
    {
        var s = DpiScale.FromPercent(percent);
        Assert.Equal(dpi, s.Dpi, 6);
        Assert.Equal(scale, s.Scale, 6);
        Assert.Equal(percent, s.Percent, 6);
        Assert.Equal(new DpiScale(dpi), s);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-96)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Constructor_RejectsInvalidDpi(double dpi)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new DpiScale(dpi));
    }

    [Fact]
    public void Default_Is96Dpi_AndUninitializedStructBehavesAs100Percent()
    {
        Assert.Equal(1.0, DpiScale.Default.Scale);
        Assert.Equal(1.0, default(DpiScale).Scale);
        Assert.Equal("144 DPI (150%)", DpiScale.FromPercent(150).ToString());
    }
}

public class CoordinateConverterTests
{
    [Theory]
    [InlineData(100, 100, 100)]
    [InlineData(125, 100, 125)]
    [InlineData(150, 100, 150)]
    [InlineData(200, 100, 200)]
    [InlineData(150, 3, 4.5)]
    public void DipPhysical_RoundTrip(double percent, double dip, double physical)
    {
        var dpi = DpiScale.FromPercent(percent);
        Assert.Equal(physical, CoordinateConverter.DipToPhysical(dip, dpi), 9);
        Assert.Equal(dip, CoordinateConverter.PhysicalToDip(physical, dpi), 9);
    }

    [Fact]
    public void DipToPhysicalRounded_RoundsHalfAwayFromZero()
    {
        var dpi = DpiScale.FromPercent(150);
        Assert.Equal(5, CoordinateConverter.DipToPhysicalRounded(3, dpi));   // 4.5 -> 5
        Assert.Equal(-5, CoordinateConverter.DipToPhysicalRounded(-3, dpi)); // -4.5 -> -5
        Assert.Equal(1, CoordinateConverter.DipToPhysicalRounded(1, DpiScale.FromPercent(125))); // 1.25 -> 1
    }

    [Theory]
    [InlineData(100)]
    [InlineData(125)]
    [InlineData(150)]
    [InlineData(200)]
    public void ScreenDip_RoundTripIsLosslessForPhysicalRects(double percent)
    {
        var dpi = DpiScale.FromPercent(percent);
        var screen = new PixelRect(-1915, -7, 301, 99);
        var dip = CoordinateConverter.ScreenToDip(screen, dpi);
        Assert.Equal(screen.X / dpi.Scale, dip.X, 9);
        Assert.Equal(screen, CoordinateConverter.DipToScreen(dip, dpi));
    }

    [Fact]
    public void DipToScreen_RoundsOutwardSoContentIsNotClipped()
    {
        // 10.1 DIP at 125% = 12.625 px -> must cover up to 13 px.
        var r = CoordinateConverter.DipToScreen(new RectD(0.5, 0.5, 10.1, 1), DpiScale.FromPercent(125));
        Assert.Equal(PixelRect.FromLTRB(0, 0, 14, 2), r);
    }

    [Fact]
    public void RelativeConversions_AreInverse()
    {
        var origin = new PixelPoint(-1800, 40);
        var rel = new PixelRect(10, 20, 30, 40);
        var screen = CoordinateConverter.RelativeToScreen(rel, origin);
        Assert.Equal(new PixelRect(-1790, 60, 30, 40), screen);
        Assert.Equal(rel, CoordinateConverter.ScreenToRelative(screen, origin));
    }

    [Fact]
    public void ImageToScreen_HandlesNegativeOriginsAndScale()
    {
        var origin = new PixelPoint(-1920, -100);
        Assert.Equal(new PointD(-1910, -95), CoordinateConverter.ImageToScreen(new PointD(10, 5), origin));
        Assert.Equal(new PointD(-1900, -90), CoordinateConverter.ImageToScreen(new PointD(10, 5), origin, 2));
        Assert.Equal(new PixelRect(-1910, -95, 20, 10), CoordinateConverter.ImageToScreen(new RectD(10, 5, 20, 10), origin));
        // Fractional OCR boxes round outward.
        Assert.Equal(PixelRect.FromLTRB(-1910, -95, -1889, -84), CoordinateConverter.ImageToScreen(new RectD(10.4, 5.2, 20.2, 10.3), origin));
        Assert.Equal(new PixelRect(-1890, -85, 30, 15), CoordinateConverter.ImageToScreen(new RectD(20, 10, 20, 10), origin, 1.5));
    }

    [Fact]
    public void NormalizedToScreen_MapsOntoReference()
    {
        var reference = new PixelRect(-800, 100, 800, 600);
        Assert.Equal(new PixelRect(-600, 250, 400, 150), CoordinateConverter.NormalizedToScreen(new RectD(0.25, 0.25, 0.5, 0.25), reference));
        Assert.Equal(reference, CoordinateConverter.NormalizedToScreen(new RectD(0, 0, 1, 1), reference));
    }

    [Fact]
    public void InsetsToPhysical_ScalesEachSide()
    {
        Assert.Equal((2, 4, 6, 8), CoordinateConverter.InsetsToPhysical(new Insets(1, 2, 3, 4), DpiScale.FromPercent(200)));
        Assert.Equal((2, 3, 5, 6), CoordinateConverter.InsetsToPhysical(new Insets(1, 2, 3, 4), DpiScale.FromPercent(150)));
        Assert.Equal((0, 0, 0, 0), CoordinateConverter.InsetsToPhysical(Insets.Zero, DpiScale.FromPercent(150)));
        Assert.True(Insets.Zero.IsZero);
        Assert.Equal(new Insets(2, 2, 2, 2), Insets.Uniform(2));
    }
}
