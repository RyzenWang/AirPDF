using AirPdf.Core.Services;

namespace AirPdf.App.Services;

/// <summary>
/// State that is shared by every window in the process: one settings file, one language,
/// one theme, one update channel. Windows own their document and view models, not these.
/// </summary>
public sealed class AppServices : IDisposable
{
    public AppServices(string? settingsDirectory = null)
    {
        SettingsService = new SettingsService(settingsDirectory);
        Settings = SettingsService.Load();
        Localization = new LocalizationService();
        Localization.Apply(Settings.Language);
        Theme = new ThemeService(SettingsService, Settings);
        Updates = new UpdateService();
    }

    public SettingsService SettingsService { get; }

    public AppSettings Settings { get; }

    public LocalizationService Localization { get; }

    public ThemeService Theme { get; }

    public UpdateService Updates { get; }

    /// <summary>
    /// Raised after a window adds, removes, or clears recent files, so the other windows can
    /// refresh their shelf. The list itself is shared, but each window builds its own
    /// view models from it.
    /// </summary>
    public event EventHandler? RecentFilesChanged;

    public void NotifyRecentFilesChanged() => RecentFilesChanged?.Invoke(this, EventArgs.Empty);

    public void Dispose()
    {
        Theme.Dispose();
        Localization.Dispose();
    }
}
