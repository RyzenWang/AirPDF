using AirPdf.App.Layout;
using Xunit;

namespace AirPdf.App.Tests;

public sealed class PageLayoutTests
{
    [Fact]
    public void PrecomputesPageTopsAndFindsVisiblePage()
    {
        var layout = new PageLayout(new[] { 100.0, 200.0, 100.0 });

        Assert.Equal(24.0, layout.GetTop(1));
        Assert.Equal(152.0, layout.GetTop(2));
        Assert.Equal(380.0, layout.GetTop(3));
        Assert.Equal(1, layout.GetPageAtViewportTop(0, 50));
        Assert.Equal(2, layout.GetPageAtViewportTop(160, 60));
        Assert.Equal(0.5, layout.GetPageOffset(1, 74), 3);
    }

    [Fact]
    public void FindsNearestPageWhenViewportStartsInGap()
    {
        var layout = new PageLayout(new[] { 100.0, 200.0 });

        Assert.Equal(2, layout.GetPageAtViewportTop(140, 5));
    }
}
