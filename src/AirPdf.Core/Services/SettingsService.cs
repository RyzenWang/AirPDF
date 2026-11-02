using System.Text.Json;

namespace AirPdf.Core.Services;

public sealed class AppSettings
{
    public List<RecentFileEntry> RecentFiles { get; set; } = [];

    public List<ReadingPositionEntry> ReadingPositions { get; set; } = [];

    /// <summary>Bookmarks the reader created, across all documents.</summary>
    public List<BookmarkEntry> Bookmarks { get; set; } = [];

    public string Theme { get; set; } = "System";

    public string Language { get; set; } = "en";

    public bool SidebarVisible { get; set; } = true;

    public double WindowWidth { get; set; } = 1240;

    public double WindowHeight { get; set; } = 860;

    public bool WindowMaximized { get; set; }

    /// <summary>
    /// Restored window top-left in physical pixels. Null lets the platform place the
    /// window; a value is only applied after it is validated against the current screens.
    /// </summary>
    public int? WindowX { get; set; }

    public int? WindowY { get; set; }
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

    /// <summary>
    /// Last zoom mode for this document: <c>fit-width</c>, <c>fit-page</c>, or
    /// <c>manual</c>. Null (older settings files) is treated as <c>fit-width</c>.
    /// </summary>
    public string? ZoomMode { get; set; }

    /// <summary>Last zoom factor. Only applied when <see cref="ZoomMode"/> is <c>manual</c>.</summary>
    public double? Zoom { get; set; }
}

/// <summary>
/// A bookmark the reader created. This is separate from the PDF's own outline: the outline
/// belongs to the document and is read-only, while these are per-machine notes about where
/// the reader was, stored alongside the reading position for the same file.
/// </summary>
public sealed class BookmarkEntry
{
    public string FilePath { get; set; } = string.Empty;

    public int Page { get; set; } = 1;

    /// <summary>Offset within the page, normalized to 0..1.</summary>
    public double PageOffset { get; set; }

    public string Label { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
/// <summary>
/// Small JSON settings store in the platform's per-user application data folder.
/// </summary>
public sealed class SettingsService
{
    private const int MaxRecentFiles = 10;
    private const int MaxBookmarksPerDocument = 100;
    private readonly object _gate = new();

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
                var settings = JsonSerializer.Deserialize(json, SettingsJsonContext.Default.AppSettings) ?? new AppSettings();
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
                var json = JsonSerializer.Serialize(settings, SettingsJsonContext.Default.AppSettings);
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

    /// <summary>Bookmarks for one document, ordered by page and then by creation time.</summary>
    public IReadOnlyList<BookmarkEntry> GetBookmarks(AppSettings settings, string filePath)
    {
        lock (_gate)
        {
            return settings.Bookmarks
                .Where(entry => IsSameFile(entry.FilePath, filePath))
                .OrderBy(entry => entry.Page)
                .ThenBy(entry => entry.PageOffset)
                .ThenBy(entry => entry.CreatedAt)
                .ToList();
        }
    }

    /// <summary>
    /// Adds a bookmark, or returns the existing one when the reader already bookmarked this
    /// spot: pressing the shortcut twice should not create two identical entries.
    /// </summary>
    public BookmarkEntry AddBookmark(
        AppSettings settings,
        string filePath,
        int page,
        double pageOffset,
        string label)
    {
        lock (_gate)
        {
            var existing = settings.Bookmarks.FirstOrDefault(
                entry => IsSameFile(entry.FilePath, filePath)
                    && entry.Page == page
                    && Math.Abs(entry.PageOffset - pageOffset) < 0.002);

            if (existing is not null)
            {
                return existing;
            }

            var bookmark = new BookmarkEntry
            {
                FilePath = filePath,
                Page = page,
                PageOffset = pageOffset,
                Label = label,
                CreatedAt = DateTimeOffset.UtcNow
            };

            settings.Bookmarks.Add(bookmark);

            // Keep the file from growing without bound if a reader bookmarks page by page.
            var forThisFile = settings.Bookmarks.Where(entry => IsSameFile(entry.FilePath, filePath)).ToList();
            if (forThisFile.Count > MaxBookmarksPerDocument)
            {
                foreach (var stale in forThisFile
                             .OrderByDescending(entry => entry.CreatedAt)
                             .Skip(MaxBookmarksPerDocument))
                {
                    settings.Bookmarks.Remove(stale);
                }
            }

            return bookmark;
        }
    }

    public bool RemoveBookmark(AppSettings settings, BookmarkEntry bookmark)
    {
        lock (_gate)
        {
            return settings.Bookmarks.Remove(bookmark);
        }
    }

    private static bool IsSameFile(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
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
