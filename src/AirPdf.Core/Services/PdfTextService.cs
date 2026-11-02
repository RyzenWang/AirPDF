using AirPdf.Core.Models;

namespace AirPdf.Core.Services;

/// <summary>
/// Reads the text layer through PDFium's text API, using the same document handle the pages
/// are rendered from.
/// </summary>
/// <remarks>
/// This replaced PdfPig for the text layer. PdfPig applies <c>/Rotate</c> to its letters
/// before grouping them, so a quarter turn came back as single-letter words and a half turn
/// as reversed text; PDFium reports characters in reading order with boxes in unrotated page
/// space, which <see cref="Pdfium.PdfTextPageBuilder"/> maps and groups.
/// </remarks>
public sealed class PdfTextService
{
    private readonly PdfDocumentService _documents;

    public PdfTextService(PdfDocumentService documents) => _documents = documents;

    /// <summary>Walks the whole document, reporting each page as it is read.</summary>
    public async Task ExtractAsync(
        string filePath,
        string? password,
        IProgress<PdfTextPage>? progress,
        CancellationToken cancellationToken = default)
    {
        var pageCount = _documents.OpenPageCount;

        for (var pageNumber = 1; pageNumber <= pageCount; pageNumber++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var page = await _documents.GetTextPageAsync(pageNumber, cancellationToken).ConfigureAwait(false);
            progress?.Report(page);
        }
    }

    /// <summary>Extracts only the requested pages.</summary>
    public async Task<IReadOnlyList<PdfTextPage>> ExtractPagesAsync(
        string filePath,
        IReadOnlyCollection<int> pageNumbers,
        string? password,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pageNumbers);

        var normalized = pageNumbers
            .Where(pageNumber => pageNumber > 0)
            .Distinct()
            .OrderBy(pageNumber => pageNumber)
            .ToArray();

        if (normalized.Length == 0)
        {
            return [];
        }

        var results = new List<PdfTextPage>(normalized.Length);
        foreach (var pageNumber in normalized)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(await _documents.GetTextPageAsync(pageNumber, cancellationToken).ConfigureAwait(false));
        }

        return results;
    }
}
