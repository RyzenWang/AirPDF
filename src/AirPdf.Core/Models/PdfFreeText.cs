namespace AirPdf.Core.Models;

/// <summary>
/// A free-text annotation: page-space bounds, the text, the font size used to draw it, and the
/// glyph colour. The bounds are kept so the annotation can be hit-tested, removed, edited, and
/// restored for undo.
/// </summary>
public sealed record PdfFreeText(
    int PageNumber,
    PdfQuad Bounds,
    string Text,
    float FontSize = 12,
    byte Red = 0,
    byte Green = 0,
    byte Blue = 0);
