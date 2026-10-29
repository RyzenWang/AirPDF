using System.Reflection;
using AirPdf.App.Services;
using AirPdf.App.Views;
using AirPdf.Core.Services;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

namespace AirPdf.App;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
        {
            base.OnFrameworkInitializationCompleted();
            return;
        }

        var options = CommandLineOptions.Parse(desktop.Args);

        // A help or version request should not try to open a document as well.
        var initialFile = options.ShowHelp || options.ShowVersion ? null : options.FilePath;
        var window = new MainWindow(initialFile);
        desktop.MainWindow = window;

        var message = BuildStartupMessage(options);
        if (message is not null)
        {
            window.Opened += (_, _) => Dispatcher.UIThread.Post(
                () => _ = new MessageDialog(message.Value.Title, message.Value.Body).ShowDialog(window),
                DispatcherPriority.Background);
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Builds the dialog shown for command-line help, version, or ignored arguments.
    /// A desktop build is a WinExe, so there is no console to write to.
    /// </summary>
    private static (string Title, string Body)? BuildStartupMessage(CommandLineOptions options)
    {
        if (!options.ShowHelp &&
            !options.ShowVersion &&
            options.UnknownOptions.Count == 0 &&
            options.IgnoredFiles.Count == 0)
        {
            return null;
        }

        var localization = CreateLocalization();
        var sections = new List<string>();
        var title = string.Empty;

        if (options.ShowVersion)
        {
            title = localization.Format("L.CommandLineVersionFormat", GetVersion());
            sections.Add(title);
        }

        if (options.ShowHelp)
        {
            if (title.Length == 0)
            {
                title = localization.T("L.CommandLineHelpTitle");
            }

            sections.Add(localization.T("L.CommandLineHelpBody"));
        }

        if (options.UnknownOptions.Count > 0)
        {
            sections.Add(localization.Format(
                "L.UnknownOptionsFormat",
                string.Join(", ", options.UnknownOptions)));
        }

        if (options.IgnoredFiles.Count > 0 && options.FilePath is not null)
        {
            if (title.Length == 0)
            {
                title = localization.T("L.ExtraFilesTitle");
            }

            sections.Add(localization.Format(
                "L.ExtraFilesFormat",
                options.FilePath,
                string.Join("\n", options.IgnoredFiles)));
        }
        else if (options.IgnoredFiles.Count > 0)
        {
            if (title.Length == 0)
            {
                title = localization.T("L.ExtraFilesTitle");
            }

            sections.Add(localization.Format(
                "L.UnknownOptionsFormat",
                string.Join(", ", options.IgnoredFiles)));
        }

        return sections.Count == 0 ? null : (title, string.Join("\n\n", sections));
    }

    private static LocalizationService CreateLocalization()
    {
        var localization = new LocalizationService();

        try
        {
            localization.Apply(new SettingsService().Load().Language);
        }
        catch
        {
            // A broken settings file must not stop the command-line message.
        }

        return localization;
    }

    private static string GetVersion()
    {
        var assembly = typeof(App).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        if (!string.IsNullOrWhiteSpace(informational))
        {
            var metadata = informational.IndexOf('+');
            return metadata > 0 ? informational[..metadata] : informational;
        }

        return assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    }
}
