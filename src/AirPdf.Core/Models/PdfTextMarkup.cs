namespace AirPdf.Core.Models;

/// <summary>The text-markup annotation flavours that use PDFium attachment points.</summary>
public enum PdfTextMarkupKind
{
    Underline,
    StrikeOut,
    Squiggly
}

/// <summary>
/// A text underline, strikeout, or squiggly annotation: the selected rectangles and its
/// colour. Kept separately from <see cref="PdfHighlight"/> so existing highlight behaviour
/// and persistence stay unchanged.
/// </summary>
public sealed record PdfTextMarkup(
    int PageNumber,
    PdfTextMarkupKind Kind,
    IReadOnlyList<PdfQuad> Quads,
    byte Red,
    byte Green,
    byte Blue);
