using WindowVeil.Appearance;
using WindowVeil.Geometry;

namespace WindowVeil.UnitTests.Core;

public class PlacementCalculatorTests
{
    private static readonly PixelRect Target = new(-100, 50, 100, 20);
    private static DpiScale Dpi(double percent) => DpiScale.FromPercent(percent);

    [Theory]
    [InlineData(100)]
    [InlineData(125)]
    [InlineData(150)]
    [InlineData(200)]
    public void Cover_ExactlyCoversTargetAtAnyDpi(double percent)
    {
        Assert.Equal(Target, PlacementCalculator.Calculate(Target, OverlayPlacement.CoverTarget, Dpi(percent)));
        Assert.True(OverlayPlacement.CoverTarget.SizesToTarget);
    }

    [Theory]
    [InlineData(100, 2)]
    [InlineData(125, 3)] // 2.5 rounds away from zero
    [InlineData(150, 3)]
    [InlineData(200, 4)]
    public void Cover_PaddingIsInDips(double percent, int px)
    {
        Assert.Equal(Target.Inflate(px), PlacementCalculator.Calculate(Target, OverlayPlacement.Cover(2), Dpi(percent)));
    }

    [Fact]
    public void Cover_PerSidePaddingAndOffset()
    {
        var placement = OverlayPlacement.Cover(new Insets(1, 2, 3, 4)) with { OffsetX = -5, OffsetY = 10 };
        Assert.Equal(Target.Inflate(2, 4, 6, 8).Offset(-10, 20), PlacementCalculator.Calculate(Target, placement, Dpi(200)));
    }

    [Fact]
    public void Right_ContentSized_VerticallyCentered_WithDipOffset()
    {
        var r = PlacementCalculator.Calculate(Target, OverlayPlacement.Right(offsetX: 8), Dpi(150), new PixelSize(30, 10));
        Assert.Equal(new PixelRect(Target.Right + 12, Target.Y + 5, 30, 10), r);
    }

    [Fact]
    public void Left_PlacesContentBeforeTarget()
    {
        var r = PlacementCalculator.Calculate(Target, OverlayPlacement.Left(offsetX: -4), Dpi(100), new PixelSize(30, 10));
        Assert.Equal(new PixelRect(Target.X - 30 - 4, Target.Y + 5, 30, 10), r);
    }

    [Fact]
    public void Above_StartAligned_And_Below_EndAligned()
    {
        var above = PlacementCalculator.Calculate(Target, OverlayPlacement.Above(offsetY: -2), Dpi(200), new PixelSize(40, 10));
        Assert.Equal(new PixelRect(Target.X, Target.Y - 10 - 4, 40, 10), above);

        var below = PlacementCalculator.Calculate(Target, OverlayPlacement.Below(offsetY: 0) with { Alignment = PlacementAlignment.End }, Dpi(100), new PixelSize(40, 10));
        Assert.Equal(new PixelRect(Target.Right - 40, Target.Bottom, 40, 10), below);
    }

    [Fact]
    public void Right_StartAndEndAlignment()
    {
        var size = new PixelSize(10, 6);
        Assert.Equal(Target.Y, PlacementCalculator.Calculate(Target, OverlayPlacement.Right(0) with { Alignment = PlacementAlignment.Start }, Dpi(100), size).Y);
        Assert.Equal(Target.Bottom - 6, PlacementCalculator.Calculate(Target, OverlayPlacement.Right(0) with { Alignment = PlacementAlignment.End }, Dpi(100), size).Y);
    }

    [Fact]
    public void Center_ExplicitSizeInDips()
    {
        var r = PlacementCalculator.Calculate(Target, OverlayPlacement.Centered(20, 10), Dpi(200));
        Assert.Equal(new PixelRect(Target.X + 30, Target.Y - 0, 40, 20), r);
    }

    [Fact]
    public void ExplicitSizeOverridesContentSize_PerAxis()
    {
        var placement = OverlayPlacement.Below(0, 0) with { Width = 50 };
        var r = PlacementCalculator.Calculate(Target, placement, Dpi(100), new PixelSize(30, 12));
        Assert.Equal(new PixelSize(50, 12), r.Size);
    }

    [Fact]
    public void NoContentSize_FallsBackToTargetSize()
    {
        var r = PlacementCalculator.Calculate(Target, OverlayPlacement.Right(0), Dpi(100));
        Assert.Equal(new PixelRect(Target.Right, Target.Y, Target.Width, Target.Height), r);
    }

    [Fact]
    public void MultiRect_EachCoversRectanglesSeparately_UnionMerges()
    {
        PixelRect[] lines = [new(0, 0, 100, 10), new(0, 12, 40, 10), PixelRect.Empty];

        var each = PlacementCalculator.Calculate(lines, OverlayPlacement.CoverTarget, Dpi(100));
        Assert.Equal([new PixelRect(0, 0, 100, 10), new PixelRect(0, 12, 40, 10)], each);

        var union = PlacementCalculator.Calculate(lines, OverlayPlacement.CoverTarget with { MultiRect = MultiRectMode.Union }, Dpi(100));
        Assert.Equal([new PixelRect(0, 0, 100, 22)], union);

        // Adjacent placements default to Union: one badge for a multi-line match.
        var badge = PlacementCalculator.Calculate(lines, OverlayPlacement.Right(0), Dpi(100), new PixelSize(5, 5));
        Assert.Single(badge);
        Assert.Empty(PlacementCalculator.Calculate([PixelRect.Empty], OverlayPlacement.CoverTarget, Dpi(100)));
    }

    [Fact]
    public void Presets_HaveDocumentedDefaults()
    {
        Assert.Equal(PlacementMode.Right, OverlayPlacement.Right().Mode);
        Assert.Equal(8, OverlayPlacement.Right().OffsetX);
        Assert.Equal(-8, OverlayPlacement.Left().OffsetX);
        Assert.Equal(PlacementAlignment.Start, OverlayPlacement.Above().Alignment);
        Assert.Equal(-8, OverlayPlacement.Above().OffsetY);
        Assert.Equal(8, OverlayPlacement.Below().OffsetY);
        Assert.Equal(MultiRectMode.Union, OverlayPlacement.Centered().MultiRect);
        Assert.False(OverlayPlacement.Centered().SizesToTarget);
    }
}
