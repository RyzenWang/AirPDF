using AirPdf.App.Services;
using AirPdf.App.ViewModels;
using AirPdf.Core.Services;
using Avalonia.Headless.XUnit;
using Xunit;

namespace AirPdf.App.Tests;

/// <summary>
/// The tab host owns the tabs of one window. Each tab is a full document view model, so
/// these tests check switching, closing, and disposal rather than any UI.
/// </summary>
public sealed class WindowViewModelTests : IDisposable
{
    private static readonly string SamplePath =
        Path.Combine(AppContext.BaseDirectory, "Assets", "sample.pdf");

    private readonly string _settingsDirectory =
        Path.Combine(Path.GetTempPath(), $"airpdf-tabs-{Guid.NewGuid():N}");

    private AppServices? _services;
    private WindowViewModel? _host;
    private readonly StubDialogService _dialog = new();

    public void Dispose()
    {
        _host?.Dispose();
        _services?.Dispose();

        if (Directory.Exists(_settingsDirectory))
        {
            Directory.Delete(_settingsDirectory, recursive: true);
        }
    }

    [AvaloniaFact]
    public void AddTab_AddsADocumentTabNextToHome()
    {
        var host = CreateHost();

        // The window opens on its home tab, so the strip is visible from the start.
        Assert.Single(host.Tabs);
        Assert.Same(host.HomeTab, host.SelectedTab);
        Assert.True(host.HomeTab.IsHomeTab);
        Assert.True(host.HasTabs);

        var first = host.AddTab();
        Assert.Equal(2, host.Tabs.Count);
        Assert.Same(first, host.SelectedTab);
        Assert.False(first.IsHomeTab);

        var second = host.AddTab();
        Assert.Equal(3, host.Tabs.Count);
        Assert.Same(second, host.SelectedTab);
    }

    [AvaloniaFact]
    public void CloseTab_OnTheHomeTab_KeepsIt()
    {
        var host = CreateHost();
        var document = host.AddTab();

        host.CloseTab(host.HomeTab);

        Assert.Equal(2, host.Tabs.Count);
        Assert.Contains(host.HomeTab, host.Tabs);
        Assert.Contains(document, host.Tabs);
    }

    [AvaloniaFact]
    public async Task OpeningFromHome_AddsADocumentTabAndLeavesHomeEmpty()
    {
        var host = CreateHost();

        await host.HomeTab.OpenPathAsync(SamplePath);

        Assert.Equal(2, host.Tabs.Count);
        Assert.False(host.HomeTab.IsDocumentOpen);

        var document = Assert.Single(host.Tabs, tab => !tab.IsHomeTab);
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!document.IsDocumentOpen && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        Assert.True(document.IsDocumentOpen);
        Assert.Same(document, host.SelectedTab);
    }

    [AvaloniaFact]
    public async Task CloseTab_OnTheLastDocumentTab_RemovesItAndShowsHome()
    {
        var host = CreateHost();
        var tab = host.AddTab();
        await tab.OpenPathAsync(SamplePath);
        Assert.True(tab.IsDocumentOpen);

        host.CloseTab(tab);

        Assert.Single(host.Tabs);
        Assert.Same(host.HomeTab, host.SelectedTab);
    }

    [AvaloniaFact]
    public async Task GoHome_KeepsTheDocumentOpenInItsTab()
    {
        var host = CreateHost();
        var tab = host.AddTab();
        await tab.OpenPathAsync(SamplePath);
        Assert.Same(tab, host.SelectedTab);

        tab.GoHomeCommand.Execute(null);

        Assert.Same(host.HomeTab, host.SelectedTab);
        Assert.True(tab.IsDocumentOpen);
        Assert.Contains(tab, host.Tabs);
    }

    [AvaloniaFact]
    public void CloseTab_WithTwoTabs_RemovesItAndSelectsTheOther()
    {
        var host = CreateHost();
        var first = host.AddTab();
        var second = host.AddTab();

        host.CloseTab(second);

        Assert.Equal(2, host.Tabs.Count);
        Assert.Contains(first, host.Tabs);
        Assert.DoesNotContain(second, host.Tabs);
        Assert.Same(host.HomeTab, host.SelectedTab);
    }

    [AvaloniaFact]
    public void SelectNext_WrapsInBothDirections()
    {
        var host = CreateHost();
        var first = host.AddTab();
        var second = host.AddTab();
        Assert.Same(second, host.SelectedTab);

        // Home is the first tab, so cycling wraps through it.
        host.SelectNext(1);
        Assert.Same(host.HomeTab, host.SelectedTab);

        host.SelectNext(1);
        Assert.Same(first, host.SelectedTab);

        host.SelectNext(-1);
        Assert.Same(host.HomeTab, host.SelectedTab);

        host.SelectNext(-1);
        Assert.Same(second, host.SelectedTab);
    }

    [AvaloniaFact]
    public async Task Tabs_KeepTheirOwnDocument()
    {
        var host = CreateHost();
        var first = host.AddTab();
        var second = host.AddTab();

        await first.OpenPathAsync(SamplePath);

        Assert.True(first.IsDocumentOpen);
        Assert.False(second.IsDocumentOpen);
        Assert.NotSame(first.Pages, second.Pages);
        Assert.NotSame(first.Settings, null);
        Assert.Same(first.Settings, second.Settings);
    }

    [AvaloniaFact]
    public async Task AddTab_WithAPath_OpensItInTheNewTab()
    {
        var host = CreateHost();

        var tab = host.AddTab(SamplePath);

        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!tab.IsDocumentOpen && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        Assert.True(tab.IsDocumentOpen);
        Assert.Equal(SamplePath, tab.FilePath);
    }

    [AvaloniaFact]
    public async Task OpenCommand_OnTheSameDocument_SelectsItsTabInsteadOfDuplicating()
    {
        var host = CreateHost();
        var first = host.AddTab();
        await first.OpenPathAsync(SamplePath);
        Assert.Equal(2, host.Tabs.Count);

        _dialog.NextFile = SamplePath;
        first.OpenCommand.Execute(null);

        // A second tab would give two document sessions over one file, and saving in either
        // then fails because the other holds the file open.
        await Task.Delay(500);
        Assert.Equal(2, host.Tabs.Count);
        Assert.Same(first, host.SelectedTab);
        Assert.Same(first, Assert.Single(host.Tabs, tab => !tab.IsHomeTab));
    }

    [AvaloniaFact]
    public async Task OpenDocument_WithTheSameFile_SelectsTheExistingTab()
    {
        var host = CreateHost();
        var first = host.AddTab();
        await first.OpenPathAsync(SamplePath);
        var second = host.AddTab();
        Assert.Same(second, host.SelectedTab);

        host.OpenDocument(SamplePath);

        Assert.Equal(3, host.Tabs.Count);
        Assert.Same(first, host.SelectedTab);
    }

    [AvaloniaFact]
    public async Task OpenDocument_WhileTheSameFileIsStillOpening_SelectsThatTab()
    {
        var host = CreateHost();
        var first = host.AddTab();

        // OpeningPath is set before the first await, so the duplicate check also covers the
        // window between creating a tab and its document finishing load.
        var opening = first.OpenPathAsync(SamplePath);
        host.OpenDocument(SamplePath);

        Assert.Equal(2, host.Tabs.Count);
        Assert.Same(first, host.SelectedTab);

        await opening;
        Assert.True(first.IsDocumentOpen);
    }

    [AvaloniaFact]
    public async Task SelectTab_MatchesTheSameFileWrittenDifferently()
    {
        var host = CreateHost();
        var tab = host.AddTab();
        await tab.OpenPathAsync(SamplePath);

        var relative = Path.GetRelativePath(Environment.CurrentDirectory, SamplePath);

        Assert.Same(tab, host.SelectTab(relative));
    }

    [AvaloniaFact]
    public async Task OpenCommand_OnHome_OpensADocumentTabInstead()
    {
        var host = CreateHost();

        _dialog.NextFile = SamplePath;
        host.HomeTab.OpenCommand.Execute(null);

        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (host.Tabs.Count < 2 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        Assert.Equal(2, host.Tabs.Count);
        Assert.False(host.HomeTab.IsDocumentOpen);

        var document = Assert.Single(host.Tabs, tab => !tab.IsHomeTab);
        while (!document.IsDocumentOpen && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        Assert.True(document.IsDocumentOpen);
        Assert.Same(document, host.SelectedTab);
    }
    [AvaloniaFact]
    public async Task SelectTab_FindsAnOpenDocumentAndIgnoresUnknownFiles()
    {
        var host = CreateHost();
        var tab = host.AddTab();
        await tab.OpenPathAsync(SamplePath);

        Assert.Same(tab, host.SelectTab(SamplePath));
        Assert.Null(host.SelectTab(Path.Combine(Path.GetTempPath(), "not-open.pdf")));
    }
    private WindowViewModel CreateHost()
    {
        _services ??= new AppServices(_settingsDirectory);
        var services = _services;

        _host = new WindowViewModel((host, isHome) =>
        {
            var documents = new PdfDocumentService();

            return new MainWindowViewModel(
                services,
                documents,
                new PdfSearchService(documents),
                new PdfTextService(documents),
                _dialog,
                new StubWindowService(),
                host,
                isHome);
        });

        return _host;
    }

    private sealed class StubDialogService : IUserDialogService
    {
        /// <summary>File the picker returns; null simulates the user cancelling.</summary>
        public string? NextFile { get; set; }

        public Task<string?> PickPdfAsync() => Task.FromResult(NextFile);

        public Task<string?> PromptPasswordAsync(string fileName, bool isRetry) =>
            Task.FromResult<string?>(null);

        public Task ShowErrorAsync(string title, string message) => Task.CompletedTask;

        /// <summary>Path the save picker returns; null simulates the reader cancelling.</summary>
        public string? NextSavePath { get; set; }

        public Task<string?> PickSavePathAsync(string suggestedName) => Task.FromResult(NextSavePath);

        public Task<string?> PromptTextAsync(string title, string heading, string fieldLabel, string initialValue) =>
            Task.FromResult<string?>(null);
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
