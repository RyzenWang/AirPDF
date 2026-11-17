namespace AirPdf.Core.Models;

/// <summary>Which kind of markup an annotation is, for the code that has to put it back.</summary>
public enum PdfMarkupKind
{
    Highlight,
    Underline,
    StrikeOut,
    Squiggly,
    Ink,
    Note,
    FreeText
}

/// <summary>
/// A markup annotation that was removed, kept as the snapshot needed to restore it. One type
/// rather than three so "remove whatever is under the pointer" can return a mixed list.
/// </summary>
public sealed record PdfMarkup(
    PdfMarkupKind Kind,
    PdfHighlight? Highlight = null,
    PdfInk? Ink = null,
    PdfNote? Note = null,
    PdfFreeText? FreeText = null,
    PdfTextMarkup? TextMarkup = null)
{
    public int PageNumber => Kind switch
    {
        PdfMarkupKind.Highlight => Highlight?.PageNumber ?? 0,
        PdfMarkupKind.Underline or PdfMarkupKind.StrikeOut or PdfMarkupKind.Squiggly =>
            TextMarkup?.PageNumber ?? 0,
        PdfMarkupKind.Ink => Ink?.PageNumber ?? 0,
        PdfMarkupKind.FreeText => FreeText?.PageNumber ?? 0,
        _ => Note?.PageNumber ?? 0
    };
}