using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using AirPdf.Core.Models;

namespace AirPdf.App.Controls;

/// <summary>
/// Draws the stroke that is being drawn right now. Points are normalized to the page, so the
/// overlay only has to scale them to its own bounds.
/// </summary>
public sealed class InkOverlay : Control
{
    public static readonly StyledProperty<IReadOnlyList<PdfSearchRect>?> PointsProperty =
        AvaloniaProperty.Register<InkOverlay, IReadOnlyList<PdfSearchRect>?>(nameof(Points));

    public static readonly StyledProperty<IBrush?> StrokeProperty =
        AvaloniaProperty.Register<InkOverlay, IBrush?>(nameof(Stroke), Brushes.Black);

    public static readonly StyledProperty<double> StrokeThicknessProperty =
        AvaloniaProperty.Register<InkOverlay, double>(nameof(StrokeThickness), 2d);

    static InkOverlay()
    {
        AffectsRender<InkOverlay>(PointsProperty, StrokeProperty, StrokeThicknessProperty);
    }

    public IReadOnlyList<PdfSearchRect>? Points
    {
        get => GetValue(PointsProperty);
        set => SetValue(PointsProperty, value);
    }

    public IBrush? Stroke
    {
        get => GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public double StrokeThickness
    {
        get => GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var points = Points;
        if (points is null || points.Count < 2 || Bounds.Width <= 0 || Bounds.Height <= 0)
        {
            return;
        }

        var geometry = new StreamGeometry();
        using (var sink = geometry.Open())
        {
            sink.BeginFigure(
                new Point(points[0].X * Bounds.Width, points[0].Y * Bounds.Height),
                isFilled: false);

            for (var index = 1; index < points.Count; index++)
            {
                sink.LineTo(new Point(points[index].X * Bounds.Width, points[index].Y * Bounds.Height));
            }

            sink.EndFigure(false);
        }

        context.DrawGeometry(null, new Pen(Stroke ?? Brushes.Black, StrokeThickness), geometry);
    }
}
