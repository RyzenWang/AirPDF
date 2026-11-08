using Avalonia.Input;

namespace AirPdf.App.Layout;

/// <summary>What a scroll key asks the page canvas to do.</summary>
public enum ScrollIntent
{
    None,
    LineUp,
    LineDown,
    DocumentStart,
    DocumentEnd
}

/// <summary>
/// Keyboard scrolling for the continuous page canvas.
/// </summary>
/// <remarks>
/// The canvas is a <c>ListBox</c>, so its own keyboard handling owned the arrow keys: every
/// press moved the selection by one item and scrolled that item into view, which is a whole
/// page. A reader who pressed Down to follow a paragraph was thrown to the top of the next
/// page and lost the line they were on, and there was no key at all that scrolled the way the
/// mouse wheel does.
///
/// These steps replace the item jumps with offsets. An arrow key moves a small share of the
/// viewport, and Home and End reach the ends of the document. Everything is a pure function of
/// the viewport and the scroll extent, so the numbers stay predictable in tests and the step
/// scales with the window rather than with the page.
///
/// Page Up and Page Down are deliberately absent: the scroll viewer inside the canvas template
/// already turns those into a one-screen scroll before the list sees the key, which is the
/// behaviour those keys should have. Only the keys Avalonia leaves to the list are claimed here.
/// </remarks>
public static class ScrollNavigation
{
    /// <summary>Share of the viewport an arrow press moves.</summary>
    public const double LineStepFraction = 0.08;

    /// <summary>
    /// Floor and ceiling for the arrow step. Without them a very short window would crawl and a
    /// very tall one would move almost a page per press.
    /// </summary>
    public const double MinLineStep = 40.0;

    public const double MaxLineStep = 140.0;

    /// <summary>
    /// Maps a key press to a scroll intent. Anything with a modifier is left to the list:
    /// <c>Shift+Arrow</c> extends a selection, and the window owns the keys it has already
    /// claimed.
    /// </summary>
    public static ScrollIntent IntentFor(Key key, KeyModifiers modifiers)
    {
        if (modifiers != KeyModifiers.None)
        {
            return ScrollIntent.None;
        }

        return key switch
        {
            Key.Up => ScrollIntent.LineUp,
            Key.Down => ScrollIntent.LineDown,
            Key.Home => ScrollIntent.DocumentStart,
            Key.End => ScrollIntent.DocumentEnd,
            _ => ScrollIntent.None
        };
    }

    /// <summary>Distance one arrow press moves for a viewport of this height.</summary>
    public static double LineStep(double viewportHeight) =>
        Math.Clamp(
            Math.Round(viewportHeight * LineStepFraction),
            MinLineStep,
            MaxLineStep);

    /// <summary>
    /// Where the canvas should scroll to, clamped to the document. A non-finite or negative
    /// measurement from a list that has not been laid out yet yields no movement instead of a
    /// jump to a nonsense offset.
    /// </summary>
    public static double Target(
        ScrollIntent intent,
        double offset,
        double extentHeight,
        double viewportHeight)
    {
        var current = double.IsFinite(offset) ? Math.Max(0, offset) : 0;
        var viewport = double.IsFinite(viewportHeight) ? Math.Max(0, viewportHeight) : 0;
        var extent = double.IsFinite(extentHeight) ? Math.Max(0, extentHeight) : 0;
        var last = Math.Max(0, extent - viewport);

        var target = intent switch
        {
            ScrollIntent.LineUp => current - LineStep(viewport),
            ScrollIntent.LineDown => current + LineStep(viewport),
            ScrollIntent.DocumentStart => 0,
            ScrollIntent.DocumentEnd => last,
            _ => current
        };

        return Math.Clamp(target, 0, last);
    }
}
