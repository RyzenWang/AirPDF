using Avalonia;
using Velopack;

namespace AirPdf.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Velopack must handle install/update hooks before any UI starts.
        VelopackApp.Build().Run();
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont();

#if DEBUG
        builder.LogToTrace();
#endif

        return builder;
    }
}
