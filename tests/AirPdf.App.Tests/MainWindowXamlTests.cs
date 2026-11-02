using AirPdf.App.Controls;
using AirPdf.App.Services;
using AirPdf.App.ViewModels;
using AirPdf.App.Views;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Xunit;

namespace AirPdf.App.Tests;

/// <summary>
/// Builds the real window so XAML regressions (bad property, missing resource, broken
/// template) fail the test suite instead of failing at startup.
/// </summary>
public sealed class MainWindowXamlTests : IDisposable
{
    private readonly string _settingsDirectory =
        Path.Combine(Path.GetTempPath(), $"airpdf-xaml-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_settingsDirectory))
        {
            Directory.Delete(_settingsDirectory, recursive: true);
        }
    }

    [AvaloniaFact]
    public void MainWindow_BuildsItsXaml()
    {
        var window = CreateWindow();

        // Windows are deliberately not closed: Closing writes window state to the
        // settings file, and these tests use a throwaway directory.
        Assert.NotNull(window.Content);
        Assert.NotNull(window.DataContext);
    }

    /// <summary>
    /// Two windows over one <see cref="AppServices"/> is what the app does. They must build
    /// independently and share the settings instance.
    /// </summary>
    [AvaloniaFact]
    public void TwoWindows_ShareServices_ButNotViewModels()
    {
        var services = new AppServices(_settingsDirectory);
        var windows = new StubWindowService();

        var first = new MainWindow(services, windows, null, restoreWindowPosition: true);
        var second = new MainWindow(services, windows, null, restoreWindowPosition: false);

        var firstModel = Assert.IsType<MainWindowViewModel>(first.DataContext);
        var secondModel = Assert.IsType<MainWindowViewModel>(second.DataContext);

        Assert.NotSame(firstModel, secondModel);
        Assert.Same(firstModel.Settings, secondModel.Settings);
    }

    /// <summary>
    /// The context menu lives in a popup, outside the window's compiled-binding scope,
    /// so it is worth asserting that its items really reach the view model commands.
    /// </summary>
    [AvaloniaFact]
    public void PageCanvasContextMenu_ReachesTheViewModelCommands()
    {
        var window = CreateWindow();
        var viewModel = Assert.IsType<MainWindowViewModel>(window.DataContext);
        var pages = window.FindControl<PdfPageList>("PagesList");
        Assert.NotNull(pages);
        Assert.NotNull(pages!.ContextMenu);

        pages.ContextMenu!.Open(pages);

        var items = pages.ContextMenu.Items.OfType<MenuItem>().ToList();
        var commands = items
            .Select(item => item.Command)
            .Where(command => command is not null)
            .ToList();

        Assert.NotEmpty(commands);
        Assert.Contains(commands, command => ReferenceEquals(command, viewModel.CopySelectionCommand));
        Assert.Contains(commands, command => ReferenceEquals(command, viewModel.OpenContainingFolderCommand));
        Assert.Contains(commands, command => ReferenceEquals(command, viewModel.OpenInNewWindowCommand));

        // "Open in new tab" is a Click handler rather than a command: the tab host lives on
        // the window, not on the document view model the menu binds to.
        Assert.Contains(items, item => item.Command is null);
    }

    /// <summary>
    /// The tab strip binds to the tab host rather than to the document, so it declares its
    /// own data type instead of opting out of compiled bindings. This asserts those bindings
    /// resolve at runtime, since a wrong data type fails silently rather than at build time.
    /// </summary>
    [AvaloniaFact]
    public void TabStrip_BindsToTheTabHost()
    {
        var window = CreateWindow();
        var list = window.FindControl<ListBox>("TabList");

        Assert.NotNull(list);
        var tabs = Assert.IsAssignableFrom<IEnumerable<MainWindowViewModel>>(list!.ItemsSource);
        Assert.Single(tabs);
        Assert.Same(tabs.First(), list.SelectedItem);
    }
    /// <summary>
    /// The high-contrast palette is compiled into a class rather than loaded by resource URI,
    /// because the URI form is annotated as requiring unreferenced code and a trimmed build
    /// cannot guarantee it. Constructing it has to yield the full brush set: a missing key
    /// falls through to the light/dark palette and produces unreadable combinations.
    /// </summary>
    [AvaloniaFact]
    public void HighContrastPalette_IsCompiledWithEveryBrush()
    {
        var palette = new AirPdf.App.Styles.HighContrastPalette();

        string[] required =
        [
            "AppBackgroundBrush", "ToolbarBackgroundBrush", "StatusBackgroundBrush",
            "SidebarBackgroundBrush", "SurfaceBrush", "SurfaceAltBrush", "SurfaceHoverBrush",
            "SurfacePressedBrush", "PagePlaceholderBrush", "BorderBrush", "BorderStrongBrush",
            "TextPrimaryBrush", "TextSecondaryBrush", "TextTertiaryBrush", "InverseTextBrush",
            "AccentBrush", "AccentHoverBrush", "AccentSoftBrush", "AccentTextBrush",
            "TealBrush", "TealSoftBrush", "ShadowBrush", "PageShadowBrush", "OverlayScrimBrush",
            "SearchHighlightBrush", "SearchActiveBrush", "DangerBrush", "DangerSoftBrush",
            "SystemAccentColor", "SystemAccentColorBrush", "TextFillColorPrimaryBrush",
            "TextFillColorSecondaryBrush", "TextFillColorTertiaryBrush",
            "TextControlBackground", "TextControlBackgroundPointerOver",
            "TextControlBackgroundFocused", "TextControlBorderBrush",
            "TextControlBorderBrushPointerOver", "TextControlBorderBrushFocused",
            "TextControlForeground"
        ];

        foreach (var key in required)
        {
            Assert.True(palette.TryGetResource(key, null, out var value), $"missing brush: {key}");
            Assert.NotNull(value);
        }

        Assert.Equal(required.Length, palette.Count);
    }

    /// <summary>
    /// Selecting high contrast merges the palette; leaving it removes the same instance.
    /// </summary>
    [AvaloniaFact]
    public void HighContrastMode_MergesAndRemovesTheCompiledPalette()
    {
        var application = Application.Current!;
        var services = new AppServices(_settingsDirectory);

        services.Theme.Set(ThemeService.HighContrastMode, persist: false);

        var merged = application.Resources.MergedDictionaries;
        Assert.Contains(merged, dictionary => dictionary is AirPdf.App.Styles.HighContrastPalette);

        services.Theme.Set(ThemeService.DarkMode, persist: false);

        Assert.DoesNotContain(merged, dictionary => dictionary is AirPdf.App.Styles.HighContrastPalette);
    }
    private MainWindow CreateWindow()
    {
        var services = new AppServices(_settingsDirectory);
        return new MainWindow(services, new StubWindowService(), null, restoreWindowPosition: true);
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
