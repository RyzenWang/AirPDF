using System.Text;
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

    /// <summary>
    /// Position in the sidebar list. The reader can drag bookmarks around, so the stored
    /// order wins over page order; entries written before this existed default to 0 and
    /// fall back to creation order, which for normal reading is page order anyway.
    /// </summary>
    public int Order { get; set; }

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

    /// <summary>Previous good copy, used when <see cref="SettingsPath"/> is unreadable.</summary>
    public string BackupPath => SettingsPath + ".bak";

    public AppSettings Load()
    {
        lock (_gate)
        {
            return Read(SettingsPath) ?? Read(BackupPath) ?? new AppSettings();
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
                WriteSettings(json);
            }
            catch
            {
                // Preferences should never prevent reading a document.
            }
        }
    }

    /// <summary>
    /// Writes over the existing file when there is one.
    /// </summary>
    /// <remarks>
    /// Overwriting in place is not just an optimisation. A packaged (MSIX) build runs with
    /// AppData redirection: creating a file under AppData sends it to the package's private
    /// cache, but opening a file that already exists at its real location does not. Because
    /// the old implementation always wrote a temporary file and renamed it, it always created
    /// a file, so a packaged build silently forked the user's settings into the cache. Opening
    /// the real file keeps both builds on one file.
    ///
    /// The cost is that an interrupted overwrite can truncate the file, so the previous good
    /// copy is kept in <see cref="BackupPath"/> and <see cref="Load"/> falls back to it.
    /// </remarks>
    private void WriteSettings(string json)
    {
        var exists = File.Exists(SettingsPath);
        if (exists)
        {
            BackUpCurrentSettings();
        }

        if (exists)
        {
            try
            {
                WriteInPlace(json);
                return;
            }
            catch (IOException)
            {
                // Another process holds the file, or the volume refused the open.
            }
            catch (UnauthorizedAccessException)
            {
                // Read-only profile, or a policy blocked the write.
            }
        }

        WriteAtomically(json);
    }

    private void WriteInPlace(string json)
    {
        using var stream = new FileStream(
            SettingsPath,
            FileMode.Open,
            FileAccess.Write,
            FileShare.None);

        var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(json);
        stream.SetLength(0);
        stream.Write(bytes, 0, bytes.Length);
        stream.Flush(flushToDisk: true);
    }

    private void WriteAtomically(string json)
    {
        var tempPath = SettingsPath + ".tmp";
        try
        {
            File.WriteAllText(tempPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(tempPath, SettingsPath, overwrite: true);
        }
        finally
        {
            // A failed move must not leave a stray file behind on the next start.
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch
            {
                // Best effort only.
            }
        }
    }

    private void BackUpCurrentSettings()
    {
        try
        {
            File.Copy(SettingsPath, BackupPath, overwrite: true);
        }
        catch
        {
            // A missing or locked backup only weakens crash recovery, not saving.
        }
    }

    private static AppSettings? Read(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            var settings = JsonSerializer.Deserialize(json, SettingsJsonContext.Default.AppSettings);
            if (settings is null)
            {
                return null;
            }

            settings.RecentFiles ??= [];
            settings.ReadingPositions ??= [];
            settings.Bookmarks ??= [];
            return settings;
        }
        catch
        {
            return null;
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
                .OrderBy(entry => entry.Order)
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

            var nextOrder = settings.Bookmarks
                .Where(entry => IsSameFile(entry.FilePath, filePath))
                .Select(entry => entry.Order)
                .DefaultIfEmpty(-1)
                .Max() + 1;

            var bookmark = new BookmarkEntry
            {
                FilePath = filePath,
                Order = nextOrder,
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

    /// <summary>Renames a bookmark; the caller trims and decides what an empty label means.</summary>
    public bool RenameBookmark(AppSettings settings, BookmarkEntry bookmark, string label)
    {
        lock (_gate)
        {
            if (!settings.Bookmarks.Contains(bookmark))
            {
                return false;
            }

            bookmark.Label = label;
            return true;
        }
    }

    /// <summary>
    /// Moves a bookmark within its document and reassigns the stored order, so dragging
    /// survives a restart.
    /// </summary>
    public bool MoveBookmark(AppSettings settings, string filePath, BookmarkEntry bookmark, int targetIndex)
    {
        lock (_gate)
        {
            var ordered = settings.Bookmarks
                .Where(entry => IsSameFile(entry.FilePath, filePath))
                .OrderBy(entry => entry.Order)
                .ThenBy(entry => entry.CreatedAt)
                .ToList();

            var from = ordered.IndexOf(bookmark);
            if (from < 0)
            {
                return false;
            }

            var to = Math.Clamp(targetIndex, 0, ordered.Count - 1);
            if (to == from)
            {
                return false;
            }

            ordered.RemoveAt(from);
            ordered.Insert(to, bookmark);

            for (var index = 0; index < ordered.Count; index++)
            {
                ordered[index].Order = index;
            }

            return true;
        }
    }

    /// <summary>Every bookmark in the settings file, ordered by file and then by list order.</summary>
    public IReadOnlyList<BookmarkEntry> GetAllBookmarks(AppSettings settings)
    {
        lock (_gate)
        {
            return settings.Bookmarks
                .OrderBy(entry => entry.FilePath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(entry => entry.Order)
                .ThenBy(entry => entry.CreatedAt)
                .ToList();
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
