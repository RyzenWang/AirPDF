namespace AirPdf.Core.Models;

/// <summary>
/// A text layer for one page. Word rectangles are normalized to the page
/// (0..1, origin top-left), matching the rendered bitmap.
/// </summary>
public sealed class PdfTextPage
{
    public required int PageNumber { get; init; }

    public required IReadOnlyList<PdfTextWord> Words { get; init; }

    public bool HasText => Words.Count > 0;
}

public sealed class PdfTextWord
{
    public required int Index { get; init; }

    public required string Text { get; init; }

    public required PdfSearchRect Bounds { get; init; }
}
