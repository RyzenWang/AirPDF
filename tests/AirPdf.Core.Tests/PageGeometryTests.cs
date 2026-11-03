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

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void ToPageQuad_RoundTripsThroughToDisplayRect(int rotation)
    {
        // Annotations are written in page space, so the inverse of the display mapping has to be
        // exact; a highlight that lands somewhere else would be a silent, visible defect.
        var rectangles = new[]
        {
            new PdfSearchRect(0.10, 0.20, 0.25, 0.04),
            new PdfSearchRect(0.55, 0.05, 0.40, 0.10),
            new PdfSearchRect(0.00, 0.00, 1.00, 1.00),
            new PdfSearchRect(0.80, 0.70, 0.20, 0.30)
        };

        foreach (var expected in rectangles)
        {
            var quad = PageGeometry.ToPageQuad(expected, Width, Height, rotation);
            var actual = PageGeometry.ToDisplayRect(quad.Left, quad.Top, quad.Right, quad.Bottom, Width, Height, rotation);

            AssertClose(expected.X, actual.X, 1e-9);
            AssertClose(expected.Y, actual.Y, 1e-9);
            AssertClose(expected.Width, actual.Width, 1e-9);
            AssertClose(expected.Height, actual.Height, 1e-9);
        }
    }

    [Fact]
    public void ToPageQuad_PutsTheTopOfTheDisplayAtTheTopOfThePage()
    {
        // Page space is Y-up, so the top edge of a band near the top of the display has to come
        // back as the larger Y. Getting this backwards would flip every highlight vertically.
        var quad = PageGeometry.ToPageQuad(new PdfSearchRect(0.1, 0.1, 0.2, 0.1), Width, Height, 0);

        Assert.True(quad.Top > quad.Bottom);
        AssertClose(Height * 0.9, quad.Top, 1e-6);
        AssertClose(Width * 0.1, quad.Left, 1e-6);
        AssertClose(Width * 0.3, quad.Right, 1e-6);
    }

    [Fact]
    public void ToPageQuad_WithNoPageSize_ReturnsEmpty()
    {
        Assert.Equal(default, PageGeometry.ToPageQuad(new PdfSearchRect(0.1, 0.1, 0.2, 0.2), 0, 0, 0));
    }
    private static void AssertClose(double expected, double actual, double tolerance = 1e-6) =>
        Assert.InRange(actual, expected - tolerance, expected + tolerance);
}
