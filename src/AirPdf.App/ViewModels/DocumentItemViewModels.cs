using AirPdf.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AirPdf.App.ViewModels;

public sealed class SearchResultViewModel
{
    public SearchResultViewModel(PdfSearchHit hit, int index)
    {
        Index = index;
        PageNumber = hit.PageNumber;
        Snippet = hit.Snippet;
        Rects = hit.Rects;
        MatchIndexOnPage = hit.MatchIndexOnPage;
    }

    public int Index { get; }

    public int PageNumber { get; }

    public string Snippet { get; }

    public IReadOnlyList<PdfSearchRect> Rects { get; }

    public int MatchIndexOnPage { get; }

    public string PageLabel => $"PAGE {PageNumber}";
}

public sealed class BookmarkViewModel
{
    public BookmarkViewModel(PdfBookmark bookmark)
    {
        Title = bookmark.Title;
        PageNumber = bookmark.PageNumber;
        Children = bookmark.Children.Select(child => new BookmarkViewModel(child)).ToList();
    }

    public string Title { get; }

    public int PageNumber { get; }

    public IReadOnlyList<BookmarkViewModel> Children { get; }

    public bool HasChildren => Children.Count > 0;

    public string PageLabel => PageNumber.ToString();
}

public sealed record MetadataRow(string Label, string Value);
