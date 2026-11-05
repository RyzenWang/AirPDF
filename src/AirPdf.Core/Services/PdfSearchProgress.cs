namespace AirPdf.Core.Services;

/// <summary>Progress of a full-document search while it is still running.</summary>
public readonly record struct PdfSearchProgress(
    int PagesScanned,
    int PageCount,
    int Hits,
    bool Truncated);
