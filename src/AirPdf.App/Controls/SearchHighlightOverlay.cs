using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using AirPdf.Core.Models;

namespace AirPdf.App.Controls;

public sealed class SearchHighlightOverlay : Control
{
    private static readonly IBrush HighlightBrush = new SolidColorBrush(Color.FromArgb(82, 255, 196, 55));
    private static readonly IBrush ActiveBrush = new SolidColorBrush(Color.FromArgb(30, 242, 125, 35));
    private static readonly IPen ActivePen = new Pen(new SolidColorBrush(Color.FromArgb(235, 201, 91, 18)), 2.0);

    public static readonly StyledProperty<IReadOnlyList<PdfSearchRect>?> RectsProperty =
        AvaloniaProperty.Register<SearchHighlightOverlay, IReadOnlyList<PdfSearchRect>?>(nameof(Rects));

    public static readonly StyledProperty<IReadOnlyList<PdfSearchRect>?> ActiveRectsProperty =
        AvaloniaProperty.Register<SearchHighlightOverlay, IReadOnlyList<PdfSearchRect>?>(nameof(ActiveRects));

    static SearchHighlightOverlay()
    {
        AffectsRender<SearchHighlightOverlay>(RectsProperty, ActiveRectsProperty);
    }

    public SearchHighlightOverlay()
    {
        IsHitTestVisible = false;
    }

    public IReadOnlyList<PdfSearchRect>? Rects
    {
        get => GetValue(RectsProperty);
        set => SetValue(RectsProperty, value);
    }

    public IReadOnlyList<PdfSearchRect>? ActiveRects
    {
        get => GetValue(ActiveRectsProperty);
        set => SetValue(ActiveRectsProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (Bounds.Width <= 1 || Bounds.Height <= 1)
        {
            return;
        }

        DrawRects(context, Rects, HighlightBrush, null);
        DrawRects(context, ActiveRects, ActiveBrush, ActivePen);
    }

    private void DrawRects(DrawingContext context, IReadOnlyList<PdfSearchRect>? rects, IBrush brush, IPen? pen)
    {
        if (rects is null)
        {
            return;
        }

        foreach (var rect in rects)
        {
            var x = Math.Clamp(rect.X, 0, 1) * Bounds.Width;
            var y = Math.Clamp(rect.Y, 0, 1) * Bounds.Height;
            var width = Math.Clamp(rect.Width, 0, 1) * Bounds.Width;
            var height = Math.Max(2, Math.Clamp(rect.Height, 0, 1) * Bounds.Height);

            context.DrawRectangle(brush, pen, new Rect(x, y, width, height), 1.5);
        }
    }
}
