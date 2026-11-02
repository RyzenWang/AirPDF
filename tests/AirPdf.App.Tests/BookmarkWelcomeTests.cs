using AirPdf.App.Services;
using AirPdf.App.ViewModels;
using AirPdf.App.Views;
using Avalonia.Headless.XUnit;
using Xunit;

namespace AirPdf.App.Tests;

/// <summary>
/// Opening a bookmark from the welcome screen is the one path that opens a document and
/// jumps in the same gesture, so it is worth checking that the target page actually renders
/// rather than staying a placeholder.
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
    public async Task OpeningABookmarkFromTheWelcomeScreen_RendersTheTargetPage()
    {
        var services = new AppServices(_settingsDirectory);
        var window = new MainWindow(services, new StubWindowService(), null, restoreWindowPosition: false);
        window.Show();

        var viewModel = Assert.IsType<MainWindowViewModel>(window.DataContext);

        await viewModel.OpenPathAsync(SamplePath);
        viewModel.UpdateCurrentPageFromScroll(pageNumber: 3, pageOffset: 0);
        viewModel.AddBookmarkCommand.Execute(null);
        var bookmark = Assert.Single(viewModel.AllBookmarks);

        viewModel.CloseDocumentCommand.Execute(null);
        await Task.Delay(300);
        Assert.False(viewModel.IsDocumentOpen);
        Assert.True(viewModel.IsWelcomeVisible);

        await viewModel.OpenBookmarkCommand.ExecuteAsync(bookmark);
        await WaitForRenderAsync(viewModel, expectedPageIndex: 2);

        Assert.True(viewModel.IsDocumentOpen);
        Assert.False(viewModel.IsWelcomeVisible);

        // The window consumed the jump during the restore pass instead of a second scroll.
        Assert.Null(viewModel.TakePendingOpenPosition());

        // The target page must have been realised, which only happens when the view is scrolled
        // to it; a placeholder is the failure the reader reported.
        Assert.NotNull(viewModel.Pages[2].Image);
        Assert.False(viewModel.Pages[2].ShowPlaceholder);
        Assert.Null(viewModel.Pages[2].RenderError);

        // CurrentPage is deliberately not asserted: it is derived from the scroll offset, and a
        // headless window has no real layout, so the offset is clamped to 0 and the page number
        // falls back to 1. The rendered page above is the platform-independent signal.

        window.Close();
    }

    private static async Task WaitForRenderAsync(MainWindowViewModel viewModel, int expectedPageIndex)
    {
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (DateTime.UtcNow < deadline)
        {
            if (viewModel.Pages.Count > expectedPageIndex && viewModel.Pages[expectedPageIndex].Image is not null)
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
