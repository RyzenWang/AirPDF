namespace AirPdf.App.Services;

/// <summary>
/// Lets a document view model ask its window to open another document in a tab, without
/// knowing about the window or the tab collection.
/// </summary>
public interface ITabService
{
    void OpenInNewTab(string filePath);
}
