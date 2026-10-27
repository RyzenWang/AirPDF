namespace AirPdf.Core.Models;

/// <summary>
/// Immutable information about an opened PDF document.
/// </summary>
public sealed class PdfDocumentInfo
{
    public required string FilePath { get; init; }

    public required int PageCount { get; init; }

    public required IReadOnlyList<PdfPageInfo> Pages { get; init; }

    public required long FileSizeBytes { get; init; }

    public required double PdfVersion { get; init; }

    public PdfMetadata Metadata { get; init; } = new();

    public IReadOnlyList<PdfBookmark> Bookmarks { get; init; } = [];

    public string FileName => Path.GetFileName(FilePath);
}

/// <summary>
/// Size and rotation of a single PDF page. Sizes are expressed in PDF points (1/72 inch)
/// after applying the page rotation, matching what the renderer outputs.
/// </summary>
public sealed record PdfPageInfo(int Number, double Width, double Height, int Rotation)
{
    public double AspectRatio => Height <= 0 ? 1 : Width / Height;
}

public sealed class PdfMetadata
{
    public string? Title { get; init; }
    public string? Author { get; init; }
    public string? Subject { get; init; }
    public string? Keywords { get; init; }
    public string? Creator { get; init; }
    public string? Producer { get; init; }
    public DateTimeOffset? CreatedAt { get; init; }
    public DateTimeOffset? ModifiedAt { get; init; }

    public bool IsEmpty =>
        string.IsNullOrWhiteSpace(Title) &&
        string.IsNullOrWhiteSpace(Author) &&
        string.IsNullOrWhiteSpace(Subject) &&
        string.IsNullOrWhiteSpace(Keywords) &&
        string.IsNullOrWhiteSpace(Creator) &&
        string.IsNullOrWhiteSpace(Producer) &&
        CreatedAt is null &&
        ModifiedAt is null;
}

public sealed class PdfBookmark
{
    public required string Title { get; init; }

    public required int PageNumber { get; init; }

    public IReadOnlyList<PdfBookmark> Children { get; init; } = [];
}

/// <summary>
/// A normalized rectangle (0..1) with the origin at the top-left of the rendered page.
/// </summary>
public readonly record struct PdfSearchRect(double X, double Y, double Width, double Height);

public sealed class PdfSearchHit
{
    public required int PageNumber { get; init; }

    public required string Snippet { get; init; }

    public required IReadOnlyList<PdfSearchRect> Rects { get; init; }

    public required int MatchIndexOnPage { get; init; }
}
