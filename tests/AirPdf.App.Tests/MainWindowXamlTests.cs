using AirPdf.App.Controls;
using AirPdf.App.Services;
using AirPdf.App.ViewModels;
using AirPdf.App.Views;
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
