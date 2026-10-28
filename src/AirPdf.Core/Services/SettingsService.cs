using System.Text.Json;

namespace AirPdf.Core.Services;

public sealed class AppSettings
{
    public List<RecentFileEntry> RecentFiles { get; set; } = [];

    public List<ReadingPositionEntry> ReadingPositions { get; set; } = [];

    public string Theme { get; set; } = "System";

    public string Language { get; set; } = "en";

    public bool SidebarVisible { get; set; } = true;

    public double WindowWidth { get; set; } = 1240;

    public double WindowHeight { get; set; } = 860;

    public bool WindowMaximized { get; set; }
}

public sealed class RecentFileEntry
{
    public string FilePath { get; set; } = string.Empty;

    public DateTimeOffset LastOpened { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class ReadingPositionEntry
{
    public string FilePath { get; set; } = string.Empty;

    public int Page { get; set; } = 1;

    /// <summary>Offset within the page, normalized to 0..1.</summary>
    public double PageOffset { get; set; }

    public long FileSizeBytes { get; set; }

    public long FileLastWriteUtcTicks { get; set; }

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Small JSON settings store in the platform's per-user application data folder.
/// </summary>
public sealed class SettingsService
{
    private const int MaxRecentFiles = 10;
    private readonly object _gate = new();
    private readonly JsonSerializerOptions _options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public SettingsService(string? settingsDirectory = null)
    {
        SettingsDirectory = settingsDirectory ?? GetDefaultDirectory();
        SettingsPath = Path.Combine(SettingsDirectory, "settings.json");
    }

    public string SettingsDirectory { get; }

    public string SettingsPath { get; }

    public AppSettings Load()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(SettingsPath))
                {
                    return new AppSettings();
                }

                var json = File.ReadAllText(SettingsPath);
                var settings = JsonSerializer.Deserialize<AppSettings>(json, _options) ?? new AppSettings();
                settings.RecentFiles ??= [];
                settings.ReadingPositions ??= [];
                return settings;
            }
            catch
            {
                return new AppSettings();
            }
        }
    }

    public void Save(AppSettings settings)
    {
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(SettingsDirectory);
                var json = JsonSerializer.Serialize(settings, _options);
                var tempPath = SettingsPath + ".tmp";
                File.WriteAllText(tempPath, json);
                File.Move(tempPath, SettingsPath, overwrite: true);
            }
            catch
            {
                // Preferences should never prevent reading a document.
            }
        }
    }

    public void AddRecentFile(AppSettings settings, string filePath)
    {
        lock (_gate)
        {
            settings.RecentFiles.RemoveAll(
                entry => string.Equals(entry.FilePath, filePath, StringComparison.OrdinalIgnoreCase));

            settings.RecentFiles.Insert(0, new RecentFileEntry
            {
                FilePath = filePath,
                LastOpened = DateTimeOffset.UtcNow
            });

            if (settings.RecentFiles.Count > MaxRecentFiles)
            {
                settings.RecentFiles.RemoveRange(MaxRecentFiles, settings.RecentFiles.Count - MaxRecentFiles);
            }
        }
    }

    public void RemoveRecentFile(AppSettings settings, string filePath)
    {
        lock (_gate)
        {
            settings.RecentFiles.RemoveAll(
                entry => string.Equals(entry.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
        }
    }

    private static string GetDefaultDirectory()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrWhiteSpace(root))
        {
            root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        }

        if (string.IsNullOrWhiteSpace(root))
        {
            root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        }

        return Path.Combine(root, "AirPdf");
    }
}
