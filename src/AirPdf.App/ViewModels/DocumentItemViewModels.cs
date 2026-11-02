using AirPdf.App.Services;
using AirPdf.Core.Models;
using AirPdf.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AirPdf.App.ViewModels;

public sealed class SearchResultViewModel : ObservableObject
{
    private readonly LocalizationService _localization;

    public SearchResultViewModel(PdfSearchHit hit, int index, LocalizationService localization)
    {
        _localization = localization;
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

    public string PageLabel => _localization.Format("L.PageUpperFormat", PageNumber);

    public void RefreshLanguage() => OnPropertyChanged(nameof(PageLabel));
}

public sealed class OutlineItemViewModel
{
    public OutlineItemViewModel(PdfBookmark bookmark)
    {
        Title = bookmark.Title;
        PageNumber = bookmark.PageNumber;
        Children = bookmark.Children.Select(child => new OutlineItemViewModel(child)).ToList();
    }

    public string Title { get; }

    public int PageNumber { get; }

    public IReadOnlyList<OutlineItemViewModel> Children { get; }

    public bool HasChildren => Children.Count > 0;

    public string PageLabel => PageNumber.ToString();
}

/// <summary>
/// One bookmark in the cross-document overview: the label plus which file and page it points
/// at, since several documents' bookmarks are listed together there.
/// </summary>
public sealed class BookmarkOverviewViewModel
{
    public BookmarkOverviewViewModel(BookmarkEntry entry, Func<string, int, string> describe)
    {
        Entry = entry;
        Title = entry.Label;
        Subtitle = describe(Path.GetFileName(entry.FilePath), entry.Page);
    }

    public BookmarkEntry Entry { get; }

    public string Title { get; }

    public string Subtitle { get; }

    public string FilePath => Entry.FilePath;

    public NavigationPoint Position => new(Entry.Page, Entry.PageOffset);
}
/// <summary>
/// A bookmark the reader created, as shown in the sidebar. Distinct from
/// <see cref="OutlineItemViewModel"/>, which mirrors the PDF's own outline.
/// </summary>
public sealed class UserBookmarkViewModel
{
    public UserBookmarkViewModel(BookmarkEntry entry, Func<int, string> pageLabel)
    {
        Entry = entry;
        PageLabel = pageLabel(entry.Page);
    }

    public BookmarkEntry Entry { get; }

    public string Label => Entry.Label;

    public int PageNumber => Entry.Page;

    public double PageOffset => Entry.PageOffset;

    public string PageLabel { get; }

    public NavigationPoint Position => new(Entry.Page, Entry.PageOffset);
}
public sealed record MetadataRow(string Label, string Value);

/// <summary>
/// A remembered reading position: a page plus the normalized offset within it.
/// Used by the back-navigation history.
/// </summary>
public readonly record struct NavigationPoint(int Page, double PageOffset);
