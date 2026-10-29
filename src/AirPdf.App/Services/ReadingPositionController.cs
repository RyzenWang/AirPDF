using AirPdf.Core.Models;
using AirPdf.Core.Services;
using Avalonia.Threading;

namespace AirPdf.App.Services;

/// <summary>
/// Owns the per-document reading position, its debounce timer, and the file
/// fingerprint used to ignore stale positions after a document changes.
/// </summary>
public sealed class ReadingPositionController : IDisposable
{
    private const int MaxPositions = 50;

    private readonly SettingsService _settingsService;
    private readonly AppSettings _settings;
    private readonly Func<int> _currentPage;
    private readonly Func<int> _pageCount;
    private readonly Func<double> _zoom;
    private readonly Func<string> _zoomMode;
    private readonly DispatcherTimer _saveTimer;
    private PdfDocumentInfo? _document;
    private ReadingPositionEntry? _pendingPosition;
    private double _pageOffset;
    private long _fileSizeBytes;
    private long _fileWriteUtcTicks;

    public ReadingPositionController(
        SettingsService settingsService,
        AppSettings settings,
        Func<int> currentPage,
        Func<int> pageCount,
        Func<double> zoom,
        Func<string> zoomMode)
    {
        _settingsService = settingsService;
        _settings = settings;
        _currentPage = currentPage;
        _pageCount = pageCount;
        _zoom = zoom;
        _zoomMode = zoomMode;
        _settings.ReadingPositions ??= [];
        _saveTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1.2)
        };
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            SaveNow();
        };
    }

    public void Attach(PdfDocumentInfo document)
    {
        _saveTimer.Stop();
        _document = document;
        _fileSizeBytes = document.FileSizeBytes;
        _fileWriteUtcTicks = GetFileWriteUtcTicks(document.FilePath);
        _pendingPosition = Find(document);
        _pageOffset = _pendingPosition?.PageOffset ?? 0;
    }

    public void Detach()
    {
        _saveTimer.Stop();
        _document = null;
        _pendingPosition = null;
        _pageOffset = 0;
    }

    public ReadingPositionEntry? TakePending()
    {
        var pending = _pendingPosition;
        _pendingPosition = null;
        return pending;
    }

    /// <summary>Zoom mode saved for the document that is currently attached.</summary>
    public string? PendingZoomMode => _pendingPosition?.ZoomMode;

    /// <summary>Zoom factor saved for the document that is currently attached.</summary>
    public double? PendingZoom => _pendingPosition?.Zoom;

    /// <summary>Current offset within the current page, normalized to 0..1.</summary>
    public double PageOffset => _pageOffset;

    public void UpdateOffset(double pageOffset) =>
        _pageOffset = Math.Clamp(pageOffset, 0, 1);

    public void ScheduleSave()
    {
        if (_document is null)
        {
            return;
        }

        _saveTimer.Stop();
        _saveTimer.Start();
    }

    public void SaveNow()
    {
        _saveTimer.Stop();
        if (_document is null || _pageCount() <= 0)
        {
            return;
        }

        var entry = _settings.ReadingPositions.FirstOrDefault(
            candidate => SamePath(candidate.FilePath, _document.FilePath));
        if (entry is null)
        {
            entry = new ReadingPositionEntry { FilePath = _document.FilePath };
            _settings.ReadingPositions.Add(entry);
        }

        entry.Page = Math.Clamp(_currentPage(), 1, _pageCount());
        entry.PageOffset = Math.Clamp(_pageOffset, 0, 1);
        entry.Zoom = _zoom();
        entry.ZoomMode = _zoomMode();
        entry.FileSizeBytes = _fileSizeBytes;
        entry.FileLastWriteUtcTicks = _fileWriteUtcTicks;
        entry.UpdatedAt = DateTimeOffset.UtcNow;

        Prune();
        _settingsService.Save(_settings);
    }

    public void Dispose() => _saveTimer.Stop();

    private ReadingPositionEntry? Find(PdfDocumentInfo document)
    {
        var entry = _settings.ReadingPositions.FirstOrDefault(
            candidate => SamePath(candidate.FilePath, document.FilePath));
        if (entry is null || entry.Page < 1 || entry.Page > document.PageCount)
        {
            return null;
        }

        if (entry.FileSizeBytes > 0 && entry.FileSizeBytes != document.FileSizeBytes)
        {
            return null;
        }

        var lastWriteTicks = GetFileWriteUtcTicks(document.FilePath);
        if (entry.FileLastWriteUtcTicks > 0 &&
            lastWriteTicks > 0 &&
            entry.FileLastWriteUtcTicks != lastWriteTicks)
        {
            return null;
        }

        return entry;
    }

    private void Prune()
    {
        var retained = _settings.ReadingPositions
            .OrderByDescending(entry => entry.UpdatedAt)
            .Take(MaxPositions)
            .ToHashSet();

        _settings.ReadingPositions.RemoveAll(entry => !retained.Contains(entry));
    }

    private static long GetFileWriteUtcTicks(string filePath)
    {
        try
        {
            return File.Exists(filePath) ? File.GetLastWriteTimeUtc(filePath).Ticks : 0;
        }
        catch
        {
            return 0;
        }
    }

    private static bool SamePath(string left, string right)
    {
        try
        {
            var comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), comparison);
        }
        catch
        {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }
    }
}
