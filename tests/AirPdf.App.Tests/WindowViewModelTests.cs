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
    public void AddTab_SelectsTheNewTabAndReportsMultipleTabs()
    {
        var host = CreateHost();

        var first = host.AddTab();
        Assert.Single(host.Tabs);
        Assert.Same(first, host.SelectedTab);
        Assert.False(host.HasMultipleTabs);

        var second = host.AddTab();
        Assert.Equal(2, host.Tabs.Count);
        Assert.Same(second, host.SelectedTab);
        Assert.True(host.HasMultipleTabs);
    }

    [AvaloniaFact]
    public async Task CloseTab_OnTheLastTab_ClosesItsDocumentAndKeepsTheTab()
    {
        var host = CreateHost();
        var tab = host.AddTab();
        await tab.OpenPathAsync(SamplePath);
        Assert.True(tab.IsDocumentOpen);

        host.CloseTab(tab);

        Assert.Single(host.Tabs);
        Assert.False(tab.IsDocumentOpen);
    }

    [AvaloniaFact]
    public void CloseTab_WithTwoTabs_RemovesItAndSelectsTheOther()
    {
        var host = CreateHost();
        var first = host.AddTab();
        var second = host.AddTab();

        host.CloseTab(second);

        Assert.Single(host.Tabs);
        Assert.Same(first, host.SelectedTab);
        Assert.False(host.HasMultipleTabs);
    }

    [AvaloniaFact]
    public void SelectNext_WrapsInBothDirections()
    {
        var host = CreateHost();
        var first = host.AddTab();
        var second = host.AddTab();
        var third = host.AddTab();
        Assert.Same(third, host.SelectedTab);

        host.SelectNext(1);
        Assert.Same(first, host.SelectedTab);

        host.SelectNext(-1);
        Assert.Same(third, host.SelectedTab);

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

        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!tab.IsDocumentOpen && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        Assert.True(tab.IsDocumentOpen);
        Assert.Equal(SamplePath, tab.FilePath);
    }

    [AvaloniaFact]
    public async Task OpenCommand_WithADocumentAlreadyOpen_AddsATab()
    {
        var host = CreateHost();
        var first = host.AddTab();
        await first.OpenPathAsync(SamplePath);
        Assert.Single(host.Tabs);

        _dialog.NextFile = SamplePath;
        first.OpenCommand.Execute(null);

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (host.Tabs.Count < 2 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        Assert.Equal(2, host.Tabs.Count);
        Assert.NotSame(first, host.SelectedTab);
    }

    [AvaloniaFact]
    public async Task OpenCommand_OnTheWelcomeScreen_UsesTheCurrentTab()
    {
        var host = CreateHost();
        var first = host.AddTab();

        _dialog.NextFile = SamplePath;
        first.OpenCommand.Execute(null);

        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!first.IsDocumentOpen && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        Assert.Single(host.Tabs);
        Assert.True(first.IsDocumentOpen);
    }
    private WindowViewModel CreateHost()
    {
        _services ??= new AppServices(_settingsDirectory);
        var services = _services;

        _host = new WindowViewModel(() => new MainWindowViewModel(
            services,
            new PdfDocumentService(),
            new PdfSearchService(),
            new PdfTextService(),
            _dialog,
            new StubWindowService(),
            _host));

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
