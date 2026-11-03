using AirPdf.App.Services;
using AirPdf.App.ViewModels;
using AirPdf.App.Views;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Xunit;

namespace AirPdf.App.Tests;

/// <summary>
/// The welcome screen is a tab now, so clicking one of its bookmarks has to deal with a
/// document that may already be open in another tab: it must focus that tab rather than open
/// a second copy, and the target page must actually render.
/// </summary>
public sealed class BookmarkWelcomeTests : IDisposable
{
    private static readonly string SamplePath =
        Path.Combine(AppContext.BaseDirectory, "Assets", "sample.pdf");

    private readonly string _settingsDirectory =
        Path.Combine(Path.GetTempPath(), $"airpdf-welcome-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_settingsDirectory))
        {
            Directory.Delete(_settingsDirectory, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task OpeningABookmarkFromHome_FocusesTheOpenTabAndRendersThePage()
    {
        var services = new AppServices(_settingsDirectory);
        var window = new MainWindow(services, new StubWindowService(), null, restoreWindowPosition: false);
        window.Show();

        var home = Assert.IsType<MainWindowViewModel>(window.DataContext);
        Assert.True(home.IsHomeTab);

        await home.OpenPathAsync(SamplePath);

        var document = Assert.Single(Tabs(window), tab => !tab.IsHomeTab);
        await WaitUntilAsync(() => document.IsDocumentOpen);
        Assert.True(document.IsDocumentOpen);

        document.UpdateCurrentPageFromScroll(pageNumber: 3, pageOffset: 0);
        document.AddBookmarkCommand.Execute(null);
        var bookmark = Assert.Single(document.AllBookmarks);

        // Back to home: the document stays open in its tab.
        document.GoHomeCommand.Execute(null);
        Assert.Same(home, window.DataContext);
        Assert.True(document.IsDocumentOpen);

        home.OpenBookmarkCommand.Execute(bookmark);
        await WaitUntilAsync(() => ReferenceEquals(window.DataContext, document) && document.Pages.Count > 2);
        await WaitUntilAsync(() => document.Pages.Count > 2 && document.Pages[2].Image is not null);

        // The existing tab was focused instead of a duplicate being opened for the same file.
        Assert.Single(Tabs(window), tab => !tab.IsHomeTab);
        Assert.Same(document, window.DataContext);

        // The target page was realised and rendered: a placeholder is what the reader saw.
        Assert.NotNull(document.Pages[2].Image);
        Assert.False(document.Pages[2].ShowPlaceholder);
        Assert.Null(document.Pages[2].RenderError);

        window.Close();
    }

    private static IReadOnlyList<MainWindowViewModel> Tabs(MainWindow window)
    {
        var list = window.FindControl<ListBox>("TabList");
        Assert.NotNull(list);

        return list!.ItemsSource is IEnumerable<MainWindowViewModel> tabs ? tabs.ToList() : [];
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(100);
        }
    }

    private sealed class StubWindowService : IWindowService
    {
        public void OpenNewWindow(string? filePath = null)
        {
        }

        public void OpenInNewWindow(string filePath)
        {
        }
    }
}
