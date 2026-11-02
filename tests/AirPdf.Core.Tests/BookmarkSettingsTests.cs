using AirPdf.Core.Services;
using Xunit;

namespace AirPdf.Core.Tests;

/// <summary>
/// Reader bookmarks are stored per file in the settings file, next to the reading position.
/// These cover the storage rules: scoping, ordering, de-duplication, and the cap.
/// </summary>
public sealed class BookmarkSettingsTests
{
    private const string Document = @"C:\docs\manual.pdf";

    [Fact]
    public void AddBookmark_StoresItForThatFileOnly()
    {
        var service = new SettingsService(Path.Combine(Path.GetTempPath(), $"airpdf-bm-{Guid.NewGuid():N}"));
        var settings = new AppSettings();

        service.AddBookmark(settings, Document, 12, 0.25, "Page 12");

        Assert.Single(service.GetBookmarks(settings, Document));
        Assert.Empty(service.GetBookmarks(settings, @"C:\docs\other.pdf"));
    }

    [Fact]
    public void GetBookmarks_OrdersByPageThenOffset()
    {
        var service = new SettingsService(Path.Combine(Path.GetTempPath(), $"airpdf-bm-{Guid.NewGuid():N}"));
        var settings = new AppSettings();

        service.AddBookmark(settings, Document, 40, 0, "Page 40");
        service.AddBookmark(settings, Document, 3, 0.8, "Page 3");
        service.AddBookmark(settings, Document, 3, 0.1, "Page 3");

        var pages = service.GetBookmarks(settings, Document).Select(entry => (entry.Page, entry.PageOffset));

        Assert.Equal([(3, 0.1), (3, 0.8), (40, 0.0)], pages);
    }

    [Fact]
    public void AddBookmark_OnTheSameSpot_ReturnsTheExistingEntry()
    {
        var service = new SettingsService(Path.Combine(Path.GetTempPath(), $"airpdf-bm-{Guid.NewGuid():N}"));
        var settings = new AppSettings();

        var first = service.AddBookmark(settings, Document, 7, 0.5, "Page 7");
        var second = service.AddBookmark(settings, Document, 7, 0.5005, "Page 7");

        Assert.Same(first, second);
        Assert.Single(settings.Bookmarks);
    }

    [Fact]
    public void AddBookmark_OnTheSamePageAtADifferentOffset_AddsAnother()
    {
        var service = new SettingsService(Path.Combine(Path.GetTempPath(), $"airpdf-bm-{Guid.NewGuid():N}"));
        var settings = new AppSettings();

        service.AddBookmark(settings, Document, 7, 0.1, "Page 7");
        service.AddBookmark(settings, Document, 7, 0.9, "Page 7");

        Assert.Equal(2, service.GetBookmarks(settings, Document).Count);
    }

    [Fact]
    public void RemoveBookmark_DropsOnlyThatEntry()
    {
        var service = new SettingsService(Path.Combine(Path.GetTempPath(), $"airpdf-bm-{Guid.NewGuid():N}"));
        var settings = new AppSettings();

        var keep = service.AddBookmark(settings, Document, 2, 0, "Page 2");
        var drop = service.AddBookmark(settings, Document, 5, 0, "Page 5");

        Assert.True(service.RemoveBookmark(settings, drop));

        var remaining = service.GetBookmarks(settings, Document);
        Assert.Same(keep, Assert.Single(remaining));
        Assert.False(service.RemoveBookmark(settings, drop));
    }

    [Fact]
    public void Bookmarks_AreCappedPerDocument()
    {
        var service = new SettingsService(Path.Combine(Path.GetTempPath(), $"airpdf-bm-{Guid.NewGuid():N}"));
        var settings = new AppSettings();

        for (var page = 1; page <= 130; page++)
        {
            service.AddBookmark(settings, Document, page, 0, $"Page {page}");
        }

        var stored = service.GetBookmarks(settings, Document);

        Assert.Equal(100, stored.Count);

        // The newest survive, so the first pages are the ones that fall off.
        Assert.DoesNotContain(stored, entry => entry.Page == 1);
        Assert.Contains(stored, entry => entry.Page == 130);
    }

    [Fact]
    public void Bookmarks_SurviveASettingsRoundTrip()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"airpdf-bm-{Guid.NewGuid():N}");
        try
        {
            var service = new SettingsService(directory);
            var settings = new AppSettings();
            service.AddBookmark(settings, Document, 9, 0.33, "Page 9");
            service.Save(settings);

            var reloaded = service.Load();
            var bookmark = Assert.Single(service.GetBookmarks(reloaded, Document));

            Assert.Equal(9, bookmark.Page);
            Assert.Equal(0.33, bookmark.PageOffset, 3);
            Assert.Equal("Page 9", bookmark.Label);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
