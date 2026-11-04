namespace AirPdf.Core.Models;

/// <summary>A point in page space (points, origin bottom-left).</summary>
public readonly record struct PdfPoint(double X, double Y);

/// <summary>One freehand stroke: the points the pointer passed through, in page space.</summary>
public sealed record PdfInkStroke(IReadOnlyList<PdfPoint> Points);

/// <summary>
/// A freehand ink annotation. Kept as strokes rather than as a flattened shape so it can be put
/// back unchanged when an edit is undone.
/// </summary>
public sealed record PdfInk(
    int PageNumber,
    IReadOnlyList<PdfInkStroke> Strokes,
    byte Red,
    byte Green,
    byte Blue,
    float Width);

/// <summary>A sticky note: a point on the page and the text attached to it.</summary>
public sealed record PdfNote(int PageNumber, PdfPoint Position, string Text);