using System.Text;

namespace AirPdf.App;

/// <summary>
/// Records unhandled exceptions to a file next to the settings, because a crash while the
/// window is open otherwise leaves nothing behind: the process just disappears.
/// </summary>
/// <remarks>
/// This cannot catch a native access violation on the render thread; those are recorded by
/// Windows instead. It does catch everything routed through the runtime, which is where a
/// bad index or a disposed object would surface.
/// </remarks>
internal static class CrashLog
{
    private const long MaxBytes = 512 * 1024;

    private static readonly object Gate = new();
    private static string? _path;

    public static void Install()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Write("AppDomain.UnhandledException", e.ExceptionObject as Exception);

        TaskScheduler.UnobservedTaskException += (_, e) =>
            Write("TaskScheduler.UnobservedTaskException", e.Exception);

        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            Write("ProcessExit", null);
    }

    public static void Write(string source, Exception? exception)
    {
        try
        {
            var builder = new StringBuilder()
                .Append(DateTimeOffset.Now.ToString("O"))
                .Append("  ")
                .Append(source)
                .AppendLine();

            if (exception is null)
            {
                builder.AppendLine("  (no exception object)");
            }
            else
            {
                for (var current = exception; current is not null; current = current.InnerException)
                {
                    builder
                        .Append("  ")
                        .Append(current.GetType().FullName)
                        .Append(": ")
                        .AppendLine(current.Message)
                        .AppendLine(current.StackTrace);
                }
            }

            builder.AppendLine();

            lock (Gate)
            {
                var path = GetPath();
                Trim(path);
                File.AppendAllText(path, builder.ToString());
            }
        }
        catch
        {
            // Logging must never be the reason a process dies.
        }
    }

    private static string GetPath()
    {
        if (_path is not null)
        {
            return _path;
        }

        var root = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrWhiteSpace(root))
        {
            root = Path.GetTempPath();
        }

        var directory = Path.Combine(root, "AirPdf");
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "crash.log");
        return _path;
    }

    private static void Trim(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Exists && info.Length > MaxBytes)
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best effort.
        }
    }
}
