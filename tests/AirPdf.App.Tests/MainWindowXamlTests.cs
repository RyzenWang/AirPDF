using AirPdf.App.Controls;
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
public sealed class MainWindowXamlTests
{
    [AvaloniaFact]
    public void MainWindow_BuildsItsXaml()
    {
        var window = new MainWindow();

        // The window is deliberately not closed: Closing writes the user's real
        // settings file, which a test must not touch.
        Assert.NotNull(window.Content);
        Assert.NotNull(window.DataContext);
    }

    /// <summary>
    /// The context menu lives in a popup, outside the window's compiled-binding scope,
    /// so it is worth asserting that its items really reach the view model commands.
    /// </summary>
    [AvaloniaFact]
    public void PageCanvasContextMenu_ReachesTheViewModelCommands()
    {
        var window = new MainWindow();
        var viewModel = Assert.IsType<MainWindowViewModel>(window.DataContext);
        var pages = window.FindControl<PdfPageList>("PagesList");
        Assert.NotNull(pages);
        Assert.NotNull(pages!.ContextMenu);

        pages.ContextMenu!.Open(pages);

        var commands = pages.ContextMenu.Items
            .OfType<MenuItem>()
            .Select(item => item.Command)
            .ToList();

        Assert.NotEmpty(commands);
        Assert.All(commands, command => Assert.NotNull(command));
        Assert.Contains(commands, command => ReferenceEquals(command, viewModel.CopySelectionCommand));
        Assert.Contains(commands, command => ReferenceEquals(command, viewModel.OpenContainingFolderCommand));
    }
}

