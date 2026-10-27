using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;

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
