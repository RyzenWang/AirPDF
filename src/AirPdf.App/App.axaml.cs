using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using AirPdf.App.Views;

namespace AirPdf.App;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var initialFile = desktop.Args?.FirstOrDefault(arg =>
                !string.IsNullOrWhiteSpace(arg) &&
                !arg.StartsWith('-') &&
                File.Exists(arg));

            desktop.MainWindow = new MainWindow(initialFile);
        }

        base.OnFrameworkInitializationCompleted();
    }
}
