using AirPdf.App.Layout;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;

namespace AirPdf.App.Controls;

/// <summary>
/// A list box with a stable internal scroll viewer, used as the continuous page canvas.
/// </summary>
public sealed class PdfPageList : ListBox
{
    private ScrollViewer? _viewer;

    public event EventHandler? ViewerScrollChanged;

    public ScrollViewer? Viewer => _viewer;

    public void ScrollToVerticalOffset(double offset)
    {
        if (_viewer is null)
        {
            return;
        }

        _viewer.SetCurrentValue(ScrollViewer.OffsetProperty, new Vector(0, Math.Max(0, offset)));
    }

    /// <summary>
    /// Scrolls the canvas for a scroll key and reports whether it took the key.
    /// </summary>
    /// <remarks>
    /// The list's own keyboard navigation moves the selection by whole items, and with one page
    /// per item that is a page turn: the reader lands at the same height on the next page and
    /// loses the line they were reading. Taking the keys here replaces that with an offset
    /// change. The key is consumed even when the canvas is already at an end, because letting
    /// it fall through would page-flip at the edges only - the same bug, intermittently.
    /// </remarks>
    public bool ScrollForKey(Key key, KeyModifiers modifiers)
    {
        if (_viewer is null)
        {
            return false;
        }

        var intent = ScrollNavigation.IntentFor(key, modifiers);
        if (intent == ScrollIntent.None)
        {
            return false;
        }

        ScrollToVerticalOffset(ScrollNavigation.Target(
            intent,
            _viewer.Offset.Y,
            _viewer.Extent.Height,
            _viewer.Viewport.Height));

        return true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (!e.Handled && ScrollForKey(e.Key, e.KeyModifiers))
        {
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        if (_viewer is not null)
        {
            _viewer.ScrollChanged -= OnViewerScrollChanged;
        }

        base.OnApplyTemplate(e);

        _viewer = e.NameScope.Find<ScrollViewer>("PART_ScrollViewer");
        if (_viewer is not null)
        {
            _viewer.ScrollChanged += OnViewerScrollChanged;
        }
    }

    private void OnViewerScrollChanged(object? sender, ScrollChangedEventArgs e) =>
        ViewerScrollChanged?.Invoke(this, EventArgs.Empty);
}
