namespace AirPdf.App.Services;

/// <summary>
/// Opens windows. Implemented by the application shell so the view model can ask for a new
/// window without knowing about <c>MainWindow</c> or window lifetimes.
/// </summary>
public interface IWindowService
{
    /// <summary>Opens another window, optionally with a document already loaded.</summary>
    void OpenNewWindow(string? filePath = null);

    /// <summary>Opens <paramref name="filePath"/> in a new window.</summary>
    void OpenInNewWindow(string filePath);
}
