namespace AirPdf.Core.Models;

/// <summary>
/// Metadata and outline that are useful once the document is already on screen, but are
/// not needed to render the first page. They are read from the resident PDFium handle only
/// when the reader opens the contents or document-information panel.
/// </summary>
public sealed class PdfDocumentDetails
{
    public double PdfVersion { get; init; } = 1.7;

    public PdfMetadata Metadata { get; init; } = new();

    public IReadOnlyList<PdfBookmark> Bookmarks { get; init; } = [];
}
