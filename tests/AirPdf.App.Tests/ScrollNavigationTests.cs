using AirPdf.App.Layout;
using Avalonia.Input;
using Xunit;

namespace AirPdf.App.Tests;

/// <summary>
/// The step size is the whole feature: an arrow key must not move a page, and Home and End must
/// reach the ends of the document. These are pure functions, so the numbers are pinned here
/// without building a window or opening a document.
/// </summary>
public sealed class ScrollNavigationTests
{
    [Theory]
    [InlineData(Key.Up, ScrollIntent.LineUp)]
    [InlineData(Key.Down, ScrollIntent.LineDown)]
    [InlineData(Key.Home, ScrollIntent.DocumentStart)]
    [InlineData(Key.End, ScrollIntent.DocumentEnd)]
    public void ScrollKeys_MapToScrollIntents(Key key, ScrollIntent expected) =>
        Assert.Equal(expected, ScrollNavigation.IntentFor(key, KeyModifiers.None));

    [Theory]
    [InlineData(Key.Up)]
    [InlineData(Key.Down)]
    [InlineData(Key.Home)]
    [InlineData(Key.End)]
    public void ModifiedScrollKeys_StayWithTheList(Key key)
    {
        // Shift+Arrow extends a selection; claiming it would break selecting text with the
        // keyboard.
        Assert.Equal(ScrollIntent.None, ScrollNavigation.IntentFor(key, KeyModifiers.Shift));
        Assert.Equal(ScrollIntent.None, ScrollNavigation.IntentFor(key, KeyModifiers.Control));
        Assert.Equal(
            ScrollIntent.None,
            ScrollNavigation.IntentFor(key, KeyModifiers.Control | KeyModifiers.Shift));
    }

    [Theory]
    [InlineData(Key.A)]
    [InlineData(Key.Left)]
    [InlineData(Key.Right)]
    [InlineData(Key.Space)]
    [InlineData(Key.Escape)]
    public void OtherKeys_AreNotScrollIntents(Key key) =>
        Assert.Equal(ScrollIntent.None, ScrollNavigation.IntentFor(key, KeyModifiers.None));

    [Theory]
    [InlineData(Key.PageUp)]
    [InlineData(Key.PageDown)]
    public void PageKeys_AreLeftToTheScrollViewer(Key key)
    {
        // The scroll viewer in the canvas template already scrolls one screen for these, and it
        // runs before the list in the bubble route. Claiming them here would be dead code.
        Assert.Equal(ScrollIntent.None, ScrollNavigation.IntentFor(key, KeyModifiers.None));
    }

    [Fact]
    public void LineStep_FollowsTheViewport()
    {
        // 8% of a normal window: a comfortable nudge, not a jump.
        Assert.Equal(56, ScrollNavigation.LineStep(700));
        Assert.Equal(80, ScrollNavigation.LineStep(1000));
    }

    [Fact]
    public void LineStep_StaysBetweenItsBounds()
    {
        Assert.Equal(ScrollNavigation.MinLineStep, ScrollNavigation.LineStep(100));
        Assert.Equal(ScrollNavigation.MinLineStep, ScrollNavigation.LineStep(0));
        Assert.Equal(ScrollNavigation.MaxLineStep, ScrollNavigation.LineStep(4000));
    }

    [Fact]
    public void Target_StepsForwardAndBackward()
    {
        Assert.Equal(56, ScrollNavigation.Target(ScrollIntent.LineDown, 0, 5000, 700));
        Assert.Equal(56, ScrollNavigation.Target(ScrollIntent.LineUp, 112, 5000, 700));
        Assert.Equal(0, ScrollNavigation.Target(ScrollIntent.DocumentStart, 900, 5000, 700));
        Assert.Equal(4300, ScrollNavigation.Target(ScrollIntent.DocumentEnd, 0, 5000, 700));
    }

    [Fact]
    public void Target_StopsAtTheTopAndTheEnd()
    {
        Assert.Equal(0, ScrollNavigation.Target(ScrollIntent.LineUp, 20, 5000, 700));

        // 5000 - 700 is the last offset that still fills the viewport.
        Assert.Equal(4256, ScrollNavigation.Target(ScrollIntent.LineDown, 4200, 5000, 700));
        Assert.Equal(4300, ScrollNavigation.Target(ScrollIntent.DocumentEnd, 0, 5000, 700));
    }

    [Fact]
    public void Target_NeverMovesWhenTheDocumentFitsTheViewport()
    {
        // A single short page at a low zoom has nothing to scroll, and must not jump.
        Assert.Equal(0, ScrollNavigation.Target(ScrollIntent.LineDown, 0, 600, 700));
        Assert.Equal(0, ScrollNavigation.Target(ScrollIntent.DocumentEnd, 0, 600, 700));
    }

    [Fact]
    public void Target_MovesFarLessThanAPage()
    {
        // The regression this feature exists for: one arrow press used to land on the next
        // page, which sits a full page height further down.
        const double pageHeight = 1690;
        var offset = ScrollNavigation.Target(ScrollIntent.LineDown, 0, pageHeight * 3, 700);

        Assert.True(offset < pageHeight, $"an arrow press moved {offset}px");
        Assert.True(offset < 100);
    }

    [Fact]
    public void Target_IgnoresMeasurementsFromAListThatHasNotBeenLaidOutYet()
    {
        // Extent and viewport are both zero before the first layout pass.
        Assert.Equal(0, ScrollNavigation.Target(ScrollIntent.LineDown, 0, 0, 0));
        Assert.Equal(
            0,
            ScrollNavigation.Target(ScrollIntent.DocumentEnd, 0, double.PositiveInfinity, 700));

        // A nonsense offset reads as the top of the document instead of producing NaN.
        Assert.Equal(56, ScrollNavigation.Target(ScrollIntent.LineDown, double.NaN, 1000, 700));
        Assert.Equal(56, ScrollNavigation.Target(ScrollIntent.LineDown, -50, 1000, 700));
    }

    [Fact]
    public void Target_LeavesAnUnknownIntentWhereItIs()
    {
        Assert.Equal(250, ScrollNavigation.Target(ScrollIntent.None, 250, 5000, 700));
    }
}
