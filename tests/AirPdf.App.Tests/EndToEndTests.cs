using AirPdf.App.Services;
using AirPdf.App.ViewModels;
using AirPdf.App.Views;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Xunit;

namespace AirPdf.App.Tests;

/// <summary>
/// End-to-end coverage: a real window, real key events through Avalonia's input pipeline, and a
/// real document. The unit tests drive view models directly and cannot catch anything that
/// depends on routing, focus, bindings, or the visual tree, which is where the last several
/// defects lived.
/// </summary>
public sealed class EndToEndTests : IDisposable
{
    private static readonly string SamplePath =
        Path.Combine(AppContext.BaseDirectory, "Assets", "sample.pdf");

    private readonly string _settingsDirectory =
        Path.Combine(Path.GetTempPath(), $"airpdf-e2e-{Guid.NewGuid():N}");

    private readonly AppServices _services;
    private readonly MainWindow _window;
    private readonly MainWindowViewModel _home;

    public EndToEndTests()
    {
        _services = new AppServices(_settingsDirectory);
        _window = new MainWindow(_services, new StubWindowService(), null, restoreWindowPosition: false);
        _window.Show();

        _home = Assert.IsType<MainWindowViewModel>(_window.DataContext);
        Assert.True(_home.IsHomeTab);
    }

    public void Dispose()
    {
        _window.Close();
        _services.Dispose();

        if (Directory.Exists(_settingsDirectory))
        {
            Directory.Delete(_settingsDirectory, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task ZoomShortcuts_ResizeThePageAndReRenderIt()
    {
        var document = await OpenSampleAsync();
        await WaitAsync(() => document.Pages.Count > 0 && document.Pages[0].Image is not null);

        var fittedWidth = document.Pages[0].DisplayWidth;
        var fittedPixels = document.Pages[0].Image!.PixelSize.Width;

        // Ctrl+= : one step in.
        _window.KeyPressQwerty(PhysicalKey.Equal, RawInputModifiers.Control);
        await WaitAsync(() => document.Pages[0].DisplayWidth > fittedWidth);
        Assert.True(document.Pages[0].DisplayWidth > fittedWidth);

        // The render has to follow the layout, not just the binding: the bitmap width is what
        // the reader actually looks at.
        var zoomedWidth = document.Pages[0].DisplayWidth;
        await WaitAsync(() => document.Pages[0].Image is { } image && image.PixelSize.Width != fittedPixels);
        Assert.NotEqual(fittedPixels, document.Pages[0].Image!.PixelSize.Width);

        // Ctrl+- twice: back below where it started.
        _window.KeyPressQwerty(PhysicalKey.Minus, RawInputModifiers.Control);
        _window.KeyPressQwerty(PhysicalKey.Minus, RawInputModifiers.Control);
        await WaitAsync(() => document.Pages[0].DisplayWidth < zoomedWidth);
        Assert.True(document.Pages[0].DisplayWidth < zoomedWidth);

        // Ctrl+1 : actual size, which is a known value rather than a relative one.
        _window.KeyPressQwerty(PhysicalKey.Digit1, RawInputModifiers.Control);
        await WaitAsync(() => Math.Abs(document.Zoom - 1) < 0.001);
        Assert.Equal(1d, document.Zoom, 3);
        Assert.Equal(
            document.Pages[0].WidthPoints * PageViewModel.PixelsPerPointAt100,
            document.Pages[0].DisplayWidth,
            1);
    }

    [AvaloniaFact]
    public async Task SearchShortcut_TypesIntoThePanelAndFindsTextOnThePage()
    {
        var document = await OpenSampleAsync();
        await WaitAsync(() => document.Pages[0].HasTextLayer);

        var word = document.Pages[0].Words
            .Select(entry => entry.Text.Trim())
            .First(text => text.Length >= 4 && text.All(char.IsLetter));

        // Ctrl+F opens the panel and focuses the box; the text has to land there for the query
        // to be set at all.
        _window.KeyPressQwerty(PhysicalKey.F, RawInputModifiers.Control);
        await WaitAsync(() => document.IsSearchOpen);

        _window.KeyTextInput(word);
        await WaitAsync(() => string.Equals(document.SearchQuery, word, StringComparison.Ordinal));
        Assert.Equal(word, document.SearchQuery);

        // Enter in the search box runs the search.
        _window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        await WaitAsync(() => document.HasSearched && !document.IsSearching);

        Assert.True(document.HasSearchResults, $"no results for '{word}'");
        Assert.NotEmpty(document.SearchResults);

        // Highlights are pushed into the page view models, which is what the overlay draws.
        await WaitAsync(() => document.Pages.Any(page => page.HighlightRects.Count > 0));
        Assert.Contains(document.Pages, page => page.HighlightRects.Count > 0);

        // F3 steps to the next hit and moves the current page.
        var before = document.CurrentPage;
        _window.KeyPressQwerty(PhysicalKey.F3, RawInputModifiers.None);
        await WaitAsync(() => document.CurrentPage != before || document.SearchResults.Count == 1);
    }

    [AvaloniaFact]
    public async Task LanguageShortcut_ChangesWhatTheWindowShowsAndRemembersIt()
    {
        // The sidebar only exists once a document is open, and that is where the tab headers
        // come from, so the switch is checked against strings the reader can actually see.
        await OpenSampleAsync();
        await WaitAsync(() => _window.GetVisualDescendants().OfType<TextBlock>()
            .Any(block => block.Text == "Bookmarks"));

        Assert.Equal("Bookmarks", _services.Localization.T("L.Bookmarks"));
        Assert.Contains(
            _window.GetVisualDescendants().OfType<TextBlock>(),
            block => block.Text == "Bookmarks");

        _window.KeyPressQwerty(PhysicalKey.L, RawInputModifiers.Control | RawInputModifiers.Shift);
        await WaitAsync(() => _services.Localization.IsChinese);
        Assert.True(_services.Localization.IsChinese);

        // The strings are resources, so the visual tree has to have been refreshed, not just
        // the view model properties.
        await WaitAsync(() => _window.GetVisualDescendants().OfType<TextBlock>()
            .Any(block => block.Text == "书签"));
        Assert.Contains(
            _window.GetVisualDescendants().OfType<TextBlock>(),
            block => block.Text == "书签");

        // The choice is written to the settings file, so a new window starts in it.
        Assert.Equal(LocalizationService.ChineseSimplified, _services.Settings.Language);
        Assert.Contains(
            "\"language\": \"zh-CN\"",
            File.ReadAllText(Path.Combine(_settingsDirectory, "settings.json")));
    }

    [AvaloniaFact]
    public async Task HighlightShortcut_RendersTheHighlightWithoutSavingACopy()
    {
        var document = await OpenSampleAsync();
        await WaitAsync(() => document.Pages[0].Image is not null && document.Pages[0].HasTextLayer);

        _window.KeyPressQwerty(PhysicalKey.A, RawInputModifiers.Control);
        await WaitAsync(() => document.HasSelection);

        var page = document.Pages[0];
        var before = page.Image;

        _window.KeyPressQwerty(PhysicalKey.H, RawInputModifiers.Control | RawInputModifiers.Shift);
        await WaitAsync(() => document.HasUnsavedAnnotations);

        // The page already had a bitmap at this width, and a render that skips that case leaves
        // the highlight invisible until the reader saves a copy and reopens it - which is exactly
        // what was reported.
        await WaitAsync(() => !ReferenceEquals(page.Image, before));
        Assert.NotSame(before, page.Image);
    }
    [AvaloniaFact]
    public async Task UndoShortcut_TakesBackTheHighlightAndReRenders()
    {
        var document = await OpenSampleAsync();
        await WaitAsync(() => document.Pages[0].Image is not null && document.Pages[0].HasTextLayer);

        _window.KeyPressQwerty(PhysicalKey.A, RawInputModifiers.Control);
        await WaitAsync(() => document.HasSelection);
        _window.KeyPressQwerty(PhysicalKey.H, RawInputModifiers.Control | RawInputModifiers.Shift);
        await WaitAsync(() => document.CanUndoAnnotation);

        var page = document.Pages[0];
        var before = page.Image;

        _window.KeyPressQwerty(PhysicalKey.Z, RawInputModifiers.Control);
        await WaitAsync(() => !document.CanUndoAnnotation);

        Assert.False(document.CanUndoAnnotation);

        // Undoing is also a document change, so the page has to be redrawn to show the highlight
        // disappear.
        await WaitAsync(() => !ReferenceEquals(page.Image, before));
        Assert.NotSame(before, page.Image);
    }
    [AvaloniaFact]
    public async Task UpdateCheck_OnABuildThatIsNotInstalled_ShowsNoBanner()
    {
        // The test build is a folder build, so Velopack reports it as not installed and the
        // reader must not advertise an update it cannot apply.
        Assert.False(_services.Updates.IsInstalled);

        var document = await OpenSampleAsync();

        Assert.Null(await _services.Updates.CheckForUpdatesAsync());

        // No phantom update affordance: no banner, no button, nothing to apply.
        Assert.False(document.HasUpdate);
        Assert.False(document.IsUpdateReady);
        Assert.False(document.CanApplyUpdate);

        // The command stays executable because it falls back to a check, so what matters is
        // that invoking it on a build that cannot install updates starts nothing.
        await document.ApplyUpdateCommand.ExecuteAsync(null);
        Assert.False(document.IsUpdating);
        Assert.False(document.HasUpdate);
        Assert.False(document.IsUpdateReady);
    }

    /// <summary>
    /// Opens the sample from the home tab, which is how a reader gets a document: the open lands
    /// in a new tab and that tab becomes active.
    /// </summary>
    private async Task<MainWindowViewModel> OpenSampleAsync()
    {
        await _home.OpenPathAsync(SamplePath);

        MainWindowViewModel? document = null;
        await WaitAsync(() =>
        {
            document = Tabs().FirstOrDefault(tab => !tab.IsHomeTab && tab.IsDocumentOpen && tab.Pages.Count > 0);
            return document is not null;
        });

        if (document is null)
        {
            // A bare timeout says nothing; carry the state that explains it.
            throw new InvalidOperationException(
                $"The sample never opened. busy={_home.IsBusy} hasError={_home.HasError} " +
                $"error='{_home.ErrorMessage}' tabs={Tabs().Count} open={_home.IsDocumentOpen}");
        }

        Assert.Same(document, _window.DataContext);
        return document;
    }

    private IReadOnlyList<MainWindowViewModel> Tabs()
    {
        var list = _window.FindControl<ListBox>("TabList");
        Assert.NotNull(list);

        return list!.ItemsSource is IEnumerable<MainWindowViewModel> tabs ? tabs.ToList() : [];
    }

    private static async Task WaitAsync(Func<bool> condition, int timeoutMs = 20_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(50);
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
