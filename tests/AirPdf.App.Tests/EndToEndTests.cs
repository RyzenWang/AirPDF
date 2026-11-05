using Avalonia;
using AirPdf.App.Services;
using AirPdf.App.ViewModels;
using AirPdf.App.Views;
using AirPdf.Core.Services;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
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
    private readonly StubDialogService _dialogs = new();
    private readonly StubWindowService _windows = new();

    public EndToEndTests()
    {
        _services = new AppServices(_settingsDirectory);
        _window = new MainWindow(
            _services,
            _windows,
            null,
            restoreWindowPosition: false,
            _dialogs);
        _window.Show();

        _home = Assert.IsType<MainWindowViewModel>(_window.DataContext);
        Assert.True(_home.IsHomeTab);
    }

    public void Dispose()
    {
        _window.Close();

        // Closing releases document sessions asynchronously. Pump until the window is gone so
        // the services are not disposed underneath that release.
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (_window.IsVisible && DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
        }

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
        Assert.True(await document.EnsureTextLayerAsync(1));
        await WaitAsync(() => document.Pages.Count > 0);

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
    public void CtrlN_OpensANewWindow()
    {
        _window.KeyPressQwerty(PhysicalKey.N, RawInputModifiers.Control);

        Assert.Single(_windows.NewWindows);
    }

    [AvaloniaFact]
    public async Task ClosingTheWindow_WithUnsavedMarkup_AsksFirst()
    {
        var document = await OpenSampleAsync();
        await document.AddInkAtAsync(1, [(0.2, 0.5), (0.8, 0.5)]);
        Assert.True(document.HasUnsavedAnnotations);
        _dialogs.NextUnsavedChoice = UnsavedChangesChoice.Discard;

        _window.Close();
        await WaitAsync(() => !_window.IsVisible, 10_000);

        Assert.Equal(1, _dialogs.UnsavedPrompts);
        Assert.False(_window.IsVisible);
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
        await WaitAsync(() => document.Pages[0].Image is not null);

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
        await WaitAsync(() => document.Pages[0].Image is not null);

        _window.KeyPressQwerty(PhysicalKey.A, RawInputModifiers.Control);
        await WaitAsync(() => document.HasSelection);
        _window.KeyPressQwerty(PhysicalKey.H, RawInputModifiers.Control | RawInputModifiers.Shift);
        await WaitAsync(() => document.CanUndoAnnotation);

        var page = document.Pages[0];
        var before = page.Image;

        _window.KeyPressQwerty(PhysicalKey.Z, RawInputModifiers.Control);

        // The flags flip before the async work finishes, so wait for the status line the command
        // writes when it is done; otherwise the fixture can dispose the service mid-flight.
        await WaitAsync(() => !document.CanUndoAnnotation && document.StatusMessage == _services.Localization.T("L.Undone"));

        Assert.False(document.CanUndoAnnotation);

        // Undoing is also a document change, so the page has to be redrawn to show the highlight
        // disappear.
        await WaitAsync(() => !ReferenceEquals(page.Image, before));
        Assert.NotSame(before, page.Image);
    }
    [AvaloniaFact]
    public async Task RedoShortcut_AppliesTheHighlightAgain()
    {
        var document = await OpenSampleAsync();
        await WaitAsync(() => document.Pages.Count > 0);

        _window.KeyPressQwerty(PhysicalKey.A, RawInputModifiers.Control);
        await WaitAsync(() => document.HasSelection);
        _window.KeyPressQwerty(PhysicalKey.H, RawInputModifiers.Control | RawInputModifiers.Shift);
        await WaitAsync(() => document.CanUndoAnnotation);

        _window.KeyPressQwerty(PhysicalKey.Z, RawInputModifiers.Control);
        await WaitAsync(() => document.CanRedoAnnotation && !document.CanUndoAnnotation);
        Assert.False(document.CanUndoAnnotation);

        _window.KeyPressQwerty(PhysicalKey.Y, RawInputModifiers.Control);
        await WaitAsync(() => document.CanUndoAnnotation && document.StatusMessage == _services.Localization.T("L.Redone"));

        Assert.True(document.CanUndoAnnotation);
        Assert.False(document.CanRedoAnnotation);
    }
    [AvaloniaFact]
    public async Task EscapeAfterSelectAll_ClearsTheSelection()
    {
        var document = await OpenSampleAsync();
        await WaitAsync(() => document.Pages.Count > 0);

        _window.KeyPressQwerty(PhysicalKey.A, RawInputModifiers.Control);
        await WaitAsync(() => document.HasSelection);
        Assert.True(document.HasSelection);

        // Until now a select-all could not be undone without selecting something else.
        _window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        await WaitAsync(() => !document.HasSelection);

        Assert.False(document.HasSelection);
        Assert.DoesNotContain(document.Pages, page => page.SelectionRects.Count > 0);
    }

    /// <summary>
    /// Escape is the keyboard way out of a tool. It left drawing mode but not erasing mode, so
    /// the eraser could only be turned off with the button.
    /// </summary>
    [AvaloniaFact]
    public async Task Escape_LeavesDrawingAndErasingModes()
    {
        var document = await OpenSampleAsync();
        await WaitAsync(() => document.Pages.Count > 0);

        document.ToggleInkModeCommand.Execute(null);
        Assert.True(document.IsInkMode);

        _window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        await WaitAsync(() => !document.IsInkMode);
        Assert.False(document.IsInkMode);

        document.ToggleEraserModeCommand.Execute(null);
        Assert.True(document.IsEraserMode);

        _window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        await WaitAsync(() => !document.IsEraserMode);
        Assert.False(document.IsEraserMode);
    }

    [AvaloniaFact]
    public async Task DiagnosticShortcut_WritesAProbeBlockOnDemand()
    {
        // The report of blank pages has to be captured where it happens, so the shortcut has to
        // work in a normal build without any environment variable.
        var log = Path.Combine(Path.GetTempPath(), "airpdf-render-probe.txt");
        if (File.Exists(log))
        {
            File.Delete(log);
        }

        try
        {
            var document = await OpenSampleAsync();
            await WaitAsync(() => document.Pages.Count > 0);

            _window.KeyPressQwerty(PhysicalKey.D, RawInputModifiers.Control | RawInputModifiers.Shift);
            await WaitAsync(() => File.Exists(log));

            Assert.True(File.Exists(log), "the shortcut did not write the diagnostic file");
            var text = File.ReadAllText(log);
            Assert.Contains("manual", text);
            Assert.Contains("realized=", text);
        }
        finally
        {
            if (File.Exists(log))
            {
                File.Delete(log);
            }
        }
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
    /// The reported path end to end: draw, look at the page, erase, undo, save. Rendering the
    /// page is what used to strip the annotation's colour, so the restored stroke came back
    /// black. This goes through the real window and the real document so the render is part of
    /// the test rather than simulated.
    /// </summary>
    [AvaloniaFact]
    public async Task ErasingAndUndoingAStroke_KeepsItsColour()
    {
        var target = Path.Combine(Path.GetTempPath(), $"airpdf-e2e-erase-{Guid.NewGuid():N}.pdf");
        File.Copy(SamplePath, target);

        try
        {
            var document = await OpenAsync(target);
            var page = document.Pages[0];
            await WaitAsync(() => page.Image is not null);

            var blue = document.Palette.Single(colour => colour.Name == "Blue");
            document.SetInkColor(blue);

            var before = page.Image;
            await document.AddInkAtAsync(1, [(0.2, 0.5), (0.8, 0.5)]);

            // Wait for the page to be drawn with the stroke, because that render is what makes
            // PDFium discard the standard colour entries.
            await WaitAsync(() => page.Image is { } image && !ReferenceEquals(image, before));

            document.ToggleEraserModeCommand.Execute(null);
            document.BeginErase();
            Assert.True(await document.EraseAtAsync(1, 0.5, 0.5));
            document.EndErase();

            await document.UndoAnnotationCommand.ExecuteAsync(null);
            await document.SaveDocumentCommand.ExecuteAsync(null);

            using var probe = new PdfDocumentService();
            await probe.OpenAsync(target);
            var ink = Assert.Single(await probe.GetInksAsync(target, 1));
            Assert.Equal(blue.Red, ink.Red);
            Assert.Equal(blue.Green, ink.Green);
            Assert.Equal(blue.Blue, ink.Blue);

            // Let go of the file so the temporary copy can be removed.
            document.CloseDocumentCommand.Execute(null);
        }
        finally
        {
            try
            {
                File.Delete(target);
            }
            catch (IOException)
            {
                // A leftover temporary file must not fail the test.
            }
        }
    }

    /// <summary>
    /// Closing a tab has to leave the window on Home, and a document opened from Home's recent
    /// list has to render. Removing the selected item clears the tab strip's two-way selection
    /// before the close code used to check it, so the window kept the closed tab as its
    /// DataContext: Home appeared because that view model reported no document, but opening the
    /// file from its recent list ran on the disposed view model. Every page stayed blank at the
    /// right reading position, and the next click crashed on the disposed document service.
    /// </summary>
    [AvaloniaFact]
    public async Task ClosingATabThenOpeningItFromRecent_RendersTheDocumentAgain()
    {
        var document = await OpenSampleAsync();
        await WaitAsync(() => document.Pages.Count > 0 && document.Pages[0].Image is not null);

        // Remember a position away from page one, because the report was about reopening at
        // the last reading position rather than at the top.
        document.UpdateCurrentPageFromScroll(2, 0.1);
        document.SaveReadingPositionNow();
        await Task.Delay(300);

        _window.KeyPressQwerty(PhysicalKey.W, RawInputModifiers.Control);
        await WaitAsync(() => Tabs().All(tab => tab.IsHomeTab), 10_000);

        // The window itself must be showing Home, not the disposed document view model.
        var home = Assert.IsType<MainWindowViewModel>(_window.DataContext);
        Assert.True(home.IsHomeTab);
        Assert.Same(Tabs().Single(tab => tab.IsHomeTab), home);

        var recent = home.RecentFiles.FirstOrDefault(
            item => string.Equals(item.FilePath, SamplePath, StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(recent);

        recent!.OpenCommand.Execute(null);

        MainWindowViewModel? reopened = null;
        await WaitAsync(() =>
        {
            reopened = Tabs().FirstOrDefault(tab => !tab.IsHomeTab && tab.IsDocumentOpen);
            return reopened is not null;
        }, 10_000);

        Assert.NotNull(reopened);
        Assert.NotSame(document, reopened);

        // It has to return to the remembered page, and that page has to be rendered.
        await WaitAsync(() => reopened!.CurrentPage == 2, 10_000);
        Assert.Equal(2, reopened.CurrentPage);

        await WaitAsync(() => reopened.Pages.Count > 1 && reopened.Pages[1].Image is not null, 20_000);

        var page = reopened.Pages[1];
        Assert.NotNull(page.Image);
        Assert.False(page.HasRenderError, page.RenderError);
        Assert.True(HasInk(page.Image!), "the reopened page rendered blank");
    }

    /// <summary>
    /// Opening a file that already has a tab selects that tab instead of starting a second
    /// document session over the same file. With two sessions, saving in either failed because
    /// the other still held the file open, and the reader saw "the change could not be applied".
    /// </summary>
    [AvaloniaFact]
    public async Task OpeningTheSameFileFromRecent_SelectsItsTabAndSavingStillWorks()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"airpdf-e2e-same-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var target = Path.Combine(directory, "same.pdf");
        File.Copy(SamplePath, target);

        try
        {
            var document = await OpenAsync(target);
            await WaitAsync(() => document.Pages.Count > 0 && document.Pages[0].Image is not null);

            document.GoHomeCommand.Execute(null);
            await WaitAsync(() => _window.DataContext is MainWindowViewModel { IsHomeTab: true }, 10_000);
            var home = Assert.IsType<MainWindowViewModel>(_window.DataContext);

            var recent = home.RecentFiles.FirstOrDefault(
                item => string.Equals(item.FilePath, target, StringComparison.OrdinalIgnoreCase));
            Assert.NotNull(recent);

            recent!.OpenCommand.Execute(null);
            await Task.Delay(800);

            // The existing tab must be selected, not a second session created.
            Assert.Same(document, _window.DataContext);
            Assert.Same(document, Assert.Single(Tabs(), tab => !tab.IsHomeTab));

            // Saving has to land now that no other session holds the file.
            await document.AddInkAtAsync(1, [(0.2, 0.5), (0.8, 0.5)]);
            await document.SaveDocumentCommand.ExecuteAsync(null);
            Assert.False(document.HasError, document.ErrorMessage);

            using var probe = new PdfDocumentService();
            await probe.OpenAsync(target);
            Assert.Single(await probe.GetInksAsync(target, 1));

            document.CloseDocumentCommand.Execute(null);
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
                // A leftover temporary directory must not fail the test.
            }
        }
    }

    /// <summary>True when a rendered page carries anything other than paper white.</summary>
    private static bool HasInk(Avalonia.Media.Imaging.WriteableBitmap bitmap)
    {
        using var buffer = bitmap.Lock();
        var pixels = new byte[buffer.RowBytes * buffer.Size.Height];
        System.Runtime.InteropServices.Marshal.Copy(buffer.Address, pixels, 0, pixels.Length);

        for (var y = 4; y < buffer.Size.Height; y += 12)
        {
            for (var x = 4; x < buffer.Size.Width; x += 12)
            {
                var offset = (y * buffer.RowBytes) + (x * 4);
                if (pixels[offset] < 245 || pixels[offset + 1] < 245 || pixels[offset + 2] < 245)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Opens the sample from the home tab, which is how a reader gets a document: the open lands
    /// in a new tab and that tab becomes active.
    /// </summary>
    private Task<MainWindowViewModel> OpenSampleAsync() => OpenAsync(SamplePath);

    private async Task<MainWindowViewModel> OpenAsync(string path)
    {
        await _home.OpenPathAsync(path);

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

    private sealed class StubDialogService : IUserDialogService
    {
        public Task<string?> PickPdfAsync() => Task.FromResult<string?>(null);

        public Task<string?> PromptPasswordAsync(string fileName, bool isRetry) =>
            Task.FromResult<string?>(null);

        public Task ShowErrorAsync(string title, string message) => Task.CompletedTask;

        public Task<string?> PickSavePathAsync(string suggestedName) => Task.FromResult<string?>(null);

        public Task<string?> PromptTextAsync(string title, string heading, string fieldLabel, string initialValue) =>
            Task.FromResult<string?>(null);

        public UnsavedChangesChoice NextUnsavedChoice { get; set; } = UnsavedChangesChoice.Discard;

        public int UnsavedPrompts { get; private set; }

        public Task<UnsavedChangesChoice> ConfirmUnsavedChangesAsync(string fileName)
        {
            UnsavedPrompts++;
            return Task.FromResult(NextUnsavedChoice);
        }
    }

    private sealed class StubWindowService : IWindowService
    {
        public List<string?> NewWindows { get; } = [];

        public void OpenNewWindow(string? filePath = null) => NewWindows.Add(filePath);

        public void OpenInNewWindow(string filePath) => NewWindows.Add(filePath);
    }
}
