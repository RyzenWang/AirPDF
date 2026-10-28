using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using AirPdf.Core.Models;

namespace AirPdf.App.Controls;

public sealed class TextSelectionOverlay : Control
{
    private static readonly IBrush SelectionBrush = new SolidColorBrush(Color.FromArgb(95, 80, 135, 235));
    private static readonly IPen SelectionPen = new Pen(new SolidColorBrush(Color.FromArgb(115, 54, 105, 201)), 0.8);

    public static readonly StyledProperty<IReadOnlyList<PdfSearchRect>?> RectsProperty =
        AvaloniaProperty.Register<TextSelectionOverlay, IReadOnlyList<PdfSearchRect>?>(nameof(Rects));

    static TextSelectionOverlay()
    {
        AffectsRender<TextSelectionOverlay>(RectsProperty);
    }

    public TextSelectionOverlay()
    {
        IsHitTestVisible = false;
    }

    public IReadOnlyList<PdfSearchRect>? Rects
    {
        get => GetValue(RectsProperty);
        set => SetValue(RectsProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (Bounds.Width <= 1 || Bounds.Height <= 1 || Rects is null)
        {
            return;
        }

        foreach (var rect in Rects)
        {
            var x = Math.Clamp(rect.X, 0, 1) * Bounds.Width;
            var y = Math.Clamp(rect.Y, 0, 1) * Bounds.Height;
            var width = Math.Clamp(rect.Width, 0, 1) * Bounds.Width;
            var height = Math.Max(2, Math.Clamp(rect.Height, 0, 1) * Bounds.Height);

            context.DrawRectangle(SelectionBrush, SelectionPen, new Rect(x, y, width, height), 1.5);
        }
    }
}
