using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace AirPdf.App.Services;

/// <summary>
/// Opens the platform file manager with the document selected. Explorer often creates its
/// window behind the foreground reader because of Windows' foreground lock, so on Windows it
/// also finds the resulting Cabinet window and brings it forward.
/// </summary>
internal static class ExplorerReveal
{
    private const int SwRestore = 9;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpShowWindow = 0x0040;

    public static void ShowInFolder(string filePath, string directory)
    {
        if (OperatingSystem.IsWindows())
        {
            // Let Explorer become foreground when the shell creates its window.
            AllowSetForegroundWindow(-1);

            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{filePath}\"")
            {
                UseShellExecute = true
            });

            var folderName = new DirectoryInfo(directory).Name;
            _ = BringExplorerToFrontAsync(folderName);
        }
        else if (OperatingSystem.IsMacOS())
        {
            Process.Start(new ProcessStartInfo("open", $"-R \"{filePath}\"")
            {
                UseShellExecute = true
            });
        }
        else
        {
            Process.Start(new ProcessStartInfo("xdg-open", $"\"{directory}\"")
            {
                UseShellExecute = true
            });
        }
    }

    private static async Task BringExplorerToFrontAsync(string folderName)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            await Task.Delay(100).ConfigureAwait(false);

            var window = FindExplorerWindow(folderName);
            if (window != IntPtr.Zero)
            {
                Activate(window);
                return;
            }
        }

        // A drive root or a localized folder name may not equal the window title. Focus the
        // newest Explorer window as a fallback rather than leaving the shell behind AirPDF.
        var fallback = FindExplorerWindow(null);
        if (fallback != IntPtr.Zero)
        {
            Activate(fallback);
        }
    }

    private static IntPtr FindExplorerWindow(string? folderName)
    {
        var found = IntPtr.Zero;
        var target = folderName?.Trim();

        EnumWindows((window, _) =>
        {
            if (!IsWindowVisible(window))
            {
                return true;
            }

            var className = new StringBuilder(64);
            if (GetClassName(window, className, className.Capacity) == 0)
            {
                return true;
            }

            if (className.ToString() is not ("CabinetWClass" or "ExploreWClass"))
            {
                return true;
            }

            if (!string.IsNullOrEmpty(target))
            {
                var title = new StringBuilder(512);
                if (GetWindowText(window, title, title.Capacity) == 0 ||
                    !title.ToString().Contains(target, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            found = window;
            return false;
        }, IntPtr.Zero);

        return found;
    }

    private static void Activate(IntPtr window)
    {
        ShowWindow(window, SwRestore);
        SetForegroundWindow(window);
        BringWindowToTop(window);
        SetWindowPos(
            window,
            IntPtr.Zero,
            0,
            0,
            0,
            0,
            SwpNoMove | SwpNoSize | SwpShowWindow);
    }

    private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, StringBuilder className, int maxLength);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr window, StringBuilder text, int maxLength);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr window, int command);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(
        IntPtr window,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);
}
