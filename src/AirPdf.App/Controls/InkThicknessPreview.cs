using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace AirPdf.App.Controls;

/// <summary>
/// Shows what the chosen ink thickness looks like. A slider alone says "3" without saying how
/// thick 3 is; this draws a stroke at that width in the current ink colour, the same way the
/// pen will draw it.
/// </summary>
public sealed class InkThicknessPreview : Control
{
    public static readonly StyledProperty<IBrush?> StrokeProperty =
        AvaloniaProperty.Register<InkThicknessPreview, IBrush?>(nameof(Stroke), Brushes.Black);

    public static readonly StyledProperty<double> ThicknessProperty =
        AvaloniaProperty.Register<InkThicknessPreview, double>(nameof(Thickness), 2d);

    static InkThicknessPreview()
    {
        AffectsRender<InkThicknessPreview>(StrokeProperty, ThicknessProperty);
    }

    public IBrush? Stroke
    {
        get => GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public double Thickness
    {
        get => GetValue(ThicknessProperty);
        set => SetValue(ThicknessProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        if (Bounds.Width <= 0 || Bounds.Height <= 0)
        {
            return;
        }

        // A short, centred sample reads as a stroke; a full-width line reads as a divider.
        var stroke = Math.Max(1, Thickness);
        var inset = Math.Max(stroke / 2, 4);
        var length = Math.Clamp(Bounds.Width - (inset * 2), 0, 116);
        var left = (Bounds.Width - length) / 2;
        var right = left + length;
        var y = Bounds.Height / 2;

        context.DrawLine(
            new Pen(Stroke ?? Brushes.Black, stroke)
            {
                LineCap = PenLineCap.Round
            },
            new Point(left, y),
            new Point(right, y));
    }
}
