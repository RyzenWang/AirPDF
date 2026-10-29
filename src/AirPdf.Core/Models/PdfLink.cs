namespace AirPdf.Core.Models;

public enum PdfLinkKind
{
    Unknown = 0,

    /// <summary>Opens an external URI in the system handler.</summary>
    Uri = 1,

    /// <summary>Jumps to another page of the same document.</summary>
    Page = 2
}

/// <summary>
/// A clickable region on a page. Bounds are normalized to 0..1 with the origin at the
/// top-left of the rendered page, matching <see cref="PdfSearchRect"/>.
/// </summary>
public sealed class PdfLink
{
    public required PdfSearchRect Bounds { get; init; }

    public required PdfLinkKind Kind { get; init; }

    /// <summary>Set when <see cref="Kind"/> is <see cref="PdfLinkKind.Uri"/>.</summary>
    public string? Uri { get; init; }

    /// <summary>One-based target page when <see cref="Kind"/> is <see cref="PdfLinkKind.Page"/>.</summary>
    public int PageNumber { get; init; }

    public bool Contains(double x, double y, double padding = 0.002) =>
        x >= Bounds.X - padding &&
        x <= Bounds.X + Bounds.Width + padding &&
        y >= Bounds.Y - padding &&
        y <= Bounds.Y + Bounds.Height + padding;
}
