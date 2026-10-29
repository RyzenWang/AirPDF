using AirPdf.App.Views;
using Avalonia;
using Avalonia.Controls;

namespace AirPdf.App.Services;

/// <summary>
/// Owns the open windows. Every window shares one <see cref="AppServices"/> instance, so a
/// theme or language change in one window is seen by all of them and the settings file is
/// written by a single store instead of last-writer-wins.
/// </summary>
public sealed class WindowService : IWindowService
{
    private const int CascadeOffset = 32;

    private readonly AppServices _services;
    private readonly List<MainWindow> _windows = [];

    public WindowService(AppServices services) => _services = services;

    public IReadOnlyList<MainWindow> Windows => _windows;

    /// <summary>Creates the first window; it restores the remembered position and size.</summary>
    public MainWindow CreateFirstWindow(string? initialFile)
    {
        var window = Create(initialFile, restorePosition: true);
        window.Show();
        return window;
    }

    public void OpenNewWindow(string? filePath = null)
    {
        var window = Create(filePath, restorePosition: false);
        window.Show();
    }

    public void OpenInNewWindow(string filePath) => OpenNewWindow(filePath);

    private MainWindow Create(string? filePath, bool restorePosition)
    {
        // A new window is offset from the window it was opened from; the first one restores
        // the remembered position instead.
        var cascadeFrom = restorePosition ? null : _windows.LastOrDefault();

        var window = new MainWindow(_services, this, filePath, restorePosition);
        window.Closed += (_, _) => _windows.Remove(window);
        _windows.Add(window);

        if (cascadeFrom is not null)
        {
            Cascade(window, cascadeFrom);
        }

        return window;
    }

    /// <summary>
    /// Offsets a new window from the one it was opened from, keeping it on a real screen.
    /// A window that has not been shown may not be positionable yet, so failures are
    /// ignored and the platform picks the spot.
    /// </summary>
    private static void Cascade(MainWindow window, MainWindow cascadeFrom)
    {
        try
        {
            var position = new PixelPoint(
                cascadeFrom.Position.X + CascadeOffset,
                cascadeFrom.Position.Y + CascadeOffset);

            if (window.Screens is not { } screens)
            {
                return;
            }

            var reachable = false;
            foreach (var screen in screens.All)
            {
                var area = screen.WorkingArea;
                if (position.X >= area.X - 40 &&
                    position.X + 160 < area.X + area.Width &&
                    position.Y >= area.Y - 20 &&
                    position.Y + 80 < area.Y + area.Height)
                {
                    reachable = true;
                    break;
                }
            }

            if (!reachable)
            {
                return;
            }

            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Position = position;
        }
        catch
        {
            // Positioning is cosmetic; never fail opening a window over it.
        }
    }
}
