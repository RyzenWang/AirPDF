using AirPdf.App.ViewModels;

namespace AirPdf.App.Services;

/// <summary>
/// Lets a document view model ask its window to open another document in a tab, without
/// knowing about the window or the tab collection.
/// </summary>
public interface ITabService
{
    void OpenInNewTab(string filePath);
    /// <summary>Selects the window's home tab; open documents stay open.</summary>
    void ShowHome();

    /// <summary>
    /// Selects the tab already showing this file, or returns null when no tab has it.
    /// </summary>
    MainWindowViewModel? SelectTab(string filePath);
}
