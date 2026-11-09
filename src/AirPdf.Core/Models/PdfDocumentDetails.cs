namespace AirPdf.Core.Models;

/// <summary>
/// Metadata and outline that are useful once the document is already on screen, but are
/// not needed to render the first page. Loading them separately keeps a large file's
/// PdfPig pass out of the open-to-first-page path.
/// </summary>
public sealed class PdfDocumentDetails
{
    public double PdfVersion { get; init; } = 1.7;

    public PdfMetadata Metadata { get; init; } = new();

    public IReadOnlyList<PdfBookmark> Bookmarks { get; init; } = [];
}
