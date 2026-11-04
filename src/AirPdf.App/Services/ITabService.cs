using AirPdf.App.ViewModels;

namespace AirPdf.App.Services;

/// <summary>
/// Lets a document view model ask its window to open another document in a tab, without
/// knowing about the window or the tab collection.
/// </summary>
public interface ITabService
{
    /// <summary>
    /// Shows a document: selects the tab that already has it open, or opens a new one. The same
    /// file is never open in two tabs of one window, which would give two document sessions
    /// over one file and make saving fail on the file lock.
    /// </summary>
    void OpenDocument(string filePath);
    /// <summary>Selects the window's home tab; open documents stay open.</summary>
    void ShowHome();

    /// <summary>
    /// Selects the tab already showing this file, or returns null when no tab has it.
    /// </summary>
    MainWindowViewModel? SelectTab(string filePath);
}
