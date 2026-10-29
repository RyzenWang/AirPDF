using AirPdf.App.Services;
using AirPdf.App.ViewModels;
using AirPdf.Core.Services;
using Avalonia.Headless.XUnit;
using Xunit;

namespace AirPdf.App.Tests;

public sealed class MainWindowViewModelTests
{
    private static readonly string SamplePath =
        Path.Combine(AppContext.BaseDirectory, "Assets", "sample.pdf");

    [AvaloniaFact]
    public async Task OpenPathAsync_LoadsSampleDocument()
    {
        using var context = new TestContext();

        await context.ViewModel.OpenPathAsync(SamplePath);

        Assert.True(context.ViewModel.IsDocumentOpen);
        Assert.Equal(8, context.ViewModel.PageCount);
        Assert.Equal(8, context.ViewModel.Pages.Count);
        Assert.Equal("sample.pdf", context.ViewModel.FileName);
    }

    [AvaloniaFact]
    public async Task SearchCommand_FindsTextInSample()
    {
        using var context = new TestContext();
        await context.ViewModel.OpenPathAsync(SamplePath);

        context.ViewModel.SearchQuery = "AirPDF";
        await context.ViewModel.SearchCommand.ExecuteAsync(null);

        Assert.NotEmpty(context.ViewModel.SearchResults);
        Assert.Contains(context.ViewModel.SearchResults, result => result.PageNumber == 1);
    }

    [AvaloniaFact]
    public async Task TextSelection_LoadsPageOnDemandAndSelectsWord()
    {
        using var context = new TestContext();
        await context.ViewModel.OpenPathAsync(SamplePath);

        const int pageNumber = 7;
        Assert.True(await context.ViewModel.EnsureTextLayerAsync(pageNumber));
        var page = context.ViewModel.Pages[pageNumber - 1];
        Assert.True(page.HasTextLayer);

        var word = page.Words[0];
        var x = word.Bounds.X + (word.Bounds.Width / 2);
        var y = word.Bounds.Y + (word.Bounds.Height / 2);

        Assert.True(await context.ViewModel.BeginTextSelectionAsync(pageNumber, x, y, clickCount: 2));
        context.ViewModel.CompleteTextSelection();

        Assert.Equal(word.Text, context.ViewModel.GetSelectedText());
    }

    [AvaloniaFact]
    public async Task ReadingPosition_RoundTripsAcrossReopen()
    {
        using var context = new TestContext();
        await context.ViewModel.OpenPathAsync(SamplePath);

        context.ViewModel.UpdateCurrentPageFromScroll(pageNumber: 3, pageOffset: 0.42);
        context.ViewModel.SaveReadingPositionNow();

        await context.ViewModel.OpenPathAsync(SamplePath);

        var pending = context.ViewModel.TakePendingReadingPosition();
        Assert.NotNull(pending);
        Assert.Equal(3, pending!.Page);
        Assert.Equal(0.42, pending.PageOffset, 3);
    }

    [AvaloniaFact]
    public async Task ZoomAndLanguageCommands_UpdateViewModelState()
    {
        using var context = new TestContext();
        await context.ViewModel.OpenPathAsync(SamplePath);

        var initialZoom = context.ViewModel.Zoom;
        context.ViewModel.ZoomInCommand.Execute(null);
        Assert.True(context.ViewModel.Zoom > initialZoom);

        context.ViewModel.ToggleLanguageCommand.Execute(null);
        Assert.Equal("中", context.ViewModel.LanguageShortLabel);

        context.ViewModel.ToggleLanguageCommand.Execute(null);
        Assert.Equal("EN", context.ViewModel.LanguageShortLabel);
    }

    private sealed class TestContext : IDisposable
    {
        private readonly string _settingsDirectory;

        public TestContext()
        {
            _settingsDirectory = Path.Combine(
                Path.GetTempPath(),
                $"airpdf-app-tests-{Guid.NewGuid():N}");

            var settingsService = new SettingsService(_settingsDirectory);
            var settings = settingsService.Load();
            var localization = new LocalizationService();
            localization.Apply(LocalizationService.English);

            ViewModel = new MainWindowViewModel(
                new PdfDocumentService(),
                new PdfSearchService(),
                new PdfTextService(),
                settingsService,
                new UpdateService(),
                new StubDialogService(),
                settings,
                localization);
        }

        public MainWindowViewModel ViewModel { get; }

        public void Dispose()
        {
            ViewModel.CloseDocumentCommand.Execute(null);

            if (Directory.Exists(_settingsDirectory))
            {
                Directory.Delete(_settingsDirectory, recursive: true);
            }
        }
    }

    private sealed class StubDialogService : IUserDialogService
    {
        public Task<string?> PickPdfAsync() => Task.FromResult<string?>(null);

        public Task<string?> PromptPasswordAsync(string fileName, bool isRetry) =>
            Task.FromResult<string?>(null);

        public Task ShowErrorAsync(string title, string message) => Task.CompletedTask;
    }
}
