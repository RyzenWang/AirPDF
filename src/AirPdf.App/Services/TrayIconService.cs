using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;

namespace AirPdf.App.Services;

/// <summary>
/// Owns the process-wide notification-area icon. The icon is independent of any one
/// window: hiding every window keeps it alive so the app can be brought back without
/// rebuilding the open documents.
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private static readonly Uri IconUri = new("avares://AirPdf/Assets/airpdf.ico");

    private readonly Application _application;
    private readonly IClassicDesktopStyleApplicationLifetime _desktop;
    private readonly WindowService _windows;
    private readonly LocalizationService _localization;
    private readonly TrayIcon _trayIcon;

    private TrayIconService(
        Application application,
        IClassicDesktopStyleApplicationLifetime desktop,
        WindowService windows,
        LocalizationService localization)
    {
        _application = application;
        _desktop = desktop;
        _windows = windows;
        _localization = localization;

        _trayIcon = new TrayIcon
        {
            Icon = new WindowIcon(AssetLoader.Open(IconUri)),
            ToolTipText = Tooltip,
            Menu = BuildMenu(),
            IsVisible = true
        };
        _trayIcon.Clicked += OnTrayIconClicked;

        TrayIcon.SetIcons(application, new TrayIcons { _trayIcon });
        _localization.LanguageChanged += OnLanguageChanged;
    }

    /// <summary>
    /// The tray is optional: a Linux desktop without StatusNotifier/AppIndicator support
    /// must not prevent the reader from starting.
    /// </summary>
    public static TrayIconService? TryCreate(
        Application application,
        IClassicDesktopStyleApplicationLifetime desktop,
        WindowService windows,
        LocalizationService localization)
    {
        try
        {
            return new TrayIconService(application, desktop, windows, localization);
        }
        catch (Exception exception)
        {
            CrashLog.Write("TrayIcon.Initialize", exception);
            return null;
        }
    }

    private string Tooltip => _localization.T("L.TrayTooltip");

    private NativeMenu BuildMenu()
    {
        var menu = new NativeMenu();

        var show = new NativeMenuItem(_localization.T("L.TrayShow"));
        show.Click += (_, _) => ShowLastWindow();
        menu.Items.Add(show);

        var hide = new NativeMenuItem(_localization.T("L.TrayHide"));
        hide.Click += (_, _) => HideAllWindows();
        menu.Items.Add(hide);

        var newWindow = new NativeMenuItem(_localization.T("L.TrayNewWindow"));
        newWindow.Click += (_, _) => _windows.OpenNewWindow();
        menu.Items.Add(newWindow);

        menu.Items.Add(new NativeMenuItemSeparator());

        var exit = new NativeMenuItem(_localization.T("L.TrayExit"));
        exit.Click += (_, _) => ExitApplication();
        menu.Items.Add(exit);

        return menu;
    }

    private void OnTrayIconClicked(object? sender, EventArgs e) => ShowLastWindow();

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        _trayIcon.Menu = BuildMenu();
        _trayIcon.ToolTipText = Tooltip;
    }

    private void ShowLastWindow()
    {
        var window = _windows.Windows.LastOrDefault(candidate => candidate.IsVisible)
                     ?? _windows.Windows.LastOrDefault();

        if (window is null)
        {
            window = _windows.CreateFirstWindow(null);
            _desktop.MainWindow = window;
        }

        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        if (!window.IsVisible)
        {
            window.Show();
        }

        window.Activate();
    }

    private void HideAllWindows()
    {
        foreach (var window in _windows.Windows.Where(candidate => candidate.IsVisible).ToList())
        {
            window.Hide();
        }
    }

    private void ExitApplication()
    {
        // Closing the last window lets the default OnLastWindowClose lifetime shut the
        // process down. Each window's normal unsaved-changes prompt still runs first.
        var windows = _windows.Windows.ToList();
        if (windows.Count == 0)
        {
            _desktop.Shutdown();
            return;
        }

        foreach (var window in windows)
        {
            window.Close();
        }
    }

    public void Dispose()
    {
        _localization.LanguageChanged -= OnLanguageChanged;
        _trayIcon.Clicked -= OnTrayIconClicked;
        _trayIcon.IsVisible = false;
        _trayIcon.Dispose();
        TrayIcon.SetIcons(_application, new TrayIcons());
    }
}
