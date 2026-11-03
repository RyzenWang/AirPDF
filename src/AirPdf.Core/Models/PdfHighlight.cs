namespace AirPdf.Core.Models;

/// <summary>
/// Everything needed to reproduce a highlight: its page, the rectangles it covers, and its
/// colour. Removing a highlight captures one of these so the removal can be undone, and adding
/// one produces the same shape for the opposite direction.
/// </summary>
public sealed record PdfHighlight(
    int PageNumber,
    IReadOnlyList<PdfQuad> Quads,
    byte Red,
    byte Green,
    byte Blue);