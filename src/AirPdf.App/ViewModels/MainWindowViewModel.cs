using System.Globalization;
using AirPdf.App.Rendering;
using AirPdf.App.Services;
using AirPdf.Core.Models;
using AirPdf.Core.Services;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Velopack;
using Velopack.Exceptions;

namespace AirPdf.App.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject, IDisposable
{
    private static readonly double[] ZoomSteps =
        [0.25, 0.33, 0.5, 0.67, 0.75, 0.9, 1.0, 1.1, 1.25, 1.5, 1.75, 2.0, 2.5, 3.0, 4.0];

    private const int MaxHistoryEntries = 50;

    /// <summary>
    /// How many markup edits the undo history keeps. The history used to be unbounded, and every
    /// step holds a snapshot of what it changed (an ink step keeps all of its points), so a long
    /// editing session accumulated memory that could never be used again. Older edits are
    /// dropped; the document still contains them, only the ability to step back that far is gone.
    /// </summary>
    public const int MaxUndoSteps = 100;

    /// <summary>
    /// Ctrl+A has to parse every page before it can select the whole document. Beyond this
    /// many pages that is a multi-second, hundreds-of-megabytes operation, so it is refused
    /// with a status message instead of freezing the window.
    /// </summary>
    public const int MaxSelectAllPages = 1000;

    /// <summary>Pages within this distance of the current page keep their parsed words.</summary>
    private const int TextCacheRadius = 4;

    /// <summary>
    /// Total budget for page bitmaps. At the 7 MP cap a single page is about 28 MB, so a
    /// page-count-only rule can retain well over 200 MB; this keeps the working set bounded
    /// while always preserving the current page and its immediate neighbours.
    /// </summary>
    private const long PageBitmapBudgetBytes = 128L * 1024 * 1024;

    /// <summary>Must match <c>SettingsService.MaxRecentFiles</c> so nothing stored is unreachable.</summary>
    private const int MaxRecentFilesShown = 10;

    private readonly PdfDocumentService _documentService;
    private readonly PdfSearchService _searchService;
    private readonly PdfTextService _textService;
    private readonly SettingsService _settingsService;
    private readonly UpdateService _updateService;
    private readonly IUserDialogService _dialogs;
    private readonly LocalizationService _localization;
    private readonly ThemeService _theme;
    private readonly IWindowService _windows;
    private readonly ITabService? _tabs;
    private readonly AppServices _services;
    private readonly AppSettings _settings;
    private readonly List<NavigationPoint> _history = [];

    /// <summary>Ink thickness in points, chosen from the palette; 1 to 10.</summary>
    [ObservableProperty]
    private double _inkWidth = 2;

    /// <summary>The colours offered for markup, in one list for both highlight and ink.</summary>
    public IReadOnlyList<MarkupColor> Palette { get; } =
    [
        new("Yellow", 255, 221, 71),
        new("Green", 139, 195, 74),
        new("Blue", 100, 181, 246),
        new("Pink", 244, 143, 177),
        new("Orange", 255, 171, 64),
        new("Red", 239, 83, 80),
        new("Grey", 120, 124, 130),
        new("Black", 32, 33, 36)
    ];

    /// <summary>
    /// What to do when the selection is already highlighted. Skipping keeps a word from growing
    /// darker every time it is highlighted; the other value is here so stacking stays a choice
    /// rather than something the code would have to be restructured for.
    /// </summary>
    public enum HighlightOverlapPolicy
    {
        Skip,
        Stack
    }

    /// <summary>Current policy; skipping duplicates by default.</summary>
    public HighlightOverlapPolicy HighlightPolicy { get; set; } = HighlightOverlapPolicy.Stack;

    /// <summary>Colour used for the next highlight; yellow until the reader picks another.</summary>
    [ObservableProperty]
    private MarkupColor _highlightColor;

    /// <summary>Colour used for the next stroke; near-black until the reader picks another.</summary>
    [ObservableProperty]
    private MarkupColor _inkColor;

    /// <summary>
    /// While this is on, dragging on a page draws instead of selecting text. It is a mode rather
    /// than a modifier because a stroke is a drag, and a drag already means selection.
    /// </summary>
    [ObservableProperty]
    private bool _isInkMode;

    /// <summary>
    /// While this is on, dragging over the page rubs out markup instead of drawing. It owns the
    /// pointer the same way drawing does, so the two modes exclude each other.
    /// </summary>
    [ObservableProperty]
    private bool _isEraserMode;

    /// <summary>Markup removed during the current drag, so one stroke of the eraser is one undo.</summary>
    private readonly List<PdfMarkup> _erased = [];

    /// <summary>
    /// One reader action as a pair of closures: what it takes to undo it, and what it takes to
    /// apply it again. Highlights, ink and notes all go through it, so Ctrl+Z behaves the same
    /// whatever was drawn, and a new kind of edit only has to describe its own two directions.
    /// </summary>
    private sealed record UndoStep(Func<Task> Undo, Func<Task> Redo);

    /// <summary>Markup per page, filled on demand and dropped whenever an edit lands.</summary>
    private readonly Dictionary<int, IReadOnlyList<PdfMarkup>> _markupCache = [];

    /// <summary>Page rotation per page; the hit test needs it and it does not change.</summary>
    private readonly Dictionary<int, int> _rotationCache = [];

    /// <summary>
    /// Markup edits, newest last. A list rather than a stack because the oldest entry has to be
    /// dropped once <see cref="MaxUndoSteps"/> is reached.
    /// </summary>
    private readonly List<UndoStep> _annotationUndo = [];

    private readonly List<UndoStep> _annotationRedo = [];
    private NavigationPoint? _pendingOpenPosition;
    private CancellationTokenSource? _loadCancellation;
    private CancellationTokenSource? _searchCancellation;
    private PdfDocumentInfo? _document;
    private PdfPageRenderer? _pageRenderer;
    private string? _password;
    private bool _disposed;
    private bool _selectingAllText;
    private double _zoom = 1.0;
    private string _zoomMode = ZoomModes.FitWidth;
    private double? _restoreZoom;
    private string? _restoreZoomMode;
    private int _currentPage = 1;
    private SearchResultViewModel? _selectedSearchResult;
    private string? _errorMessage;
    private string? _errorKey;
    private string? _busyFileName;
    private bool _searchFailed;
    private TextLayerLoader? _textLoader;
    private readonly ReadingPositionController _reading;
    private readonly TextSelectionController _selection;
    private UpdateInfo? _availableUpdate;

    public MainWindowViewModel(
        AppServices services,
        PdfDocumentService documentService,
        PdfSearchService searchService,
        PdfTextService textService,
        IUserDialogService dialogs,
        IWindowService windows,
        ITabService? tabs = null,
        bool isHomeTab = false)
    {
        _documentService = documentService;
        _searchService = searchService;
        _textService = textService;
        _settingsService = services.SettingsService;
        _updateService = services.Updates;
        _dialogs = dialogs;
        _localization = services.Localization;
        _theme = services.Theme;
        _windows = windows;
        _tabs = tabs;
        IsHomeTab = isHomeTab;
        _services = services;
        _settings = services.Settings;
        _isSidebarVisible = _settings.SidebarVisible;
        _settings.RecentFiles ??= [];
        _settings.ReadingPositions ??= [];
        _highlightColor = Palette[0];
        _inkColor = Palette[^1];
        _documentSubtitle = _localization.T("L.Ready");
        _reading = new ReadingPositionController(
            _settingsService,
            _settings,
            () => _currentPage,
            () => PageCount,
            () => _zoom,
            () => _zoomMode);
        _selection = new TextSelectionController(() => Pages, () => _textLoader);
        _selection.SelectionChanged += (_, _) => NotifySelectionChanged();

        _theme.Changed += OnThemeChanged;
        _services.RecentFilesChanged += OnSharedRecentFilesChanged;
        _services.BookmarksChanged += OnSharedBookmarksChanged;
        _localization.LanguageChanged += OnLanguageChanged;
        RefreshRecentFiles();
        RefreshAllBookmarks();

        // Only the first window in the process checks the update feed.
        if (UpdateService.TryBeginStartupCheck())
        {
            _ = CheckForUpdatesAsync(userInitiated: false);
        }
    }

    public event EventHandler<int>? ScrollToPageRequested;

    public event EventHandler<NavigationPoint>? ReturnToPositionRequested;

    public event EventHandler? DocumentOpened;

    public event EventHandler? ZoomChanged;

    public event EventHandler? SearchOpened;

    public event EventHandler<SearchResultViewModel>? ScrollToSearchResultRequested;

    public event EventHandler<string>? CopySelectionRequested;

    public AppSettings Settings => _settings;

    [ObservableProperty]
    private IReadOnlyList<PageViewModel> _pages = [];

    [ObservableProperty]
    private IReadOnlyList<ThumbnailViewModel> _thumbnails = [];

    [ObservableProperty]
    private IReadOnlyList<OutlineItemViewModel> _outlineItems = [];

    [ObservableProperty]
    private IReadOnlyList<UserBookmarkViewModel> _bookmarks = [];

    /// <summary>
    /// True once an annotation has been added and no copy has been written since. Annotations
    /// live in memory only, so the window has to say so or a reader will close it and lose them.
    /// </summary>
    [ObservableProperty]
    private bool _hasUnsavedAnnotations;

    /// <summary>A short-lived line in the status bar, for "highlight added" and "copy saved".</summary>
    [ObservableProperty]
    private string _statusMessage = string.Empty;

    /// <summary>Bookmarks from every document, for the overview on the welcome screen.</summary>
    [ObservableProperty]
    private IReadOnlyList<BookmarkOverviewViewModel> _allBookmarks = [];

    [ObservableProperty]
    private IReadOnlyList<MetadataRow> _metadata = [];

    [ObservableProperty]
    private IReadOnlyList<RecentFileViewModel> _recentFiles = [];

    [ObservableProperty]
    private IReadOnlyList<SearchResultViewModel> _searchResults = [];

    [ObservableProperty]
    private bool _isDocumentOpen;

    [ObservableProperty]
    private string _fileName = string.Empty;

    [ObservableProperty]
    private string _filePath = string.Empty;

    /// <summary>
    /// The file this tab is in the middle of opening. <see cref="FilePath"/> stays empty until
    /// the document has loaded, so this is what lets a second open of the same file find the
    /// tab already working on it instead of starting a duplicate session over the same file.
    /// </summary>
    public string? OpeningPath { get; private set; }

    [ObservableProperty]
    private string _fileDirectory = string.Empty;

    [ObservableProperty]
    private string _fileSizeText = string.Empty;

    [ObservableProperty]
    private string _documentSubtitle = "Ready";

    [ObservableProperty]
    private string _windowTitle = "AirPDF";

    [ObservableProperty]
    private int _pageCount;

    [ObservableProperty]
    private string _pageNumberInput = "1";

    [ObservableProperty]
    private bool _isSidebarVisible;

    [ObservableProperty]
    private bool _isPagesTabSelected = true;

    [ObservableProperty]
    private bool _isContentsTabSelected;

    [ObservableProperty]
    private bool _isBookmarksTabSelected;

    [ObservableProperty]
    private bool _isSearchOpen;

    [ObservableProperty]
    private bool _isSearching;

    [ObservableProperty]
    private bool _hasSearched;

    [ObservableProperty]
    private string _searchQuery = string.Empty;

    [ObservableProperty]
    private string _searchSummary = string.Empty;

    [ObservableProperty]
    private bool _searchResultsTruncated;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _busyText = "Opening document…";

    [ObservableProperty]
    private bool _isDragOver;

    private bool _hasSearchResults;

    public string ZoomText => $"{(int)Math.Round(Zoom * 100)}%";

    public string LanguageShortLabel => _localization.LanguageShortLabel;

    public string PageStatusText => PageCount == 0
        ? _localization.T("L.NoDocument")
        : _localization.Format("L.PageStatusFormat", CurrentPage, PageCount);

    public string PageCountText => PageCount == 1
        ? _localization.T("L.PageCountOne")
        : _localization.Format("L.PageCountMany", PageCount);

    /// <summary>
    /// Reading progress as a 0..1 fraction. Uses the page index plus the normalized
    /// offset within the page, so it advances smoothly while scrolling.
    /// </summary>
    public double ReadingProgress
    {
        get
        {
            if (PageCount <= 0)
            {
                return 0;
            }

            var offset = Math.Clamp(_reading.PageOffset, 0, 1);
            return Math.Clamp(((CurrentPage - 1) + offset) / PageCount, 0, 1);
        }
    }

    public string ReadingProgressText => PageCount <= 0
        ? string.Empty
        : string.Format(CultureInfo.InvariantCulture, "{0:0}%", ReadingProgress * 100);

    public bool CanNavigateBack => _history.Count > 0;

    public bool HasRecentFiles => RecentFiles.Count > 0;

    public bool HasNoRecentFiles => !HasRecentFiles;

    public bool HasOutlineItems => OutlineItems.Count > 0;

    public bool HasNoOutlineItems => !HasOutlineItems;

    public bool HasStatusMessage => !string.IsNullOrWhiteSpace(StatusMessage);

    public bool HasBookmarks => Bookmarks.Count > 0;

    public bool HasNoBookmarks => !HasBookmarks;

    public bool HasAllBookmarks => AllBookmarks.Count > 0;

    public bool HasNoAllBookmarks => !HasAllBookmarks;

    public bool HasMetadata => Metadata.Count > 0;

    public bool HasSearchQuery => !string.IsNullOrWhiteSpace(SearchQuery);

    public bool HasSelection => _selection.HasSelection;

    public string SelectionStatus => HasSelection
        ? _localization.Format("L.SelectionStatusFormat", _selection.SelectedText.Length)
        : string.Empty;

    [ObservableProperty]
    private bool _hasUpdate;

    [ObservableProperty]
    private bool _isUpdateReady;

    [ObservableProperty]
    private bool _isUpdating;

    [ObservableProperty]
    private int _updateProgress;

    [ObservableProperty]
    private string _updateVersion = string.Empty;

    public bool CanApplyUpdate => HasUpdate && !IsUpdating;

    /// <summary>Store and portable builds have no Velopack updater, so the button is hidden.</summary>
    public bool CanCheckForUpdates => _updateService.IsInstalled;

    public string UpdateButtonText
    {
        get
        {
            if (!HasUpdate)
            {
                return string.Empty;
            }

            if (IsUpdating)
            {
                return _localization.Format("L.UpdatingFormat", UpdateProgress);
            }

            return IsUpdateReady
                ? _localization.Format("L.RestartToUpdateFormat", UpdateVersion)
                : _localization.Format("L.UpdateAvailableFormat", UpdateVersion);
        }
    }

    public bool HasSearchResults
    {
        get => _hasSearchResults;
        private set
        {
            if (SetProperty(ref _hasSearchResults, value))
            {
                OnPropertyChanged(nameof(HasNoSearchResults));
            }
        }
    }

    public bool HasNoSearchResults => HasSearched && !IsSearching && !HasSearchResults;

    public bool IsWelcomeVisible => !IsDocumentOpen && !IsBusy;

    /// <summary>True for the window's home tab, which shows the welcome screen.</summary>
    public bool IsHomeTab { get; }

    public bool IsNotHomeTab => !IsHomeTab;

    /// <summary>Label shown on the window's tab strip.</summary>
    public string TabTitle => IsHomeTab
        ? _localization.T("L.Home")
        : IsDocumentOpen && !string.IsNullOrWhiteSpace(FileName)
        ? FileName
        : _localization.T("L.NewTab");

    /// <summary>Where this tab is scrolled to; used when its view is re-attached.</summary>
    public NavigationPoint CurrentPosition =>
        new(CurrentPage, Math.Clamp(_reading.PageOffset, 0, 1));

    public bool IsSidebarShown => IsDocumentOpen && IsSidebarVisible;

    public bool IsLightTheme => !IsDarkTheme;

    public bool IsDarkTheme => _theme.IsDark;

    public bool IsSystemTheme => _theme.IsSystem;

    public string ThemeTooltip => _localization.Format(
        "L.ThemeTooltipFormat",
        _localization.T(ThemeService.ModeLabelKey(_theme.Mode)));

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

    public string ErrorMessage
    {
        get => _errorMessage ?? string.Empty;
        private set
        {
            if (SetProperty(ref _errorMessage, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public int CurrentPage => _currentPage;

    public double Zoom
    {
        get => _zoom;
        set
        {
            var clamped = Math.Clamp(value, 0.25, 4.0);
            if (Math.Abs(clamped - _zoom) < 0.0001)
            {
                return;
            }

            _zoom = clamped;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ZoomText));
            ApplyZoomToPages();
            ZoomChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public SearchResultViewModel? SelectedSearchResult
    {
        get => _selectedSearchResult;
        set
        {
            if (SetProperty(ref _selectedSearchResult, value))
            {
                ApplySearchHighlights(value);
                if (value is not null)
                {
                    PushHistory();
                    SetCurrentPage(value.PageNumber);
                    ScrollToSearchResultRequested?.Invoke(this, value);
                }
            }
        }
    }

    public bool CanGoPrevious => IsDocumentOpen && CurrentPage > 1;

    public bool CanGoNext => IsDocumentOpen && CurrentPage < PageCount;

    public double ViewportWidth { get; private set; }

    public double ViewportHeight { get; private set; }

    [RelayCommand]
    private async Task OpenAsync()
    {
        if (IsBusy)
        {
            return;
        }

        var path = await _dialogs.PickPdfAsync();
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        // Opening a second document must not throw away the one being read: put it in a
        // new tab. The welcome screen (no document open) keeps using this tab.
        if (IsDocumentOpen && _tabs is not null)
        {
            _tabs.OpenDocument(path);
            return;
        }

        await OpenPathAsync(path);
    }

    [RelayCommand]
    private async Task OpenRecentAsync(string? path)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            await OpenPathAsync(path);
        }
    }

    [RelayCommand]
    private async Task RemoveRecentAsync(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        _settingsService.RemoveRecentFile(_settings, path);
        _settingsService.Save(_settings);
        _services.NotifyRecentFilesChanged();
        RefreshRecentFiles();
        await Task.CompletedTask;
    }

    [RelayCommand]
    private void ClearRecent()
    {
        _settings.RecentFiles.Clear();
        _settingsService.Save(_settings);
        _services.NotifyRecentFilesChanged();
        RefreshRecentFiles();
    }

    [RelayCommand]
    private void CloseDocument()
    {
        SaveReadingPositionNow();
        CancelSafely(_loadCancellation);
        CancelSafely(_searchCancellation);
        CloseDocumentCore();
        _documentService.CloseDocument();
    }

    /// <summary>
    /// Releases the PDFium session without blocking the UI thread. Close paths await this
    /// before disposing the tab; the subsequent Dispose then has no handle left to close.
    /// </summary>
    public async Task ReleaseDocumentAsync()
    {
        if (!IsDocumentOpen || _disposed)
        {
            return;
        }

        SaveReadingPositionNow();
        CancelSafely(_loadCancellation);
        CancelSafely(_searchCancellation);

        try
        {
            await _documentService.CloseDocumentAsync();
        }
        catch (Exception unexpected)
        {
            // The fallback sync close in Dispose still has a chance to release the handle.
            CrashLog.Write("CloseDocument", unexpected);
        }
    }

    /// <summary>
    /// The logo in the toolbar: returns to the welcome screen. Closing the document is exactly
    /// that, and it saves the reading position on the way out, so nothing is lost.
    /// </summary>
    [RelayCommand]
    private void GoHome()
    {
        // Documents stay open: home is a tab, not the absence of a document.
        if (_tabs is not null && !IsHomeTab)
        {
            _tabs.ShowHome();
            return;
        }

        // Without a tab host (unit tests, headless hosts) fall back to closing.
        if (IsDocumentOpen)
        {
            CloseDocument();
        }
    }

    [RelayCommand]
    private void ToggleSidebar()
    {
        IsSidebarVisible = !IsSidebarVisible;
        _settings.SidebarVisible = IsSidebarVisible;
        _settingsService.Save(_settings);
    }

    [RelayCommand]
    private void ToggleTheme() => _theme.Cycle();

    /// <summary>Opens a second, empty window.</summary>
    [RelayCommand]
    private void NewWindow() => _windows.OpenNewWindow();

    /// <summary>Opens the current document in another window.</summary>
    [RelayCommand]
    private void OpenInNewWindow()
    {
        if (!IsDocumentOpen || string.IsNullOrWhiteSpace(FilePath))
        {
            return;
        }

        _windows.OpenInNewWindow(FilePath);
    }

    [RelayCommand]
    private void NavigateBack()
    {
        if (!IsDocumentOpen || _history.Count == 0)
        {
            return;
        }

        var target = _history[^1];
        _history.RemoveAt(_history.Count - 1);
        OnPropertyChanged(nameof(CanNavigateBack));

        SetCurrentPage(target.Page);
        ReturnToPositionRequested?.Invoke(this, target);
    }

    [RelayCommand]
    private void OpenContainingFolder()
    {
        if (!IsDocumentOpen || string.IsNullOrWhiteSpace(FilePath))
        {
            return;
        }

        try
        {
            if (OperatingSystem.IsWindows())
            {
                StartProcess("explorer.exe", $"/select,\"{FilePath}\"");
            }
            else if (OperatingSystem.IsMacOS())
            {
                StartProcess("open", $"-R \"{FilePath}\"");
            }
            else
            {
                StartProcess("xdg-open", $"\"{FileDirectory}\"");
            }
        }
        catch
        {
            SetError("L.ExplorerError");
        }
    }

    [RelayCommand]
    private void SetLanguage(string? language)
    {
        _localization.Apply(language);
        _settings.Language = _localization.Language;
        _settingsService.Save(_settings);
    }

    [RelayCommand]
    private void ToggleLanguage()
    {
        SetLanguage(_localization.IsChinese ? LocalizationService.English : LocalizationService.ChineseSimplified);
    }

    [RelayCommand]
    private void ToggleSearch()
    {
        if (IsSearchOpen)
        {
            IsSearchOpen = false;
            return;
        }

        IsSearchOpen = true;
        SearchOpened?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void CloseSearch()
    {
        IsSearchOpen = false;
        ClearSearchState();
    }

    [RelayCommand]
    private void ClearSearch()
    {
        SearchQuery = string.Empty;
        ClearSearchState();
        SearchOpened?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void CopySelection()
    {
        if (HasSelection)
        {
            CopySelectionRequested?.Invoke(this, _selection.SelectedText);
        }
    }

    [RelayCommand]
    private async Task SelectAllTextAsync()
    {
        if (PageCount > MaxSelectAllPages)
        {
            ShowStatus(_localization.T("L.SelectAllTooLarge"));
            return;
        }

        // While every page is being parsed, a scroll must not release the pages that have
        // already arrived; SelectAll populates the selected-page set only at the end.
        _selectingAllText = true;
        try
        {
            await _selection.SelectAllAsync();
        }
        finally
        {
            _selectingAllText = false;
            TrimTextLayers();
        }
    }

    [RelayCommand]
    private void ShowPagesTab()
    {
        IsPagesTabSelected = true;
        IsContentsTabSelected = false;
        IsBookmarksTabSelected = false;
    }

    [RelayCommand]
    private void ShowContentsTab()
    {
        IsPagesTabSelected = false;
        IsContentsTabSelected = true;
        IsBookmarksTabSelected = false;
    }

    [RelayCommand]
    private void ShowBookmarksTab()
    {
        IsPagesTabSelected = false;
        IsContentsTabSelected = false;
        IsBookmarksTabSelected = true;
    }

    /// <summary>Reads the bookmarks stored for a document into the sidebar list.</summary>
    private void LoadBookmarks(string filePath)
    {
        var label = (Func<int, string>)(page => _localization.Format("L.BookmarkPageFormat", page));

        Bookmarks = string.IsNullOrWhiteSpace(filePath)
            ? []
            : _settingsService
                .GetBookmarks(_settings, filePath)
                .Select(entry => new UserBookmarkViewModel(entry, label))
                .ToList();
    }

    /// <summary>Bookmarks the current position, which is the page plus the scroll offset.</summary>
    [RelayCommand]
    private void AddBookmark()
    {
        if (!IsDocumentOpen || string.IsNullOrWhiteSpace(FilePath))
        {
            return;
        }

        var position = CurrentPosition;
        var label = _localization.Format("L.BookmarkPageFormat", position.Page);

        _settingsService.AddBookmark(_settings, FilePath, position.Page, position.PageOffset, label);
        PersistBookmarkChange();
    }

    [RelayCommand]
    private void RemoveBookmark(UserBookmarkViewModel? bookmark)
    {
        if (bookmark is null || !IsDocumentOpen)
        {
            return;
        }

        if (_settingsService.RemoveBookmark(_settings, bookmark.Entry))
        {
            PersistBookmarkChange();
        }
    }

    [RelayCommand]
    private void GoToBookmark(UserBookmarkViewModel? bookmark)
    {
        if (bookmark is null || !IsDocumentOpen)
        {
            return;
        }

        PushHistory();
        SetCurrentPage(bookmark.PageNumber);
        ReturnToPositionRequested?.Invoke(this, bookmark.Position);
    }

    /// <summary>Rebuilds the cross-document overview from the settings file.</summary>
    private void RefreshAllBookmarks()
    {
        var describe = (Func<string, int, string>)((fileName, page) =>
            _localization.Format("L.BookmarkInFile", fileName, page));

        AllBookmarks = _settingsService
            .GetAllBookmarks(_settings)
            .Select(entry => new BookmarkOverviewViewModel(entry, describe))
            .ToList();
    }

    /// <summary>
    /// Opens a bookmark from the overview: same file jumps straight there, another file is
    /// opened first and then scrolled.
    /// </summary>
    [RelayCommand]
    private async Task OpenBookmarkAsync(BookmarkOverviewViewModel? bookmark)
    {
        if (bookmark is null)
        {
            return;
        }

        var entry = bookmark.Entry;
        var sameFile = IsDocumentOpen
            && string.Equals(FilePath, entry.FilePath, StringComparison.OrdinalIgnoreCase);

        var position = new NavigationPoint(entry.Page, entry.PageOffset);

        // A document already open in another tab wins over opening a duplicate of it.
        if (!sameFile && _tabs is not null && _tabs.SelectTab(entry.FilePath) is { } openTab)
        {
            Dispatcher.UIThread.Post(
                () => openTab.JumpToPosition(position),
                DispatcherPriority.Loaded);
            return;
        }

        if (!sameFile)
        {
            // Hand the target to the open pass instead of scrolling afterwards: two competing
            // scrolls, one of them before the list has been measured, left the page blank.
            _pendingOpenPosition = position;
            await OpenPathAsync(entry.FilePath);
            if (!string.Equals(FilePath, entry.FilePath, StringComparison.OrdinalIgnoreCase))
            {
                _pendingOpenPosition = null;
                return;
            }

            SetCurrentPage(entry.Page);
            return;
        }

        PushHistory();
        SetCurrentPage(entry.Page);
        Dispatcher.UIThread.Post(
            () => ReturnToPositionRequested?.Invoke(this, position),
            DispatcherPriority.Loaded);
    }

    [RelayCommand]
    private async Task RenameBookmarkAsync(UserBookmarkViewModel? bookmark)
    {
        if (bookmark is null || !IsDocumentOpen)
        {
            return;
        }

        var title = _localization.T("L.RenameBookmark");
        var label = await _dialogs.PromptTextAsync(
            title,
            title,
            _localization.T("L.BookmarkNameLabel"),
            bookmark.Label);

        if (label is null || !_settingsService.RenameBookmark(_settings, bookmark.Entry, label))
        {
            return;
        }

        PersistBookmarkChange();
    }

    /// <summary>Moves a bookmark to a new index; used by drag and drop and the arrow commands.</summary>
    /// <summary>
    /// Turns the current text selection into highlight annotations.
    /// </summary>
    /// <remarks>
    /// The selection lives in normalized display space and annotations need page space, so every
    /// rectangle is mapped back with the page's own rotation, read on demand for just the pages
    /// the selection touches.
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanHighlightSelection))]
    private async Task HighlightSelectionAsync()
    {
        if (!IsDocumentOpen || !HasSelection)
        {
            return;
        }

        var added = new List<PdfHighlight>();

        try
        {
            foreach (var page in Pages.Where(candidate => candidate.SelectionRects.Count > 0).ToList())
            {
                var rotation = await _documentService.GetPageRotationAsync(FilePath, page.Number);
                var quads = page.SelectionRects
                    .Select(rect => PageGeometry.ToPageQuad(rect, page.WidthPoints, page.HeightPoints, rotation))
                    .Where(quad => quad.Width > 0 && quad.Height > 0)
                    .ToList();

                if (quads.Count == 0)
                {
                    continue;
                }

                if (HighlightPolicy == HighlightOverlapPolicy.Skip)
                {
                    var existing = await _documentService.GetHighlightsAsync(FilePath, page.Number);
                    quads = quads
                        .Where(quad => !existing.Any(highlight => highlight.Quads.Any(known => SameQuad(known, quad))))
                        .ToList();

                    if (quads.Count == 0)
                    {
                        continue;
                    }
                }

                await _documentService.AddHighlightAsync(
                    FilePath,
                    page.Number,
                    quads,
                    HighlightColor.Red,
                    HighlightColor.Green,
                    HighlightColor.Blue);

                added.Add(new PdfHighlight(page.Number, quads, HighlightColor.Red, HighlightColor.Green, HighlightColor.Blue));
                page.RequestRender();
            }

            if (added.Count == 0)
            {
                ShowStatus(_localization.T("L.AlreadyHighlighted"));
                return;
            }

            PushStep(HighlightAddedStep(added));
            HasUnsavedAnnotations = true;
            ShowStatus(_localization.T("L.HighlightAdded"));
        }
        catch (PdfDocumentException failure)
        {
            SetError(_localization.GetPdfFailureMessage(failure.Failure));
        }
        catch (Exception unexpected)
        {
            CrashLog.Write("HighlightSelection", unexpected);
            SetError(_localization.T("L.ErrorEditFailed"));
        }
    }

    /// <summary>Picks the colour the next highlight will use.</summary>
    public void SetHighlightColor(MarkupColor? color)
    {
        if (color is not null)
        {
            HighlightColor = color;
        }
    }

    /// <summary>Picks the colour the next stroke will use.</summary>
    public void SetInkColor(MarkupColor? color)
    {
        if (color is not null)
        {
            InkColor = color;
        }
    }

    /// <summary>
    /// The pen button picks a colour and starts drawing in one action: choosing a pen colour is
    /// how a reader says "I want to draw with this". It also leaves erasing, because a colour
    /// can only mean the pen: leaving the eraser on would keep rubbing markup out while the pen
    /// looked active.
    /// </summary>
    public void UseInkColor(MarkupColor? color)
    {
        SetInkColor(color);
        IsEraserMode = false;
        IsInkMode = true;
        ShowStatus(_localization.T("L.InkModeOn"));
    }

    /// <summary>Text of the note being shown, if any.</summary>
    [ObservableProperty]
    private string? _noteText;

    /// <summary>True when a right-click landed on markup, so the menu offers to remove it.</summary>
    [ObservableProperty]
    private bool _hasMarkupUnderPointer;

    /// <summary>True when the markup under the pointer is a note with text to read.</summary>
    [ObservableProperty]
    private bool _hasNoteUnderPointer;

    /// <summary>Text of the note under the pointer, if that is what was hit.</summary>
    private string? _noteUnderPointer;

    /// <summary>
    /// Works out what the pointer is over, so the context menu only offers what applies: remove an
    /// annotation when one is under it, show the note when the annotation is a note.
    /// </summary>
    public async Task UpdateContextTargetAsync(int pageNumber, double normalizedX, double normalizedY)
    {
        HasMarkupUnderPointer = false;
        HasNoteUnderPointer = false;
        _noteUnderPointer = null;

        if (!IsDocumentOpen)
        {
            return;
        }

        var page = Pages.FirstOrDefault(candidate => candidate.Number == pageNumber);
        if (page is null)
        {
            return;
        }

        try
        {
            var rotation = await GetRotationAsync(pageNumber);
            var point = PageGeometry.ToPageQuad(
                new PdfSearchRect(normalizedX, normalizedY, 0, 0),
                page.WidthPoints,
                page.HeightPoints,
                rotation);

            var markup = await GetMarkupAsync(pageNumber);
            var hit = markup.LastOrDefault(candidate => HitTest(candidate, point.Left, point.Top));
            if (hit is null)
            {
                return;
            }

            HasMarkupUnderPointer = true;
            if (hit.Kind == PdfMarkupKind.Note && !string.IsNullOrWhiteSpace(hit.Note?.Text))
            {
                HasNoteUnderPointer = true;
                _noteUnderPointer = hit.Note!.Text;
            }
        }
        catch (PdfDocumentException)
        {
            // A document that cannot be read has no markup to offer.
        }
        catch (ObjectDisposedException)
        {
            // The tab can close while the pointer hit test is waiting on the document.
        }
    }

    /// <summary>Shows the note the menu was opened on.</summary>
    public void ShowNoteUnderPointer()
    {
        if (!string.IsNullOrWhiteSpace(_noteUnderPointer))
        {
            NoteText = _noteUnderPointer;
        }
    }
    /// <summary>Text of the note the menu is offering to show.</summary>
    public string? NoteUnderPointer => _noteUnderPointer;

    /// <summary>Drops the cached markup after an edit.</summary>
    private void InvalidateMarkupCache() => _markupCache.Clear();

    private async Task<int> GetRotationAsync(int pageNumber)
    {
        if (_rotationCache.TryGetValue(pageNumber, out var cached))
        {
            return cached;
        }

        var rotation = await _documentService.GetPageRotationAsync(FilePath, pageNumber);
        _rotationCache[pageNumber] = rotation;
        return rotation;
    }

    private async Task<IReadOnlyList<PdfMarkup>> GetMarkupAsync(int pageNumber)
    {
        if (_markupCache.TryGetValue(pageNumber, out var cached))
        {
            return cached;
        }

        var markup = await _documentService.GetMarkupAsync(FilePath, pageNumber);
        _markupCache[pageNumber] = markup;
        return markup;
    }

    private static bool HitTest(PdfMarkup markup, double x, double y) => markup.Kind switch
    {
        PdfMarkupKind.Highlight => markup.Highlight!.Quads.Any(quad =>
            x >= quad.Left - 0.5 && x <= quad.Right + 0.5 && y >= quad.Bottom - 0.5 && y <= quad.Top + 0.5),
        PdfMarkupKind.Ink => InkContains(markup.Ink!, x, y),
        PdfMarkupKind.Note => x >= markup.Note!.Position.X - 1 && x <= markup.Note.Position.X + 24 &&
                              y >= markup.Note.Position.Y - 1 && y <= markup.Note.Position.Y + 24,
        _ => false
    };

    private static bool InkContains(PdfInk ink, double x, double y)
    {
        // A stroke is a polyline: the hit area is the line, not its vertices. Comparing only the
        // vertices means the middle of a long stroke cannot be clicked.
        const double slack = 6;

        foreach (var stroke in ink.Strokes)
        {
            var points = stroke.Points;
            if (points.Count == 0)
            {
                continue;
            }

            if (points.Count == 1)
            {
                if (DistanceToPoint(x, y, points[0].X, points[0].Y) <= slack)
                {
                    return true;
                }

                continue;
            }

            for (var index = 1; index < points.Count; index++)
            {
                if (DistanceToSegment(x, y, points[index - 1], points[index]) <= slack)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static double DistanceToPoint(double x, double y, double px, double py) =>
        Math.Sqrt((x - px) * (x - px) + (y - py) * (y - py));

    private static double DistanceToSegment(double x, double y, PdfPoint start, PdfPoint end)
    {
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        var lengthSquared = dx * dx + dy * dy;

        if (lengthSquared < 1e-9)
        {
            return DistanceToPoint(x, y, start.X, start.Y);
        }

        var t = Math.Clamp(((x - start.X) * dx + (y - start.Y) * dy) / lengthSquared, 0, 1);
        return DistanceToPoint(x, y, start.X + t * dx, start.Y + t * dy);
    }

    /// <summary>True when a note's text is worth showing.</summary>
    public bool HasNoteText => !string.IsNullOrWhiteSpace(NoteText);

    /// <summary>Closes the note popup.</summary>
    public void ClearNoteText() => NoteText = null;
    /// <summary>Label on the pen flyout, which doubles as the way out of drawing mode.</summary>
    public string InkModeLabel => _localization.T(IsInkMode ? "L.StopDrawing" : "L.StartDrawing");
    /// <summary>Turns freehand drawing on or off. Turning it on always leaves erasing.</summary>
    [RelayCommand]
    private void ToggleInkMode()
    {
        if (!IsDocumentOpen)
        {
            return;
        }

        if (IsInkMode)
        {
            IsInkMode = false;
        }
        else
        {
            IsEraserMode = false;
            IsInkMode = true;
        }

        ShowStatus(_localization.T(IsInkMode ? "L.InkModeOn" : "L.InkModeOff"));
    }

    /// <summary>Turns the eraser on or off. Turning it on always leaves drawing.</summary>
    [RelayCommand]
    private void ToggleEraserMode()
    {
        if (!IsDocumentOpen)
        {
            return;
        }

        if (IsEraserMode)
        {
            IsEraserMode = false;
        }
        else
        {
            IsInkMode = false;
            IsEraserMode = true;
        }

        ShowStatus(_localization.T(IsEraserMode ? "L.EraserModeOn" : "L.EraserModeOff"));
    }

    /// <summary>Starts collecting erasures, so a drag becomes one undo step.</summary>
    public void BeginErase()
    {
        _erased.Clear();
    }

    /// <summary>Rubs out whatever markup sits under a point on the page.</summary>
    public async Task<bool> EraseAtAsync(int pageNumber, double normalizedX, double normalizedY)
    {
        if (!IsDocumentOpen || !IsEraserMode)
        {
            return false;
        }

        var page = Pages.FirstOrDefault(candidate => candidate.Number == pageNumber);
        if (page is null)
        {
            return false;
        }

        try
        {
            var rotation = await GetRotationAsync(pageNumber);
            var point = PageGeometry.ToPageQuad(
                new PdfSearchRect(normalizedX, normalizedY, 0, 0),
                page.WidthPoints,
                page.HeightPoints,
                rotation);

            var removed = await _documentService.RemoveMarkupAtAsync(FilePath, pageNumber, point.Left, point.Top);
            if (removed.Count == 0)
            {
                return false;
            }

            _erased.AddRange(removed);
            page.RequestRender();
            HasUnsavedAnnotations = true;
            return true;
        }
        catch (PdfDocumentException failure)
        {
            SetError(_localization.GetPdfFailureMessage(failure.Failure));
            return false;
        }
    }

    /// <summary>Closes an erase drag, pushing one undo step for everything it removed.</summary>
    public void EndErase()
    {
        if (_erased.Count == 0)
        {
            return;
        }

        var removed = _erased.ToList();
        _erased.Clear();
        PushStep(MarkupRemovedStep(removed));
        ShowStatus(string.Format(_localization.T("L.Erased"), removed.Count));
    }

    /// <summary>
    /// Commits one freehand stroke. The points arrive in normalized display coordinates, as the
    /// pointer reports them, and are mapped back to page space with the page's own rotation.
    /// </summary>
    public async Task<bool> AddInkAtAsync(int pageNumber, IReadOnlyList<(double X, double Y)> points)
    {
        if (!IsDocumentOpen || points.Count < 2)
        {
            return false;
        }

        var page = Pages.FirstOrDefault(candidate => candidate.Number == pageNumber);
        if (page is null)
        {
            return false;
        }

        try
        {
            var rotation = await _documentService.GetPageRotationAsync(FilePath, pageNumber);
            var converted = points
                .Select(point => PageGeometry.ToPageQuad(
                    new PdfSearchRect(point.X, point.Y, 0, 0),
                    page.WidthPoints,
                    page.HeightPoints,
                    rotation))
                .Select(quad => new PdfPoint(quad.Left, quad.Top))
                .ToList();

            var ink = new PdfInk(
                pageNumber,
                [new PdfInkStroke(converted)],
                InkColor.Red,
                InkColor.Green,
                InkColor.Blue,
                (float)Math.Clamp(InkWidth, 1, 10));

            await AddInkAsync(ink);
            PushStep(InkAddedStep(ink));
            HasUnsavedAnnotations = true;
            ShowStatus(_localization.T("L.InkAdded"));
            return true;
        }
        catch (PdfDocumentException failure)
        {
            SetError(_localization.GetPdfFailureMessage(failure.Failure));
            return false;
        }
        catch (Exception unexpected)
        {
            CrashLog.Write("AddInk", unexpected);
            SetError(_localization.T("L.ErrorEditFailed"));
            return false;
        }
    }

    /// <summary>Asks for the text of a sticky note and places it at a point on the page.</summary>
    public async Task<bool> AddNoteAtAsync(int pageNumber, double normalizedX, double normalizedY)
    {
        if (!IsDocumentOpen)
        {
            return false;
        }

        var page = Pages.FirstOrDefault(candidate => candidate.Number == pageNumber);
        if (page is null)
        {
            return false;
        }

        var title = _localization.T("L.AddNote");
        var text = await _dialogs.PromptTextAsync(
            title,
            title,
            _localization.T("L.NoteTextLabel"),
            string.Empty);

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        try
        {
            var rotation = await _documentService.GetPageRotationAsync(FilePath, pageNumber);
            var point = PageGeometry.ToPageQuad(
                new PdfSearchRect(normalizedX, normalizedY, 0, 0),
                page.WidthPoints,
                page.HeightPoints,
                rotation);

            var note = new PdfNote(pageNumber, new PdfPoint(point.Left, point.Top), text.Trim());
            await AddNoteAsync(note);
            PushStep(NoteAddedStep(note));
            HasUnsavedAnnotations = true;
            ShowStatus(_localization.T("L.NoteAdded"));
            return true;
        }
        catch (PdfDocumentException failure)
        {
            SetError(_localization.GetPdfFailureMessage(failure.Failure));
            return false;
        }
        catch (Exception unexpected)
        {
            CrashLog.Write("AddNote", unexpected);
            SetError(_localization.T("L.ErrorEditFailed"));
            return false;
        }
    }
    private bool CanHighlightSelection() => IsDocumentOpen && HasSelection && !IsBusy;

    /// <summary>Writes the document, annotations included, to a file the reader chooses.</summary>
    [RelayCommand(CanExecute = nameof(CanSaveCopy))]
    private async Task SaveCopyAsync()
    {
        if (!IsDocumentOpen)
        {
            return;
        }

        var suggested = $"{Path.GetFileNameWithoutExtension(FileName)} - annotated.pdf";
        var destination = await _dialogs.PickSavePathAsync(suggested);
        if (string.IsNullOrWhiteSpace(destination))
        {
            return;
        }

        try
        {
            await _documentService.SaveCopyAsync(FilePath, destination);
            HasUnsavedAnnotations = false;
            ShowStatus(_localization.Format("L.SavedAs", Path.GetFileName(destination)));
        }
        catch (PdfDocumentException failure)
        {
            SetError(failure.Failure == PdfFailure.AccessDenied
                ? _localization.T("L.ErrorSaveDenied")
                : _localization.GetPdfFailureMessage(failure.Failure));
        }
        catch (IOException)
        {
            SetError(_localization.T("L.ErrorSaveFailed"));
        }
        catch (Exception unexpected)
        {
            CrashLog.Write("SaveDocument", unexpected);
            SetError(_localization.T("L.ErrorEditFailed"));
        }
    }

    /// <summary>
    /// Removes the highlights under a point, given in normalized display coordinates as the
    /// pointer reports them.
    /// </summary>
    /// <remarks>
    /// The point goes through the same inverse mapping as a selection, so a highlight on a
    /// rotated page is removed by clicking where it is drawn rather than where it would have
    /// been without rotation.
    /// </remarks>
    /// <summary>
    /// Removes whatever markup sits under the point: a highlight, a stroke or a note. One action
    /// and one undo step, because the reader pointed at one thing.
    /// </summary>
    public async Task<bool> RemoveMarkupAtAsync(int pageNumber, double normalizedX, double normalizedY)
    {
        if (!IsDocumentOpen)
        {
            return false;
        }

        var page = Pages.FirstOrDefault(candidate => candidate.Number == pageNumber);
        if (page is null)
        {
            return false;
        }

        try
        {
            var rotation = await _documentService.GetPageRotationAsync(FilePath, pageNumber);
            var point = PageGeometry.ToPageQuad(
                new PdfSearchRect(normalizedX, normalizedY, 0, 0),
                page.WidthPoints,
                page.HeightPoints,
                rotation);

            var removed = await _documentService.RemoveMarkupAtAsync(FilePath, pageNumber, point.Left, point.Top);
            if (removed.Count == 0)
            {
                ShowStatus(_localization.T("L.NoMarkupHere"));
                return false;
            }

            PushStep(MarkupRemovedStep(removed));

            page.RequestRender();
            HasUnsavedAnnotations = true;
            ShowStatus(_localization.T("L.MarkupRemoved"));
            return true;
        }
        catch (PdfDocumentException failure)
        {
            SetError(_localization.GetPdfFailureMessage(failure.Failure));
            return false;
        }
        catch (Exception unexpected)
        {
            CrashLog.Write("RemoveMarkup", unexpected);
            SetError(_localization.T("L.ErrorEditFailed"));
            return false;
        }
    }

    /// <summary>
    /// Shows the text of the note under a point, if there is one. Notes are annotations rather
    /// than part of the page, so nothing on the page itself can be clicked to read them.
    /// </summary>
    public async Task<bool> TryShowNoteAtAsync(int pageNumber, double normalizedX, double normalizedY)
    {
        if (!IsDocumentOpen)
        {
            return false;
        }

        var page = Pages.FirstOrDefault(candidate => candidate.Number == pageNumber);
        if (page is null)
        {
            return false;
        }

        try
        {
            var rotation = await _documentService.GetPageRotationAsync(FilePath, pageNumber);
            var point = PageGeometry.ToPageQuad(
                new PdfSearchRect(normalizedX, normalizedY, 0, 0),
                page.WidthPoints,
                page.HeightPoints,
                rotation);

            var notes = await _documentService.GetNotesAsync(FilePath, pageNumber);
            var hit = notes.FirstOrDefault(note =>
                point.Left >= note.Position.X - 1 && point.Left <= note.Position.X + 24 &&
                point.Top >= note.Position.Y - 1 && point.Top <= note.Position.Y + 24);

            if (hit is null || string.IsNullOrWhiteSpace(hit.Text))
            {
                return false;
            }

            NoteText = hit.Text;
            return true;
        }
        catch (PdfDocumentException)
        {
            return false;
        }
        catch (ObjectDisposedException)
        {
            // The tab can close between the press and this asynchronous hit test.
            return false;
        }
    }

    /// <summary>True when two quads cover the same area, within rounding.</summary>
    private static bool SameQuad(PdfQuad left, PdfQuad right) =>
        Math.Abs(left.Left - right.Left) < 0.5 &&
        Math.Abs(left.Top - right.Top) < 0.5 &&
        Math.Abs(left.Right - right.Right) < 0.5 &&
        Math.Abs(left.Bottom - right.Bottom) < 0.5;
    /// <summary>True when there is an annotation edit to take back.</summary>
    public bool CanUndoAnnotation => _annotationUndo.Count > 0;

    /// <summary>True when there is a taken-back edit that can be applied again.</summary>
    public bool CanRedoAnnotation => _annotationRedo.Count > 0;

    /// <summary>Edits that can still be undone; at most <see cref="MaxUndoSteps"/>.</summary>
    public int UndoDepth => _annotationUndo.Count;

    /// <summary>Edits that can be redone; at most <see cref="MaxUndoSteps"/>.</summary>
    public int RedoDepth => _annotationRedo.Count;

    /// <summary>Takes back the last markup edit, whatever kind it was.</summary>
    [RelayCommand(CanExecute = nameof(CanUndoAnnotation))]
    private async Task UndoAnnotationAsync()
    {
        if (!IsDocumentOpen || _annotationUndo.Count == 0)
        {
            return;
        }

        var step = _annotationUndo[^1];
        _annotationUndo.RemoveAt(_annotationUndo.Count - 1);
        _annotationRedo.Add(step);
        InvalidateMarkupCache();
        NotifyUndoChanged();

        await RunUndoStepAsync(step.Undo, "L.Undone");
    }

    [RelayCommand(CanExecute = nameof(CanRedoAnnotation))]
    private async Task RedoAnnotationAsync()
    {
        if (!IsDocumentOpen || _annotationRedo.Count == 0)
        {
            return;
        }

        var step = _annotationRedo[^1];
        _annotationRedo.RemoveAt(_annotationRedo.Count - 1);
        _annotationUndo.Add(step);
        InvalidateMarkupCache();
        NotifyUndoChanged();

        await RunUndoStepAsync(step.Redo, "L.Redone");
    }

    private async Task RunUndoStepAsync(Func<Task> action, string statusKey)
    {
        try
        {
            await action();
            HasUnsavedAnnotations = true;
            ShowStatus(_localization.T(statusKey));
        }
        catch (PdfDocumentException failure)
        {
            SetError(_localization.GetPdfFailureMessage(failure.Failure));
        }
        catch (Exception unexpected)
        {
            CrashLog.Write("UndoStep", unexpected);
            SetError(_localization.T("L.ErrorEditFailed"));
        }
    }

    /// <summary>Records an edit and drops the redo trail, as every editor does.</summary>
    private void PushStep(UndoStep step)
    {
        InvalidateMarkupCache();

        if (_annotationUndo.Count >= MaxUndoSteps)
        {
            _annotationUndo.RemoveAt(0);
        }

        _annotationUndo.Add(step);
        _annotationRedo.Clear();
        NotifyUndoChanged();
    }

    private UndoStep HighlightAddedStep(IReadOnlyList<PdfHighlight> highlights) => new(
        Undo: async () =>
        {
            foreach (var highlight in highlights)
            {
                await RemoveHighlightAsync(highlight);
            }
        },
        Redo: async () =>
        {
            foreach (var highlight in highlights)
            {
                await AddHighlightAsync(highlight);
            }
        });

    private UndoStep HighlightRemovedStep(IReadOnlyList<PdfHighlight> highlights) => new(
        Undo: async () =>
        {
            foreach (var highlight in highlights)
            {
                await AddHighlightAsync(highlight);
            }
        },
        Redo: async () =>
        {
            foreach (var highlight in highlights)
            {
                await RemoveHighlightAsync(highlight);
            }
        });

    private UndoStep InkAddedStep(PdfInk ink) => new(
        Undo: async () =>
        {
            await _documentService.RemoveInkMatchingAsync(FilePath, ink);
            RenderPage(ink.PageNumber);
        },
        Redo: async () => await AddInkAsync(ink));

    /// <summary>Undo for a removal: each markup is added back the way it was taken out.</summary>
    private UndoStep MarkupRemovedStep(IReadOnlyList<PdfMarkup> removed) => new(
        Undo: async () =>
        {
            foreach (var markup in removed)
            {
                await RestoreMarkupAsync(markup);
            }
        },
        Redo: async () =>
        {
            foreach (var markup in removed)
            {
                await RemoveMarkupAsync(markup);
            }
        });

    private async Task RestoreMarkupAsync(PdfMarkup markup)
    {
        switch (markup.Kind)
        {
            case PdfMarkupKind.Highlight when markup.Highlight is { } highlight:
                await AddHighlightAsync(highlight);
                break;
            case PdfMarkupKind.Ink when markup.Ink is { } ink:
                await AddInkAsync(ink);
                break;
            case PdfMarkupKind.Note when markup.Note is { } note:
                await AddNoteAsync(note);
                break;
        }
    }

    private async Task RemoveMarkupAsync(PdfMarkup markup)
    {
        switch (markup.Kind)
        {
            case PdfMarkupKind.Highlight when markup.Highlight is { } highlight:
                await RemoveHighlightAsync(highlight);
                break;
            case PdfMarkupKind.Ink when markup.Ink is { } ink:
                await _documentService.RemoveInkMatchingAsync(FilePath, ink);
                RenderPage(ink.PageNumber);
                break;
            case PdfMarkupKind.Note when markup.Note is { } note:
                await _documentService.RemoveNoteMatchingAsync(FilePath, note);
                RenderPage(note.PageNumber);
                break;
        }
    }
    private UndoStep NoteAddedStep(PdfNote note) => new(
        Undo: async () =>
        {
            await _documentService.RemoveNoteMatchingAsync(FilePath, note);
            RenderPage(note.PageNumber);
        },
        Redo: async () => await AddNoteAsync(note));

    private async Task AddHighlightAsync(PdfHighlight highlight)
    {
        await _documentService.AddHighlightAsync(
            FilePath,
            highlight.PageNumber,
            highlight.Quads,
            highlight.Red,
            highlight.Green,
            highlight.Blue);

        RenderPage(highlight.PageNumber);
    }

    private async Task RemoveHighlightAsync(PdfHighlight highlight)
    {
        await _documentService.RemoveHighlightMatchingAsync(FilePath, highlight.PageNumber, highlight.Quads);
        RenderPage(highlight.PageNumber);
    }

    private async Task AddInkAsync(PdfInk ink)
    {

        await _documentService.AddInkAsync(
            FilePath,
            ink.PageNumber,
            ink.Strokes,
            ink.Red,
            ink.Green,
            ink.Blue,
            ink.Width);

        RenderPage(ink.PageNumber);
    }

    private async Task AddNoteAsync(PdfNote note)
    {
        await _documentService.AddNoteAsync(FilePath, note.PageNumber, note.Position, note.Text);
        RenderPage(note.PageNumber);
    }

    private void RenderPage(int pageNumber) =>
        Pages.FirstOrDefault(page => page.Number == pageNumber)?.RequestRender();
    private void ClearUndo()
    {
        _annotationUndo.Clear();
        _annotationRedo.Clear();
        NotifyUndoChanged();
    }

    private void NotifyUndoChanged()
    {
        OnPropertyChanged(nameof(CanUndoAnnotation));
        OnPropertyChanged(nameof(CanRedoAnnotation));
        OnPropertyChanged(nameof(UndoDepth));
        OnPropertyChanged(nameof(RedoDepth));
        UndoAnnotationCommand.NotifyCanExecuteChanged();
        RedoAnnotationCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Writes the document back over the file it came from.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSaveCopy))]
    private async Task SaveDocumentAsync()
    {
        if (!IsDocumentOpen)
        {
            return;
        }

        try
        {
            await _documentService.SaveInPlaceAsync(FilePath);
            HasUnsavedAnnotations = false;
            ClearError();
            ShowStatus(_localization.T("L.Saved"));
        }
        catch (PdfDocumentException failure)
        {
            if (failure.Failure == PdfFailure.AccessDenied)
            {
                SetSaveFailure("L.ErrorSaveDenied");
            }
            else
            {
                SetError(_localization.GetPdfFailureMessage(failure.Failure));
            }
        }
        catch (IOException)
        {
            SetSaveFailure("L.ErrorSaveFailed");
        }
        catch (UnauthorizedAccessException)
        {
            // Another tab or window in this process still has the file open, or another
            // program locks it. The replace cannot land until that handle is gone.
            SetSaveFailure("L.ErrorSaveInUse");
        }
        catch (Exception unexpected)
        {
            CrashLog.Write("SaveCopy", unexpected);
            SetSaveFailure("L.ErrorEditFailed");
        }
    }

    /// <summary>
    /// Reports a failed save. When the service preserved the saved copy, the message names
    /// that file so the reader can retry saving or open the recovered document instead of
    /// losing the annotations.
    /// </summary>
    private void SetSaveFailure(string fallbackKey)
    {
        if (_documentService.RecoveryPath is { } recovery)
        {
            _errorKey = null;
            ErrorMessage = _localization.Format("L.ErrorSaveRecovered", recovery);
            return;
        }

        SetError(fallbackKey);
    }

    /// <summary>
    /// Returns true when the document can be closed or replaced. A document with unsaved
    /// annotations asks first; Save must actually succeed, or the tab stays open.
    /// </summary>
    public async Task<bool> ConfirmCloseAsync()
    {
        if (!HasUnsavedAnnotations)
        {
            return true;
        }

        var choice = await _dialogs.ConfirmUnsavedChangesAsync(FileName);
        switch (choice)
        {
            case UnsavedChangesChoice.Save:
                await SaveDocumentAsync();
                return !HasUnsavedAnnotations;
            case UnsavedChangesChoice.Discard:
                return true;
            default:
                return false;
        }
    }

    private bool CanSaveCopy() => IsDocumentOpen;

    private void ShowStatus(string message)
    {
        StatusMessage = message;

        // Long enough to read, short enough not to sit there. A posted callback would clear it
        // on the next idle turn, which is one frame.
        DispatcherTimer.RunOnce(
            () =>
            {
                if (string.Equals(StatusMessage, message, StringComparison.Ordinal))
                {
                    StatusMessage = string.Empty;
                }
            },
            TimeSpan.FromSeconds(6));
    }

    /// <summary>Marks the title while annotations exist only in memory.</summary>
    private void UpdateTitleMarker()
    {
        if (!IsDocumentOpen)
        {
            WindowTitle = "AirPDF";
            return;
        }

        var title = $"{FileName} — AirPDF";
        WindowTitle = HasUnsavedAnnotations ? $"• {title}" : title;
    }
    /// <summary>Jumps to a position and records a back step; used when another tab asks.</summary>
    public void JumpToPosition(NavigationPoint position)
    {
        if (!IsDocumentOpen)
        {
            return;
        }

        SetCurrentPage(position.Page);
        ReturnToPositionRequested?.Invoke(this, position);
    }
    public void MoveBookmarkTo(UserBookmarkViewModel? bookmark, int targetIndex)
    {
        if (bookmark is null || !IsDocumentOpen)
        {
            return;
        }

        if (_settingsService.MoveBookmark(_settings, FilePath, bookmark.Entry, targetIndex))
        {
            PersistBookmarkChange();
        }
    }

    [RelayCommand]
    private void MoveBookmarkUp(UserBookmarkViewModel? bookmark) => MoveBookmarkBy(bookmark, -1);

    [RelayCommand]
    private void MoveBookmarkDown(UserBookmarkViewModel? bookmark) => MoveBookmarkBy(bookmark, 1);

    private void MoveBookmarkBy(UserBookmarkViewModel? bookmark, int delta)
    {
        if (bookmark is null)
        {
            return;
        }

        // Compare the stored entry, not the view model: reloading the list after a move
        // builds new view models around the same entries.
        var index = -1;
        for (var i = 0; i < Bookmarks.Count; i++)
        {
            if (ReferenceEquals(Bookmarks[i].Entry, bookmark.Entry))
            {
                index = i;
                break;
            }
        }
        if (index >= 0)
        {
            MoveBookmarkTo(bookmark, index + delta);
        }
    }

    /// <summary>Saves, reloads, and tells the other windows that bookmarks changed.</summary>
    private void PersistBookmarkChange()
    {
        _settingsService.Save(_settings);
        LoadBookmarks(FilePath);
        RefreshAllBookmarks();
        _services.NotifyBookmarksChanged();
    }

    [RelayCommand]
    private void PreviousPage() => NavigateToPage(CurrentPage - 1);

    [RelayCommand]
    private void NextPage() => NavigateToPage(CurrentPage + 1);

    [RelayCommand]
    private void GoToPageInput()
    {
        if (int.TryParse(PageNumberInput, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
        {
            NavigateToPage(number);
        }
        else
        {
            PageNumberInput = CurrentPage.ToString(CultureInfo.InvariantCulture);
        }
    }

    [RelayCommand]
    private void ZoomIn()
    {
        var next = ZoomSteps.FirstOrDefault(step => step > _zoom + 0.001, 4.0);
        _zoomMode = ZoomModes.Manual;
        Zoom = next;
    }

    [RelayCommand]
    private void ZoomOut()
    {
        var previous = ZoomSteps.LastOrDefault(step => step < _zoom - 0.001, 0.25);
        _zoomMode = ZoomModes.Manual;
        Zoom = previous;
    }

    [RelayCommand]
    private void SetZoom(string? value)
    {
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var numeric))
        {
            return;
        }

        if (numeric > 8)
        {
            numeric /= 100.0;
        }

        _zoomMode = ZoomModes.Manual;
        Zoom = numeric;
    }

    [RelayCommand]
    private void ActualSize()
    {
        _zoomMode = ZoomModes.Manual;
        Zoom = 1.0;
    }

    [RelayCommand]
    private void FitWidth() => FitWidthToViewport();

    [RelayCommand]
    private void FitPage() => FitPageToViewport();

    [RelayCommand]
    private async Task SearchAsync()
    {
        if (!IsDocumentOpen || _document is null)
        {
            return;
        }

        var query = SearchQuery.Trim();
        if (query.Length == 0)
        {
            ClearSearchState();
            return;
        }

        CancelSafely(_searchCancellation);
        var cancellation = new CancellationTokenSource();
        _searchCancellation = cancellation;

        IsSearching = true;
        HasSearched = true;
        HasSearchResults = false;
        SearchResultsTruncated = false;
        _searchFailed = false;
        SetSearchSummary();
        SearchResults = [];
        SelectedSearchResult = null;
        ClearAllHighlights();
        OnPropertyChanged(nameof(HasNoSearchResults));

        var progress = new Progress<PdfSearchProgress>(value =>
        {
            if (!IsSearching ||
                cancellation.IsCancellationRequested ||
                !ReferenceEquals(_searchCancellation, cancellation))
            {
                return;
            }

            SearchSummary = _localization.Format(
                "L.SearchProgressFormat",
                value.PagesScanned,
                value.PageCount);
        });

        try
        {
            var hits = await _searchService.SearchAsync(
                _document.FilePath,
                query,
                _password,
                cancellation.Token,
                progress);
            if (cancellation.IsCancellationRequested)
            {
                return;
            }

            var results = hits
                .Select((hit, index) => new SearchResultViewModel(hit, index + 1, _localization))
                .ToList();

            SearchResultsTruncated = results.Count >= 250;
            SearchResults = results;
            HasSearchResults = results.Count > 0;
            OnPropertyChanged(nameof(HasNoSearchResults));
            SetSearchSummary();

            if (results.Count > 0)
            {
                SelectedSearchResult = results[0];
            }
        }
        catch (OperationCanceledException)
        {
            // A newer search superseded this one.
        }
        catch (PdfDocumentException ex)
        {
            _searchFailed = true;
            SearchSummary = _localization.T("L.SearchFailed");
            SetErrorFromFailure(ex.Failure);
        }
        catch
        {
            _searchFailed = true;
            SearchSummary = _localization.T("L.SearchFailed");
            SetError("L.SearchError");
        }
        finally
        {
            if (ReferenceEquals(_searchCancellation, cancellation))
            {
                IsSearching = false;
                _searchCancellation = null;
                SetSearchSummary();
                OnPropertyChanged(nameof(HasNoSearchResults));
            }

            cancellation.Dispose();
        }
    }

    [RelayCommand]
    private void NextSearchResult()
    {
        if (SearchResults.Count == 0)
        {
            return;
        }

        var index = SelectedSearchResult is null ? -1 : SelectedSearchResult.Index - 1;
        SelectedSearchResult = SearchResults[(index + 1) % SearchResults.Count];
    }

    [RelayCommand]
    private void PreviousSearchResult()
    {
        if (SearchResults.Count == 0)
        {
            return;
        }

        var index = SelectedSearchResult is null ? 0 : SelectedSearchResult.Index - 1;
        SelectedSearchResult = SearchResults[(index - 1 + SearchResults.Count) % SearchResults.Count];
    }

    [RelayCommand]
    private void DismissError() => ClearError();

    [RelayCommand]
    private async Task CheckForUpdates() => await CheckForUpdatesAsync(userInitiated: true);

    [RelayCommand]
    private async Task ApplyUpdateAsync()
    {
        if (_availableUpdate is null)
        {
            await CheckForUpdatesAsync(userInitiated: true);
            return;
        }

        if (!IsUpdateReady)
        {
            await DownloadUpdateAsync(_availableUpdate, userInitiated: true);
            if (!IsUpdateReady)
            {
                return;
            }
        }

        SaveReadingPositionNow();
        _updateService.ApplyAndRestart(_availableUpdate);
    }

    private async Task CheckForUpdatesAsync(bool userInitiated)
    {
        if (!_updateService.IsInstalled)
        {
            if (userInitiated)
            {
                SetError("L.UpdateNotInstalled");
            }

            return;
        }

        try
        {
            if (!userInitiated)
            {
                await Task.Delay(2500);
            }

            var update = await _updateService.CheckForUpdatesAsync();
            if (update is null)
            {
                return;
            }

            _availableUpdate = update;
            UpdateVersion = update.TargetFullRelease.Version.ToString();
            HasUpdate = true;
            IsUpdateReady = false;
            OnPropertyChanged(nameof(UpdateButtonText));

            await DownloadUpdateAsync(update, userInitiated: false);
        }
        catch (NotInstalledException)
        {
            // Portable/source builds do not participate in auto-updates.
        }
        catch
        {
            if (userInitiated)
            {
                SetError("L.UpdateError");
            }
        }
    }

    private async Task DownloadUpdateAsync(UpdateInfo update, bool userInitiated)
    {
        if (IsUpdating)
        {
            return;
        }

        IsUpdating = true;
        UpdateProgress = 0;
        OnPropertyChanged(nameof(UpdateButtonText));

        try
        {
            await _updateService.DownloadAsync(
                update,
                progress => Dispatcher.UIThread.Post(() =>
                {
                    UpdateProgress = Math.Clamp(progress, 0, 100);
                    OnPropertyChanged(nameof(UpdateButtonText));
                }),
                CancellationToken.None);

            IsUpdateReady = true;
        }
        catch
        {
            if (userInitiated)
            {
                SetError("L.UpdateError");
            }
        }
        finally
        {
            IsUpdating = false;
            OnPropertyChanged(nameof(UpdateButtonText));
        }
    }

    public async Task OpenPathAsync(string filePath, bool recordRecent = true)
    {
        // A closed tab must not be able to load a document again: its document service is
        // already disposed, so the pages would be created without a renderer behind them.
        if (_disposed || string.IsNullOrWhiteSpace(filePath))
        {
            return;
        }

        // Home is a place: opening from it must not turn the welcome screen into a document.
        if (IsHomeTab && _tabs is not null)
        {
            _tabs.OpenDocument(filePath);
            return;
        }

        if (IsDocumentOpen && !await ConfirmCloseAsync())
        {
            return;
        }

        OpeningPath = filePath;
        SaveReadingPositionNow();
        CancelSafely(_loadCancellation);
        var cancellation = new CancellationTokenSource();
        _loadCancellation = cancellation;

        IsBusy = true;
        _busyFileName = Path.GetFileName(filePath);
        BusyText = _localization.Format("L.BusyOpening", _busyFileName);
        ClearError();
        OnPropertyChanged(nameof(IsWelcomeVisible));

        try
        {
            string? password = null;
            PdfDocumentInfo? info = null;

            for (var attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    info = await _documentService.OpenAsync(filePath, password, cancellation.Token);
                    break;
                }
                catch (PdfDocumentException ex) when (
                    ex.Failure is PdfFailure.PasswordRequired or PdfFailure.InvalidPassword &&
                    !cancellation.IsCancellationRequested)
                {
                    password = await _dialogs.PromptPasswordAsync(Path.GetFileName(filePath), attempt > 0);
                    if (password is null)
                    {
                        return;
                    }
                }
            }

            if (info is null)
            {
                SetError("L.ErrorInvalidPassword");
                return;
            }

            cancellation.Token.ThrowIfCancellationRequested();

            // The tab may have been closed while the document was parsing.
            if (_disposed)
            {
                return;
            }

            LoadDocument(info, password);

            if (recordRecent)
            {
                AddRecentFile(info.FilePath);
            }

            DocumentOpened?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException)
        {
            // Another document was opened while this one was loading.
        }
        catch (PdfDocumentException ex)
        {
            SetErrorFromFailure(ex.Failure);
        }
        catch (Exception)
        {
            SetError("L.OpenError");
            _ = _dialogs.ShowErrorAsync(_localization.T("L.CouldNotOpen"), _localization.T("L.OpenError"));
        }
        finally
        {
            if (ReferenceEquals(_loadCancellation, cancellation))
            {
                IsBusy = false;
                _busyFileName = null;
                _loadCancellation = null;
                OpeningPath = null;
                OnPropertyChanged(nameof(IsWelcomeVisible));
            }

            cancellation.Dispose();
        }
    }

    public void UpdateViewport(double width, double height)
    {
        ViewportWidth = width;
        ViewportHeight = height;
    }

    public void FitWidthToViewport()
    {
        if (Pages.Count == 0)
        {
            return;
        }

        var available = Math.Max(180, ViewportWidth - 72);
        _zoomMode = ZoomModes.FitWidth;
        Zoom = available / (Pages[0].WidthPoints * PageViewModel.PixelsPerPointAt100);
    }

    public void FitPageToViewport()
    {
        if (Pages.Count == 0)
        {
            return;
        }

        var page = Pages[0];
        var byWidth = Math.Max(180, ViewportWidth - 80) / (page.WidthPoints * PageViewModel.PixelsPerPointAt100);
        var byHeight = Math.Max(180, ViewportHeight - 90) / (page.HeightPoints * PageViewModel.PixelsPerPointAt100);
        _zoomMode = ZoomModes.FitPage;
        Zoom = Math.Min(byWidth, byHeight);
    }

    /// <summary>
    /// Applies the zoom that was remembered for this document. Falls back to
    /// fit-width for documents that have never been opened before.
    /// </summary>
    public void ApplyInitialZoom()
    {
        switch (_restoreZoomMode)
        {
            case ZoomModes.FitPage:
                FitPageToViewport();
                break;
            case ZoomModes.Manual when _restoreZoom is { } zoom:
                _zoomMode = ZoomModes.Manual;
                Zoom = zoom;
                break;
            default:
                FitWidthToViewport();
                break;
        }

        _restoreZoom = null;
        _restoreZoomMode = null;
    }

    public void NavigateToPage(int pageNumber)
    {
        if (!IsDocumentOpen || PageCount == 0)
        {
            return;
        }

        PushHistory();
        SetCurrentPage(pageNumber);
        ScrollToPageRequested?.Invoke(this, _currentPage);
    }

    public void UpdateCurrentPageFromScroll(int pageNumber, double pageOffset = 0)
    {
        if (!IsDocumentOpen)
        {
            return;
        }

        _reading.UpdateOffset(pageOffset);
        SetCurrentPage(pageNumber);

        // The offset changes on every scroll tick, even when the page index does not,
        // so the progress read-out must be refreshed here rather than in SetCurrentPage.
        OnPropertyChanged(nameof(ReadingProgress));
        OnPropertyChanged(nameof(ReadingProgressText));

        _reading.ScheduleSave();
    }

    public ReadingPositionEntry? TakePendingReadingPosition() => _reading.TakePending();

    /// <summary>
    /// A jump requested as part of opening a document, such as a bookmark clicked on the
    /// welcome screen. The window applies it instead of the saved reading position, which
    /// keeps it in the same restore pass rather than racing a second scroll against it.
    /// </summary>
    public NavigationPoint? TakePendingOpenPosition()
    {
        var pending = _pendingOpenPosition;
        _pendingOpenPosition = null;
        return pending;
    }

    public void SaveReadingPositionNow() => _reading.SaveNow();

    public void SaveWindowState(double width, double height, bool isMaximized, int? x = null, int? y = null)
    {
        SaveReadingPositionNow();

        // Only remember the restore size/position while the window is not maximized or
        // full screen; otherwise the next launch would start at the screen's dimensions.
        if (!isMaximized && width > 500 && height > 400)
        {
            _settings.WindowWidth = width;
            _settings.WindowHeight = height;

            if (x is not null && y is not null)
            {
                _settings.WindowX = x;
                _settings.WindowY = y;
            }
        }

        _settings.WindowMaximized = isMaximized;
        _settings.SidebarVisible = IsSidebarVisible;
        _settingsService.Save(_settings);
    }

    public void CommitPageNumberInput() => GoToPageInput();

    public Task<bool> BeginTextSelectionAsync(int pageNumber, double x, double y, int clickCount) =>
        _selection.BeginAsync(pageNumber, x, y, clickCount);

    public bool UpdateTextSelection(int pageNumber, double x, double y) =>
        _selection.Update(pageNumber, x, y);

    public void CompleteTextSelection() => _selection.Complete();

    public void ClearTextSelection()
    {
        _selection.Clear();
        TrimTextLayers();
    }

    public string GetSelectedText() => _selection.SelectedText;

    /// <summary>
    /// Ensures the text layer for a page is available. Exposed for tests and
    /// for input code that wants to pre-warm a page before hit-testing.
    /// </summary>
    public Task<bool> EnsureTextLayerAsync(int pageNumber) =>
        _textLoader?.EnsurePageAsync(pageNumber) ?? Task.FromResult(false);

    /// <summary>Ensures one page's link annotations are loaded.</summary>
    public Task EnsureLinksAsync(int pageNumber) =>
        pageNumber >= 1 && pageNumber <= Pages.Count
            ? Pages[pageNumber - 1].EnsureLinksAsync()
            : Task.CompletedTask;

    /// <summary>
    /// Follows a link annotation. Page destinations reuse the normal navigation so they get
    /// history and scroll handling. URIs are only handed to the system for http, https and
    /// mailto: a PDF must not be able to launch programs or local files through this path.
    /// </summary>
    public void OpenLink(PdfLink link)
    {
        switch (link.Kind)
        {
            case PdfLinkKind.Page:
                NavigateToPage(link.PageNumber);
                break;

            case PdfLinkKind.Uri when link.Uri is { Length: > 0 } text:
                if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || !IsSafeLinkScheme(uri.Scheme))
                {
                    return;
                }

                try
                {
                    StartProcess(uri.AbsoluteUri, string.Empty);
                }
                catch
                {
                    SetError("L.LinkError");
                }

                break;
        }
    }

    private static bool IsSafeLinkScheme(string scheme) =>
        scheme.Equals("http", StringComparison.OrdinalIgnoreCase) ||
        scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ||
        scheme.Equals("mailto", StringComparison.OrdinalIgnoreCase);

    private void NotifySelectionChanged()
    {
        HighlightSelectionCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(SelectionStatus));
    }

    private async Task OnTextPageLoadedAsync(PdfTextPage page, TextLayerLoader loader)
    {
        if (!ReferenceEquals(_textLoader, loader))
        {
            return;
        }

        if (!Dispatcher.UIThread.CheckAccess())
        {
            await Dispatcher.UIThread.InvokeAsync(() => OnTextPageLoadedCore(page, loader));
            return;
        }

        OnTextPageLoadedCore(page, loader);
    }

    private void OnTextPageLoadedCore(PdfTextPage page, TextLayerLoader loader)
    {
        if (!ReferenceEquals(_textLoader, loader))
        {
            return;
        }

        PageViewModel? pageViewModel = page.PageNumber >= 1 && page.PageNumber <= Pages.Count
            ? Pages[page.PageNumber - 1]
            : null;
        if (pageViewModel is null || pageViewModel.Number != page.PageNumber)
        {
            pageViewModel = Pages.FirstOrDefault(candidate => candidate.Number == page.PageNumber);
        }

        if (pageViewModel is null)
        {
            return;
        }

        pageViewModel.SetTextLayout(page);
        _selection.OnTextLayerLoaded(page.PageNumber);
    }

    public void SaveSettings() => _settingsService.Save(_settings);

    private void LoadDocument(PdfDocumentInfo info, string? password)
    {
        CloseDocumentCore(clearDocument: false);

        _document = info;
        _password = password;
        _pageRenderer = new PdfPageRenderer(_documentService, info.FilePath, password);
        _reading.Attach(info);

        Pages = info.Pages.Select(page => new PageViewModel(_pageRenderer, page, _localization)).ToList();

        // The stroke drawn under the pointer has to be the colour and width it will keep.
        foreach (var page in Pages)
        {
            page.InkBrush = InkColor.Brush;
            page.InkWidthPoints = InkWidth;
        }
        Thumbnails = info.Pages.Select(page => new ThumbnailViewModel(_pageRenderer, page)).ToList();
        OutlineItems = info.Bookmarks.Select(item => new OutlineItemViewModel(item)).ToList();
        LoadBookmarks(info.FilePath);
        Metadata = BuildMetadata(info);
        TextLayerLoader? loader = null;
        loader = new TextLayerLoader(
            _textService,
            info.FilePath,
            password,
            info.PageCount,
            page => OnTextPageLoadedAsync(page, loader!));
        _textLoader = loader;

        FileName = info.FileName;
        FilePath = info.FilePath;
        FileDirectory = Path.GetDirectoryName(info.FilePath) ?? string.Empty;
        FileSizeText = FormatFileSize(info.FileSizeBytes);
        PageCount = info.PageCount;
        DocumentSubtitle = $"{PageCountText} · {FileSizeText}";
        WindowTitle = $"{info.FileName} — AirPDF";
        IsDocumentOpen = true;
        PageNumberInput = "1";
        SetCurrentPage(1, updateInput: false);
        PageNumberInput = "1";

        // Pages are constructed at zoom 1.0, so nothing needs re-rendering here. The zoom
        // remembered for this document is applied by ApplyInitialZoom once the window has
        // a viewport; that also avoids the old "render at 1.0, then render again" burst.
        _zoom = 1.0;
        _zoomMode = NormalizeZoomMode(_reading.PendingZoomMode);
        _restoreZoom = _reading.PendingZoom;
        _restoreZoomMode = _zoomMode;
        OnPropertyChanged(nameof(Zoom));
        OnPropertyChanged(nameof(ZoomText));

        ClearSearchState();
        SearchQuery = string.Empty;
        IsPagesTabSelected = true;
        IsContentsTabSelected = false;
        IsBookmarksTabSelected = false;

        OnPropertyChanged(nameof(HasOutlineItems));
        OnPropertyChanged(nameof(HasBookmarks));
        OnPropertyChanged(nameof(HasMetadata));
        OnPropertyChanged(nameof(PageStatusText));
        OnPropertyChanged(nameof(CanGoPrevious));
        OnPropertyChanged(nameof(CanGoNext));
        OnPropertyChanged(nameof(IsWelcomeVisible));
    }

    private void CloseDocumentCore(bool clearDocument = true)
    {
        _reading.Detach();
        ClearHistory();
        ClearUndo();

        // The caches are keyed by page number, so they cannot outlive the document.
        InvalidateMarkupCache();
        _rotationCache.Clear();
        _zoomMode = ZoomModes.FitWidth;
        _restoreZoom = null;
        _restoreZoomMode = null;
        var textLoader = _textLoader;
        _textLoader = null;
        textLoader?.Dispose();
        _selection.Clear();

        foreach (var page in Pages)
        {
            page.Dispose();
        }

        foreach (var thumbnail in Thumbnails)
        {
            thumbnail.Dispose();
        }

        Pages = [];
        Thumbnails = [];
        OutlineItems = [];
        Bookmarks = [];
        Metadata = [];
        SearchResults = [];
        SelectedSearchResult = null;
        HasSearchResults = false;
        HasSearched = false;
        SearchResultsTruncated = false;
        SearchSummary = string.Empty;
        IsSearching = false;
        IsSearchOpen = false;
        IsDocumentOpen = false;
        PageCount = 0;
        SetCurrentPage(1, updateInput: false);
        PageNumberInput = "1";
        FileName = string.Empty;
        FilePath = string.Empty;
        FileDirectory = string.Empty;
        FileSizeText = string.Empty;
        DocumentSubtitle = _localization.T("L.Ready");
        WindowTitle = "AirPDF";
        HasUnsavedAnnotations = false;
        ClearError();
        OnPropertyChanged(nameof(HasOutlineItems));
        OnPropertyChanged(nameof(HasBookmarks));
        OnPropertyChanged(nameof(HasMetadata));
        OnPropertyChanged(nameof(PageStatusText));
        OnPropertyChanged(nameof(CanGoPrevious));
        OnPropertyChanged(nameof(CanGoNext));
        OnPropertyChanged(nameof(IsWelcomeVisible));

        if (clearDocument)
        {
            _document = null;
            _password = null;
            _pageRenderer = null;
        }
    }

    private void SetCurrentPage(int pageNumber, bool updateInput = true)
    {
        if (PageCount <= 0)
        {
            _currentPage = 1;
            return;
        }

        var clamped = Math.Clamp(pageNumber, 1, PageCount);
        if (_currentPage == clamped)
        {
            if (updateInput)
            {
                PageNumberInput = clamped.ToString(CultureInfo.InvariantCulture);
            }

            return;
        }

        _currentPage = clamped;
        OnPropertyChanged(nameof(CurrentPage));
        OnPropertyChanged(nameof(PageStatusText));
        OnPropertyChanged(nameof(ReadingProgress));
        OnPropertyChanged(nameof(ReadingProgressText));
        OnPropertyChanged(nameof(CanGoPrevious));
        OnPropertyChanged(nameof(CanGoNext));

        if (updateInput)
        {
            PageNumberInput = clamped.ToString(CultureInfo.InvariantCulture);
        }

        TrimPageImages();
        TrimTextLayers();
        TrimThumbnails();
    }

    /// <summary>
    /// Keeps parsed text only around the page being read. A selection keeps its pages until
    /// the selection is cleared, because copying and redrawing use the same word geometry.
    /// </summary>
    private void TrimTextLayers()
    {
        var loader = _textLoader;
        if (loader is null || _selectingAllText)
        {
            return;
        }

        foreach (var page in Pages)
        {
            if (Math.Abs(page.Number - _currentPage) <= TextCacheRadius ||
                _selection.IsPageSelected(page.Number) ||
                !page.HasTextLayer)
            {
                continue;
            }

            page.ReleaseTextLayout();
            loader.Release(page.Number);
        }
    }

    private void TrimThumbnails()
    {
        foreach (var thumbnail in Thumbnails)
        {
            if (Math.Abs(thumbnail.Number - _currentPage) > 4)
            {
                thumbnail.TrimImage();
            }
        }
    }

    private void TrimPageImages()
    {
        foreach (var page in Pages)
        {
            if (Math.Abs(page.Number - _currentPage) > 6)
            {
                page.TrimImage();
            }
        }

        var total = Pages.Sum(page => page.BitmapBytes);
        if (total <= PageBitmapBudgetBytes)
        {
            return;
        }

        // Evict farthest pages first. The current page and its immediate neighbours are kept
        // even if they alone exceed the budget: evicting what the reader is looking at would
        // trade memory for a blank screen.
        foreach (var page in Pages
                     .Where(page => Math.Abs(page.Number - _currentPage) > 1)
                     .OrderByDescending(page => Math.Abs(page.Number - _currentPage)))
        {
            if (total <= PageBitmapBudgetBytes)
            {
                break;
            }

            var before = page.BitmapBytes;
            page.TrimImage();
            total -= before - page.BitmapBytes;
        }
    }

    private void ApplyZoomToPages()
    {
        foreach (var page in Pages)
        {
            page.ApplyZoom(_zoom);
        }

        TrimPageImages();
    }

    private void ApplySearchHighlights(SearchResultViewModel? active)
    {
        ClearAllHighlights();
        if (SearchResults.Count == 0)
        {
            return;
        }

        foreach (var group in SearchResults.GroupBy(result => result.PageNumber))
        {
            var page = Pages.FirstOrDefault(candidate => candidate.Number == group.Key);
            if (page is null)
            {
                continue;
            }

            var allRects = group.SelectMany(result => result.Rects).ToList();
            var activeRects = active is not null && active.PageNumber == group.Key
                ? active.Rects
                : [];

            page.SetHighlights(allRects, activeRects);
        }
    }

    private void ClearAllHighlights()
    {
        foreach (var page in Pages)
        {
            page.ClearHighlights();
        }
    }

    private void ClearSearchState()
    {
        CancelSafely(_searchCancellation);
        _searchCancellation = null;
        _searchFailed = false;
        SearchResults = [];
        SelectedSearchResult = null;
        HasSearchResults = false;
        HasSearched = false;
        SearchResultsTruncated = false;
        SearchSummary = string.Empty;
        IsSearching = false;
        ClearAllHighlights();
        OnPropertyChanged(nameof(HasNoSearchResults));
    }

    private void SetSearchSummary()
    {
        if (_searchFailed)
        {
            SearchSummary = _localization.T("L.SearchFailed");
            return;
        }

        if (!HasSearched)
        {
            SearchSummary = string.Empty;
            return;
        }

        if (IsSearching)
        {
            SearchSummary = _localization.T("L.Searching");
            return;
        }

        SearchSummary = SearchResults.Count switch
        {
            0 => _localization.T("L.NoMatches"),
            1 => _localization.T("L.MatchOne"),
            _ => _localization.Format("L.MatchMany", SearchResults.Count)
        };

        if (SearchResultsTruncated)
        {
            SearchSummary = $"{SearchSummary} {_localization.T("L.SearchTruncated")}";
        }
    }

    private void SetError(string key)
    {
        _errorKey = key;
        ErrorMessage = _localization.T(key);
    }

    private void SetErrorFromFailure(PdfFailure failure)
    {
        _errorKey = failure switch
        {
            PdfFailure.NotFound => "L.ErrorNotFound",
            PdfFailure.AccessDenied => "L.ErrorAccessDenied",
            PdfFailure.PasswordRequired => "L.ErrorPasswordRequired",
            PdfFailure.InvalidPassword => "L.ErrorInvalidPassword",
            PdfFailure.Corrupt => "L.ErrorCorrupt",
            PdfFailure.Unsupported => "L.ErrorUnsupported",
            _ => "L.ErrorUnknown"
        };
        ErrorMessage = _localization.T(_errorKey);
    }

    private void ClearError()
    {
        _errorKey = null;
        ErrorMessage = string.Empty;
    }

    private static void CancelSafely(CancellationTokenSource? cancellation)
    {
        if (cancellation is null)
        {
            return;
        }

        try
        {
            cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The operation already completed and disposed its lifetime.
        }
    }

    private void RefreshRecentFiles()
    {
        RecentFiles = _settings.RecentFiles
            .Where(entry => File.Exists(entry.FilePath))
            .OrderByDescending(entry => entry.LastOpened)
            .Take(MaxRecentFilesShown)
            .Select(entry => new RecentFileViewModel(
                entry.FilePath,
                entry.LastOpened,
                _localization,
                OpenRecentFromItemAsync,
                RemoveRecentFromItemAsync))
            .ToList();
    }

    private async Task OpenRecentFromItemAsync(string path)
    {
        await OpenPathAsync(path);
    }

    private async Task RemoveRecentFromItemAsync(string path)
    {
        _settingsService.RemoveRecentFile(_settings, path);
        _settingsService.Save(_settings);
        _services.NotifyRecentFilesChanged();
        RefreshRecentFiles();
        await Task.CompletedTask;
    }

    private void AddRecentFile(string filePath)
    {
        _settingsService.AddRecentFile(_settings, filePath);
        _settingsService.Save(_settings);
        _services.NotifyRecentFilesChanged();
        RefreshRecentFiles();
    }

    private static string FormatFileSize(long bytes)
    {
        if (bytes < 1024)
        {
            return $"{bytes} B";
        }

        if (bytes < 1024 * 1024)
        {
            return $"{bytes / 1024.0:0.#} KB";
        }

        return $"{bytes / (1024.0 * 1024.0):0.#} MB";
    }

    private IReadOnlyList<MetadataRow> BuildMetadata(PdfDocumentInfo info)
    {
        var rows = new List<MetadataRow>();
        var metadata = info.Metadata;
        var dateFormat = _localization.IsChinese ? "yyyy年M月d日" : "MMM d, yyyy";

        Add(_localization.T("L.MetaTitle"), metadata.Title);
        Add(_localization.T("L.MetaAuthor"), metadata.Author);
        Add(_localization.T("L.MetaSubject"), metadata.Subject);
        Add(_localization.T("L.MetaCreated"), metadata.CreatedAt?.LocalDateTime.ToString(dateFormat, _localization.Culture));
        Add(_localization.T("L.MetaCreator"), metadata.Creator);
        Add(_localization.T("L.MetaProducer"), metadata.Producer);
        rows.Add(new MetadataRow(_localization.T("L.MetaPages"), info.PageCount.ToString(CultureInfo.InvariantCulture)));
        rows.Add(new MetadataRow(
            _localization.T("L.MetaPdfVersion"),
            info.PdfVersion.ToString("0.0", CultureInfo.InvariantCulture)));
        rows.Add(new MetadataRow(_localization.T("L.MetaFileSize"), FormatFileSize(info.FileSizeBytes)));
        Add(_localization.T("L.MetaKeywords"), metadata.Keywords);

        return rows;

        void Add(string label, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                rows.Add(new MetadataRow(label, value.Trim()));
            }
        }
    }

    partial void OnFileNameChanged(string value) => OnPropertyChanged(nameof(TabTitle));

    partial void OnIsDocumentOpenChanged(bool value)
    {
        OnPropertyChanged(nameof(TabTitle));
        OnPropertyChanged(nameof(IsWelcomeVisible));
        OnPropertyChanged(nameof(IsSidebarShown));
        OnPropertyChanged(nameof(CanGoPrevious));
        OnPropertyChanged(nameof(CanGoNext));
    }

    partial void OnIsSidebarVisibleChanged(bool value) => OnPropertyChanged(nameof(IsSidebarShown));

    partial void OnPageCountChanged(int value)
    {
        OnPropertyChanged(nameof(PageStatusText));
        OnPropertyChanged(nameof(PageCountText));
        OnPropertyChanged(nameof(ReadingProgress));
        OnPropertyChanged(nameof(ReadingProgressText));
        OnPropertyChanged(nameof(CanGoNext));
    }

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(IsWelcomeVisible));

    /// <summary>
    /// Keeps every window in step: the theme lives in <see cref="ThemeService"/>, so a
    /// change made in one window notifies all of them.
    /// </summary>
    private void OnSharedRecentFilesChanged(object? sender, EventArgs e) => RefreshRecentFiles();

    private void OnSharedBookmarksChanged(object? sender, EventArgs e)
    {
        LoadBookmarks(FilePath);
        RefreshAllBookmarks();
    }
    private void OnThemeChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(IsDarkTheme));
        OnPropertyChanged(nameof(IsLightTheme));
        OnPropertyChanged(nameof(IsSystemTheme));
        OnPropertyChanged(nameof(ThemeTooltip));
    }

    partial void OnHasSearchedChanged(bool value) => OnPropertyChanged(nameof(HasNoSearchResults));

    partial void OnSearchQueryChanged(string value) => OnPropertyChanged(nameof(HasSearchQuery));

    partial void OnIsSearchingChanged(bool value) => OnPropertyChanged(nameof(HasNoSearchResults));

    partial void OnHasUpdateChanged(bool value)
    {
        OnPropertyChanged(nameof(CanApplyUpdate));
        OnPropertyChanged(nameof(UpdateButtonText));
    }

    partial void OnIsUpdateReadyChanged(bool value) => OnPropertyChanged(nameof(UpdateButtonText));

    partial void OnIsUpdatingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanApplyUpdate));
        OnPropertyChanged(nameof(UpdateButtonText));
    }

    partial void OnUpdateProgressChanged(int value) => OnPropertyChanged(nameof(UpdateButtonText));

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(LanguageShortLabel));
        OnPropertyChanged(nameof(PageStatusText));
        OnPropertyChanged(nameof(PageCountText));
        OnPropertyChanged(nameof(SelectionStatus));
        OnPropertyChanged(nameof(UpdateButtonText));
        OnPropertyChanged(nameof(ThemeTooltip));
        OnPropertyChanged(nameof(ReadingProgressText));

        if (_document is not null)
        {
            WindowTitle = $"{_document.FileName} — AirPDF";
            DocumentSubtitle = $"{PageCountText} · {FileSizeText}";
            Metadata = BuildMetadata(_document);
        }
        else
        {
            WindowTitle = "AirPDF";
            DocumentSubtitle = _localization.T("L.Ready");
        }

        if (IsBusy && !string.IsNullOrEmpty(_busyFileName))
        {
            BusyText = _localization.Format("L.BusyOpening", _busyFileName);
        }

        if (!string.IsNullOrEmpty(_errorKey))
        {
            ErrorMessage = _localization.T(_errorKey);
        }

        foreach (var result in SearchResults)
        {
            result.RefreshLanguage();
        }

        foreach (var page in Pages)
        {
            page.RefreshLanguage();
        }

        SetSearchSummary();
        RefreshRecentFiles();
    }

    partial void OnOutlineItemsChanged(IReadOnlyList<OutlineItemViewModel> value) =>
        NotifyOutlineChanged();

    partial void OnBookmarksChanged(IReadOnlyList<UserBookmarkViewModel> value) =>
        NotifyBookmarksChanged();

    partial void OnStatusMessageChanged(string value) => OnPropertyChanged(nameof(HasStatusMessage));

    partial void OnIsInkModeChanged(bool value)
    {
        OnPropertyChanged(nameof(InkModeLabel));

        if (value)
        {
            // The drawing tool owns the pointer, so a text selection left highlighted would
            // look like both modes are active at once.
            ClearTextSelection();
        }
    }

    partial void OnIsEraserModeChanged(bool value)
    {
        if (value)
        {
            ClearTextSelection();
        }
    }

    partial void OnNoteTextChanged(string? value) => OnPropertyChanged(nameof(HasNoteText));

    partial void OnInkColorChanged(MarkupColor value)
    {
        foreach (var page in Pages)
        {
            page.InkBrush = value.Brush;
        }
    }

    partial void OnInkWidthChanged(double value)
    {
        foreach (var page in Pages)
        {
            page.InkWidthPoints = value;
        }
    }

    partial void OnHasUnsavedAnnotationsChanged(bool value) => UpdateTitleMarker();

    partial void OnAllBookmarksChanged(IReadOnlyList<BookmarkOverviewViewModel> value)
    {
        OnPropertyChanged(nameof(HasAllBookmarks));
        OnPropertyChanged(nameof(HasNoAllBookmarks));
    }

    partial void OnMetadataChanged(IReadOnlyList<MetadataRow> value) =>
        OnPropertyChanged(nameof(HasMetadata));

    partial void OnRecentFilesChanged(IReadOnlyList<RecentFileViewModel> value) =>
        NotifyRecentChanged();

    partial void OnSearchResultsChanged(IReadOnlyList<SearchResultViewModel> value)
    {
        HasSearchResults = value.Count > 0;
        OnPropertyChanged(nameof(HasNoSearchResults));
    }

    private void NotifyOutlineChanged()
    {
        OnPropertyChanged(nameof(HasOutlineItems));
        OnPropertyChanged(nameof(HasNoOutlineItems));
    }

    private void NotifyBookmarksChanged()
    {
        OnPropertyChanged(nameof(HasBookmarks));
        OnPropertyChanged(nameof(HasNoBookmarks));
    }

    private void NotifyRecentChanged()
    {
        OnPropertyChanged(nameof(HasRecentFiles));
        OnPropertyChanged(nameof(HasNoRecentFiles));
    }

    private void PushHistory()
    {
        if (!IsDocumentOpen || PageCount <= 0)
        {
            return;
        }

        var point = new NavigationPoint(CurrentPage, Math.Clamp(_reading.PageOffset, 0, 1));
        if (_history.Count > 0 && _history[^1] == point)
        {
            return;
        }

        _history.Add(point);
        if (_history.Count > MaxHistoryEntries)
        {
            _history.RemoveAt(0);
        }

        OnPropertyChanged(nameof(CanNavigateBack));
    }

    private void ClearHistory()
    {
        if (_history.Count == 0)
        {
            return;
        }

        _history.Clear();
        OnPropertyChanged(nameof(CanNavigateBack));
    }

    private static void StartProcess(string fileName, string arguments) =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(fileName, arguments)
        {
            UseShellExecute = true
        });


    private static string NormalizeZoomMode(string? mode) => mode switch
    {
        ZoomModes.FitPage => ZoomModes.FitPage,
        ZoomModes.Manual => ZoomModes.Manual,
        _ => ZoomModes.FitWidth
    };
    private static class ZoomModes
    {
        public const string FitWidth = "fit-width";
        public const string FitPage = "fit-page";
        public const string Manual = "manual";
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        OpeningPath = null;

        _localization.LanguageChanged -= OnLanguageChanged;
        _theme.Changed -= OnThemeChanged;
        _services.RecentFilesChanged -= OnSharedRecentFilesChanged;
        _services.BookmarksChanged -= OnSharedBookmarksChanged;

        CancelSafely(_loadCancellation);
        CancelSafely(_searchCancellation);
        _reading.Dispose();
        CloseDocumentCore();
        _documentService.Dispose();
    }
}
