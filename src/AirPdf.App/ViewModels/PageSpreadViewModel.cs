namespace AirPdf.App.ViewModels;

/// <summary>
/// One row of the two-page reader. The right page is null for a document with an odd page
/// count, so the last row still has the same shape as every other row.
/// </summary>
public sealed class PageSpreadViewModel
{
    public PageSpreadViewModel(PageViewModel leftPage, PageViewModel? rightPage)
    {
        LeftPage = leftPage;
        RightPage = rightPage;
    }

    public PageViewModel LeftPage { get; }

    public PageViewModel? RightPage { get; }

    public bool HasRightPage => RightPage is not null;

    public static IReadOnlyList<PageSpreadViewModel> Create(IReadOnlyList<PageViewModel> pages)
    {
        if (pages.Count == 0)
        {
            return [];
        }

        var spreads = new List<PageSpreadViewModel>((pages.Count + 1) / 2);
        for (var index = 0; index < pages.Count; index += 2)
        {
            spreads.Add(new PageSpreadViewModel(
                pages[index],
                index + 1 < pages.Count ? pages[index + 1] : null));
        }

        return spreads;
    }
}
