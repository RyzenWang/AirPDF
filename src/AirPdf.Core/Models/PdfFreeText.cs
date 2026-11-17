namespace AirPdf.Core.Models;

/// <summary>
/// A free-text annotation: page-space bounds, the text, and the font size used to draw it.
/// The bounds are kept so the annotation can be hit-tested, removed, and restored for undo.
/// </summary>
public sealed record PdfFreeText(
    int PageNumber,
    PdfQuad Bounds,
    string Text,
    float FontSize = 12);
