using Avalonia.Media;
using AirPdf.Core.Models;

namespace AirPdf.App.ViewModels;

/// <summary>Where a markup-list click should scroll: page plus normalized display bounds.</summary>
public sealed record MarkupNavigationPoint(int PageNumber, PdfSearchRect Bounds);

/// <summary>One row in the sidebar's markup list.</summary>
public sealed class AnnotationItemViewModel
{
    public AnnotationItemViewModel(
        int pageNumber,
        PdfMarkupKind kind,
        string title,
        string detail,
        string pageLabel,
        string glyph,
        byte red,
        byte green,
        byte blue,
        PdfQuad bounds)
    {
        PageNumber = pageNumber;
        Kind = kind;
        Title = title;
        Detail = detail;
        PageLabel = pageLabel;
        Glyph = glyph;
        Bounds = bounds;
        Brush = new SolidColorBrush(Color.FromRgb(red, green, blue));
    }

    public int PageNumber { get; }

    public PdfMarkupKind Kind { get; }

    public string Title { get; }

    public string Detail { get; }

    public string PageLabel { get; }

    public bool HasDetail => !string.IsNullOrWhiteSpace(Detail);

    public string Glyph { get; }

    /// <summary>Page-space bounds; navigation maps this onto the displayed page.</summary>
    public PdfQuad Bounds { get; }

    public IBrush Brush { get; }
}
