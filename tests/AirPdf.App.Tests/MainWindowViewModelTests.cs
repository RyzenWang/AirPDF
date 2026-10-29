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
    public async Task Zoom_IsRememberedPerDocument()
    {
        using var context = new TestContext();
        await context.ViewModel.OpenPathAsync(SamplePath);
        context.ViewModel.UpdateViewport(1200, 900);
        context.ViewModel.ApplyInitialZoom();

        context.ViewModel.SetZoomCommand.Execute("1.5");
        Assert.Equal(1.5, context.ViewModel.Zoom, 3);

        // Reopening the same document must restore the manual zoom instead of
        // resetting to 100% and fitting the width again.
        await context.ViewModel.OpenPathAsync(SamplePath);
        context.ViewModel.UpdateViewport(1200, 900);
        context.ViewModel.ApplyInitialZoom();

        Assert.Equal(1.5, context.ViewModel.Zoom, 3);
    }

    [AvaloniaFact]
    public async Task Zoom_FitWidthModeIsRememberedAsFitWidth()
    {
        using var context = new TestContext();
        await context.ViewModel.OpenPathAsync(SamplePath);
        context.ViewModel.UpdateViewport(1200, 900);
        context.ViewModel.ApplyInitialZoom();

        var fitWidthZoom = context.ViewModel.Zoom;

        await context.ViewModel.OpenPathAsync(SamplePath);

        // A different viewport width must produce a different fitted zoom, which only
        // happens when the remembered mode is "fit width" rather than a frozen number.
        context.ViewModel.UpdateViewport(760, 900);
        context.ViewModel.ApplyInitialZoom();

        Assert.NotEqual(fitWidthZoom, context.ViewModel.Zoom, 3);
    }

    [AvaloniaFact]
    public async Task ReadingProgress_ReflectsPageAndOffset()
    {
        using var context = new TestContext();
        await context.ViewModel.OpenPathAsync(SamplePath);

        Assert.Equal("0%", context.ViewModel.ReadingProgressText);

        context.ViewModel.UpdateCurrentPageFromScroll(pageNumber: 3, pageOffset: 0);
        Assert.Equal("25%", context.ViewModel.ReadingProgressText);

        context.ViewModel.UpdateCurrentPageFromScroll(pageNumber: 3, pageOffset: 0.5);
        Assert.Equal("31%", context.ViewModel.ReadingProgressText);

        context.ViewModel.UpdateCurrentPageFromScroll(pageNumber: 8, pageOffset: 1);
        Assert.Equal("100%", context.ViewModel.ReadingProgressText);
    }

    [AvaloniaFact]
    public async Task NavigateBack_ReturnsToThePositionBeforeTheJump()
    {
        using var context = new TestContext();
        await context.ViewModel.OpenPathAsync(SamplePath);

        context.ViewModel.UpdateCurrentPageFromScroll(pageNumber: 1, pageOffset: 0.2);
        Assert.False(context.ViewModel.CanNavigateBack);

        context.ViewModel.NavigateToPage(5);
        Assert.Equal(5, context.ViewModel.CurrentPage);
        Assert.True(context.ViewModel.CanNavigateBack);

        context.ViewModel.NavigateBackCommand.Execute(null);

        Assert.Equal(1, context.ViewModel.CurrentPage);
        Assert.False(context.ViewModel.CanNavigateBack);
    }

    [AvaloniaFact]
    public async Task ThemeMode_CyclesThroughAllFourModes()
    {
        using var context = new TestContext();

        context.ViewModel.SetThemeMode("System", persist: false);
        Assert.True(context.ViewModel.IsSystemTheme);
        Assert.False(string.IsNullOrWhiteSpace(context.ViewModel.ThemeTooltip));

        // The cycle is System → Light → Dark → High contrast → System.
        context.ViewModel.ToggleThemeCommand.Execute(null);
        Assert.False(context.ViewModel.IsSystemTheme);
        Assert.False(context.ViewModel.IsDarkTheme);

        context.ViewModel.ToggleThemeCommand.Execute(null);
        Assert.True(context.ViewModel.IsDarkTheme);

        context.ViewModel.ToggleThemeCommand.Execute(null);
        Assert.False(context.ViewModel.IsSystemTheme);
        Assert.True(context.ViewModel.IsDarkTheme);

        context.ViewModel.ToggleThemeCommand.Execute(null);
        Assert.True(context.ViewModel.IsSystemTheme);
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
            ViewModel.Dispose();

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
