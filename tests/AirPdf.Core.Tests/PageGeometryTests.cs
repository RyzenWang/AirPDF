using AirPdf.Core.Models;
using Xunit;

namespace AirPdf.Core.Tests;

/// <summary>
/// The page-space to display-space mapping is pure arithmetic, so it is checked against
/// hand-computed values for every rotation before anything renders.
/// </summary>
public sealed class PageGeometryTests
{
    private const double Width = 200;
    private const double Height = 300;

    // A 100 x 30 pt box near the top-left of the unrotated page.
    private const double Left = 20;
    private const double Top = 70;
    private const double Right = 120;
    private const double Bottom = 40;

    [Fact]
    public void UnrotatedPage_MapsWithTheOriginAtTheTopLeft()
    {
        var rect = PageGeometry.ToDisplayRect(Left, Top, Right, Bottom, Width, Height, 0);

        AssertClose(0.10, rect.X);
        AssertClose((Height - Top) / Height, rect.Y);
        AssertClose(0.50, rect.Width);
        AssertClose(30 / Height, rect.Height);
    }

    [Fact]
    public void QuarterTurn_SwapsTheAxes()
    {
        // Display box is 300 x 200, so the 100 pt horizontal box becomes 100 pt vertical.
        var rect = PageGeometry.ToDisplayRect(Left, Top, Right, Bottom, Width, Height, 90);

        AssertClose(40 / Height, rect.X);
        AssertClose(20 / Width, rect.Y);
        AssertClose(30 / Height, rect.Width);
        AssertClose(100 / Width, rect.Height);
    }

    [Fact]
    public void HalfTurn_MirrorsBothAxes()
    {
        var rect = PageGeometry.ToDisplayRect(Left, Top, Right, Bottom, Width, Height, 180);

        AssertClose(80 / Width, rect.X);
        AssertClose(40 / Height, rect.Y);
        AssertClose(0.50, rect.Width);
        AssertClose(30 / Height, rect.Height);
    }

    [Fact]
    public void ThreeQuarterTurn_SwapsTheAxesTheOtherWay()
    {
        var rect = PageGeometry.ToDisplayRect(Left, Top, Right, Bottom, Width, Height, 270);

        AssertClose(230 / Height, rect.X);
        AssertClose(80 / Width, rect.Y);
        AssertClose(30 / Height, rect.Width);
        AssertClose(100 / Width, rect.Height);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void TheWholePageCoversTheWholeDisplay(int rotation)
    {
        var rect = PageGeometry.ToDisplayRect(0, Height, Width, 0, Width, Height, rotation);

        AssertClose(0, rect.X);
        AssertClose(0, rect.Y);
        AssertClose(1, rect.Width);
        AssertClose(1, rect.Height);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void RotationPreservesTheArea(int rotation)
    {
        var upright = PageGeometry.ToDisplayRect(Left, Top, Right, Bottom, Width, Height, 0);
        var rotated = PageGeometry.ToDisplayRect(Left, Top, Right, Bottom, Width, Height, rotation);

        AssertClose(upright.Width * upright.Height, rotated.Width * rotated.Height, 1e-6);
    }

    [Fact]
    public void RotationsBeyondAFullTurnAreNormalized()
    {
        var once = PageGeometry.ToDisplayRect(Left, Top, Right, Bottom, Width, Height, 90);
        var wrapped = PageGeometry.ToDisplayRect(Left, Top, Right, Bottom, Width, Height, 450);
        var negative = PageGeometry.ToDisplayRect(Left, Top, Right, Bottom, Width, Height, -270);

        Assert.Equal(once, wrapped);
        Assert.Equal(once, negative);
        Assert.Equal(90, PageGeometry.NormalizeRotation(450));
        Assert.Equal(270, PageGeometry.NormalizeRotation(-90));
        Assert.Equal(0, PageGeometry.NormalizeRotation(12));
    }

    [Fact]
    public void DegenerateInputIsRejectedInsteadOfProducingNaNs()
    {
        var empty = PageGeometry.ToDisplayRect(Left, Top, Right, Bottom, 0, Height, 0);
        Assert.Equal(default, empty);

        var point = PageGeometry.ToDisplayRect(Left, Top, Left, Top, Width, Height, 90);
        AssertClose(0, point.Width);
        AssertClose(0, point.Height);
        Assert.False(double.IsNaN(point.X) || double.IsNaN(point.Y));
    }

    [Fact]
    public void CoordinatesAreClampedIntoThePage()
    {
        var rect = PageGeometry.ToDisplayRect(-50, 400, 500, -100, Width, Height, 90);

        Assert.InRange(rect.X, 0, 1);
        Assert.InRange(rect.Y, 0, 1);
        Assert.InRange(rect.X + rect.Width, 0, 1.0001);
        Assert.InRange(rect.Y + rect.Height, 0, 1.0001);
    }

    private static void AssertClose(double expected, double actual, double tolerance = 1e-6) =>
        Assert.InRange(actual, expected - tolerance, expected + tolerance);
}
