using AirPdf.App.Services;
using AirPdf.App.ViewModels;
using AirPdf.Core.Models;
using AirPdf.Core.Services;
using Avalonia.Headless.XUnit;
using Xunit;

namespace AirPdf.App.Tests;

public sealed class MainWindowViewModelTests
{
    private static readonly string SamplePath =
        Path.Combine(AppContext.BaseDirectory, "Assets", "sample.pdf");

    private static readonly string LinksPath =
        Path.Combine(AppContext.BaseDirectory, "Assets", "links.pdf");

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

        context.Services.Theme.Set(ThemeService.SystemMode, persist: false);
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

    /// <summary>
    /// Two windows share one theme service, so a change made in one must be visible in the
    /// other without either of them owning the state.
    /// </summary>
    [AvaloniaFact]
    public void ThemeChange_IsVisibleToEveryWindow()
    {
        using var context = new TestContext();
        using var second = context.CreateSecondViewModel();

        Assert.Equal(context.ViewModel.IsDarkTheme, second.IsDarkTheme);

        context.Services.Theme.Set(ThemeService.DarkMode, persist: false);

        Assert.True(context.ViewModel.IsDarkTheme);
        Assert.True(second.IsDarkTheme);

        context.Services.Theme.Set(ThemeService.LightMode, persist: false);

        Assert.False(context.ViewModel.IsDarkTheme);
        Assert.False(second.IsDarkTheme);
    }

    [AvaloniaFact]
    public async Task Links_AreHitTestable_AndInternalDestinationsNavigate()
    {
        using var context = new TestContext();
        await context.ViewModel.OpenPathAsync(LinksPath);

        // Page 2 of the fixture links back to page 1.
        await context.ViewModel.EnsureLinksAsync(2);
        var page = context.ViewModel.Pages[1];
        var link = Assert.Single(page.Links);
        Assert.Equal(PdfLinkKind.Page, link.Kind);
        Assert.Equal(1, link.PageNumber);

        context.ViewModel.UpdateCurrentPageFromScroll(pageNumber: 2, pageOffset: 0);

        var hit = page.HitTestLink(
            link.Bounds.X + (link.Bounds.Width / 2),
            link.Bounds.Y + (link.Bounds.Height / 2));
        Assert.NotNull(hit);
        Assert.Null(page.HitTestLink(0.98, 0.02));

        context.ViewModel.OpenLink(hit!);

        Assert.Equal(1, context.ViewModel.CurrentPage);
        Assert.True(context.ViewModel.CanNavigateBack);
    }

    [AvaloniaFact]
    public void NewWindowCommand_AsksTheWindowService()
    {
        using var context = new TestContext();

        context.ViewModel.NewWindowCommand.Execute(null);

        Assert.Single(context.Windows.NewWindows);
        Assert.Null(context.Windows.NewWindows[0]);
    }

    [AvaloniaFact]
    public async Task OpenInNewWindowCommand_PassesTheCurrentDocument()
    {
        using var context = new TestContext();
        await context.ViewModel.OpenPathAsync(SamplePath);

        context.ViewModel.OpenInNewWindowCommand.Execute(null);

        Assert.Equal(SamplePath, Assert.Single(context.Windows.DocumentsInNewWindows));
    }

    [AvaloniaFact]
    public async Task OpenInNewWindowCommand_DoesNothingWithoutADocument()
    {
        using var context = new TestContext();

        context.ViewModel.OpenInNewWindowCommand.Execute(null);

        Assert.Empty(context.Windows.DocumentsInNewWindows);
    }

    /// <summary>
    /// Windows share one settings store, so a change made in one window is not lost when
    /// another window saves (the old design reloaded and rewrote the file per window).
    /// </summary>
    [AvaloniaFact]
    public async Task TwoWindows_ShareOneSettingsInstance()
    {
        using var context = new TestContext();
        var second = context.CreateSecondViewModel();

        Assert.Same(context.ViewModel.Settings, second.Settings);
        Assert.Same(context.ViewModel.Settings.RecentFiles, second.Settings.RecentFiles);

        await context.ViewModel.OpenPathAsync(SamplePath);

        Assert.Contains(
            context.Services.Settings.RecentFiles,
            file => file.FilePath == SamplePath);

        // The second window rebuilt its shelf from the shared list.
        Assert.Contains(second.RecentFiles, file => file.FilePath == SamplePath);
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
        private readonly List<MainWindowViewModel> _viewModels = [];

        public TestContext()
        {
            _settingsDirectory = Path.Combine(
                Path.GetTempPath(),
                $"airpdf-app-tests-{Guid.NewGuid():N}");

            Services = new AppServices(_settingsDirectory);
            Windows = new StubWindowService();
            ViewModel = CreateViewModel();
        }

        public AppServices Services { get; }

        public StubWindowService Windows { get; }

        public MainWindowViewModel ViewModel { get; }

        /// <summary>Another window over the same services, as a real second window would be.</summary>
        public MainWindowViewModel CreateSecondViewModel() => CreateViewModel();

        public void Dispose()
        {
            foreach (var viewModel in _viewModels)
            {
                viewModel.Dispose();
            }

            Services.Dispose();

            if (Directory.Exists(_settingsDirectory))
            {
                Directory.Delete(_settingsDirectory, recursive: true);
            }
        }

        private MainWindowViewModel CreateViewModel()
        {
            var viewModel = new MainWindowViewModel(
                Services,
                new PdfDocumentService(),
                new PdfSearchService(),
                new PdfTextService(),
                new StubDialogService(),
                Windows);

            _viewModels.Add(viewModel);
            return viewModel;
        }

        internal sealed class StubWindowService : IWindowService
        {
            public List<string?> NewWindows { get; } = [];

            public List<string> DocumentsInNewWindows { get; } = [];

            public void OpenNewWindow(string? filePath = null) => NewWindows.Add(filePath);

            public void OpenInNewWindow(string filePath) => DocumentsInNewWindows.Add(filePath);
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
