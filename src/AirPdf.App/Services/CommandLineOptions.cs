namespace AirPdf.App.Services;

/// <summary>
/// Parsed command line. AirPDF opens one document per window, so only the first
/// existing file is used; the remaining arguments are reported back to the user
/// instead of being dropped silently.
/// </summary>
public sealed class CommandLineOptions
{
    private CommandLineOptions()
    {
    }

    public bool ShowHelp { get; private init; }

    public bool ShowVersion { get; private init; }

    /// <summary>The first argument that names an existing file, if any.</summary>
    public string? FilePath { get; private init; }

    /// <summary>Existing files after the first one, plus paths that do not exist.</summary>
    public IReadOnlyList<string> IgnoredFiles { get; private init; } = [];

    /// <summary>Arguments that looked like options but were not recognised.</summary>
    public IReadOnlyList<string> UnknownOptions { get; private init; } = [];

    public static CommandLineOptions Parse(IReadOnlyList<string>? args)
    {
        var filePaths = new List<string>();
        var unknownOptions = new List<string>();
        var showHelp = false;
        var showVersion = false;
        var filesOnly = false;

        if (args is not null)
        {
            foreach (var arg in args)
            {
                if (string.IsNullOrWhiteSpace(arg))
                {
                    continue;
                }

                if (filesOnly)
                {
                    filePaths.Add(arg);
                    continue;
                }

                switch (arg)
                {
                    case "--":
                        filesOnly = true;
                        continue;
                    case "-h" or "--help" or "-?":
                        showHelp = true;
                        continue;
                    case "--version" or "-v":
                        showVersion = true;
                        continue;
                }

                if (arg.Length > 1 && arg[0] == '-')
                {
                    unknownOptions.Add(arg);
                    continue;
                }

                filePaths.Add(arg);
            }
        }

        var existing = filePaths.Where(File.Exists).ToList();
        var missing = filePaths.Where(path => !File.Exists(path)).ToList();

        return new CommandLineOptions
        {
            ShowHelp = showHelp,
            ShowVersion = showVersion,
            FilePath = existing.FirstOrDefault(),
            IgnoredFiles = [.. existing.Skip(1), .. missing],
            UnknownOptions = unknownOptions
        };
    }
}
