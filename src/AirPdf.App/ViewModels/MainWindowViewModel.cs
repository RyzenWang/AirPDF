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

    /// <summary>Must match <c>SettingsService.MaxRecentFiles</c> so nothing stored is unreachable.</summary>
    private const int MaxRecentFilesShown = 10;

    private readonly PdfDocumentService _documentService;
    private readonly PdfSearchService _searchService;
    private readonly PdfTextService _textService;
    private readonly SettingsService _settingsService;
    private readonly UpdateService _updateService;
    private readonly IUserDialogService _dialogs;
    private readonly LocalizationService _localization;
    private readonly AppSettings _settings;
    private readonly List<NavigationPoint> _history = [];
    private CancellationTokenSource? _loadCancellation;
    private CancellationTokenSource? _searchCancellation;
    private PdfDocumentInfo? _document;
    private PdfPageRenderer? _pageRenderer;
    private string? _password;
    private double _zoom = 1.0;
    private string _zoomMode = ZoomModes.FitWidth;
    private string _themeMode = ThemeModes.System;
    private IResourceProvider? _highContrastPalette;
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
        PdfDocumentService documentService,
        PdfSearchService searchService,
        PdfTextService textService,
        SettingsService settingsService,
        UpdateService updateService,
        IUserDialogService dialogs,
        AppSettings settings,
        LocalizationService localization)
    {
        _documentService = documentService;
        _searchService = searchService;
        _textService = textService;
        _settingsService = settingsService;
        _updateService = updateService;
        _dialogs = dialogs;
        _localization = localization;
        _settings = settings;
        _isSidebarVisible = _settings.SidebarVisible;
        _settings.RecentFiles ??= [];
        _settings.ReadingPositions ??= [];
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

        _themeMode = NormalizeThemeMode(_settings.Theme);
        _settings.Theme = _themeMode;
        _isDarkTheme = ResolveIsDark(_themeMode);
        ApplyTheme();

        if (Application.Current?.PlatformSettings is { } platformSettings)
        {
            platformSettings.ColorValuesChanged += OnPlatformColorValuesChanged;
        }

        _localization.LanguageChanged += OnLanguageChanged;
        RefreshRecentFiles();
        _ = CheckForUpdatesAsync(userInitiated: false);
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
    private IReadOnlyList<BookmarkViewModel> _bookmarks = [];

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
    private bool _isBusy;

    [ObservableProperty]
    private string _busyText = "Opening document…";

    [ObservableProperty]
    private bool _isDragOver;

    [ObservableProperty]
    private bool _isDarkTheme;

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

    public bool HasBookmarks => Bookmarks.Count > 0;

    public bool HasNoBookmarks => !HasBookmarks;

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

    public bool IsSidebarShown => IsDocumentOpen && IsSidebarVisible;

    public bool IsLightTheme => !IsDarkTheme;

    public bool IsSystemTheme => string.Equals(_themeMode, ThemeModes.System, StringComparison.Ordinal);

    public string ThemeTooltip => _localization.Format(
        "L.ThemeTooltipFormat",
        _localization.T(_themeMode switch
        {
            ThemeModes.Dark => "L.ThemeDark",
            ThemeModes.Light => "L.ThemeLight",
            ThemeModes.HighContrast => "L.ThemeHighContrast",
            _ => "L.ThemeSystem"
        }));

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
        if (!string.IsNullOrWhiteSpace(path))
        {
            await OpenPathAsync(path);
        }
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
        RefreshRecentFiles();
        await Task.CompletedTask;
    }

    [RelayCommand]
    private void ClearRecent()
    {
        _settings.RecentFiles.Clear();
        _settingsService.Save(_settings);
        RefreshRecentFiles();
    }

    [RelayCommand]
    private void CloseDocument()
    {
        SaveReadingPositionNow();
        CancelSafely(_loadCancellation);
        CancelSafely(_searchCancellation);
        CloseDocumentCore();
    }

    [RelayCommand]
    private void ToggleSidebar()
    {
        IsSidebarVisible = !IsSidebarVisible;
        _settings.SidebarVisible = IsSidebarVisible;
        _settingsService.Save(_settings);
    }

    [RelayCommand]
    private void ToggleTheme()
    {
        SetThemeMode(
            _themeMode switch
            {
                ThemeModes.System => ThemeModes.Light,
                ThemeModes.Light => ThemeModes.Dark,
                ThemeModes.Dark => ThemeModes.HighContrast,
                _ => ThemeModes.System
            },
            persist: true);
    }

    /// <summary>
    /// Cycles System → Light → Dark → High contrast → System. "System" keeps following
    /// the OS instead of pinning a fixed theme.
    /// </summary>
    public void SetThemeMode(string mode, bool persist)
    {
        _themeMode = NormalizeThemeMode(mode);
        _settings.Theme = _themeMode;
        IsDarkTheme = ResolveIsDark(_themeMode);

        // IsDarkTheme may be unchanged (e.g. System-light → Light), so apply explicitly.
        ApplyTheme();

        OnPropertyChanged(nameof(IsSystemTheme));
        OnPropertyChanged(nameof(IsLightTheme));
        OnPropertyChanged(nameof(ThemeTooltip));

        if (persist)
        {
            _settingsService.Save(_settings);
        }
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
    private async Task SelectAllTextAsync() => await _selection.SelectAllAsync();

    [RelayCommand]
    private void ShowPagesTab()
    {
        IsPagesTabSelected = true;
        IsContentsTabSelected = false;
    }

    [RelayCommand]
    private void ShowContentsTab()
    {
        IsPagesTabSelected = false;
        IsContentsTabSelected = true;
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
        _searchFailed = false;
        SetSearchSummary();
        SearchResults = [];
        SelectedSearchResult = null;
        ClearAllHighlights();
        OnPropertyChanged(nameof(HasNoSearchResults));

        try
        {
            var hits = await _searchService.SearchAsync(_document.FilePath, query, _password, cancellation.Token);
            if (cancellation.IsCancellationRequested)
            {
                return;
            }

            var results = hits
                .Select((hit, index) => new SearchResultViewModel(hit, index + 1, _localization))
                .ToList();

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
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return;
        }

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

    public void ClearTextSelection() => _selection.Clear();

    public string GetSelectedText() => _selection.SelectedText;

    /// <summary>
    /// Ensures the text layer for a page is available. Exposed for tests and
    /// for input code that wants to pre-warm a page before hit-testing.
    /// </summary>
    public Task<bool> EnsureTextLayerAsync(int pageNumber) =>
        _textLoader?.EnsurePageAsync(pageNumber) ?? Task.FromResult(false);

    private void NotifySelectionChanged()
    {
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
        Thumbnails = info.Pages.Select(page => new ThumbnailViewModel(_pageRenderer, page)).ToList();
        Bookmarks = info.Bookmarks.Select(bookmark => new BookmarkViewModel(bookmark)).ToList();
        Metadata = BuildMetadata(info);
        TextLayerLoader? loader = null;
        loader = new TextLayerLoader(
            _textService,
            info.FilePath,
            password,
            info.PageCount,
            page => OnTextPageLoadedAsync(page, loader!));
        _textLoader = loader;
        loader.Request(pageNumber: 1, radius: 1);

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
        Bookmarks = [];
        Metadata = [];
        SearchResults = [];
        SelectedSearchResult = null;
        HasSearchResults = false;
        HasSearched = false;
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
        ClearError();
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
        _textLoader?.Request(clamped, radius: 1);
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
    }

    private void TrimPageImages()
    {
        foreach (var page in Pages)
        {
            if (Math.Abs(page.Number - _currentPage) > 4)
            {
                page.TrimImage();
            }
        }
    }

    private void ApplyZoomToPages()
    {
        foreach (var page in Pages)
        {
            page.ApplyZoom(_zoom);
        }
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
        RefreshRecentFiles();
        await Task.CompletedTask;
    }

    private void AddRecentFile(string filePath)
    {
        _settingsService.AddRecentFile(_settings, filePath);
        _settingsService.Save(_settings);
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

    partial void OnIsDocumentOpenChanged(bool value)
    {
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

    partial void OnIsDarkThemeChanged(bool value)
    {
        ApplyTheme();
        OnPropertyChanged(nameof(IsLightTheme));
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

    partial void OnBookmarksChanged(IReadOnlyList<BookmarkViewModel> value) =>
        NotifyBookmarksChanged();

    partial void OnMetadataChanged(IReadOnlyList<MetadataRow> value) =>
        OnPropertyChanged(nameof(HasMetadata));

    partial void OnRecentFilesChanged(IReadOnlyList<RecentFileViewModel> value) =>
        NotifyRecentChanged();

    partial void OnSearchResultsChanged(IReadOnlyList<SearchResultViewModel> value)
    {
        HasSearchResults = value.Count > 0;
        OnPropertyChanged(nameof(HasNoSearchResults));
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

    private void ApplyTheme()
    {
        if (Application.Current is not { } application)
        {
            return;
        }

        application.RequestedThemeVariant = _themeMode switch
        {
            ThemeModes.Light => ThemeVariant.Light,
            ThemeModes.Dark or ThemeModes.HighContrast => ThemeVariant.Dark,
            // Default lets Avalonia follow the operating system.
            _ => ThemeVariant.Default
        };

        ApplyHighContrastPalette(
            application,
            string.Equals(_themeMode, ThemeModes.HighContrast, StringComparison.Ordinal));
    }

    /// <summary>
    /// Avalonia 11 has no high-contrast theme variant, so the high-contrast palette is a
    /// flat dictionary merged on top of <c>Styles/Colors.axaml</c>. Later merged
    /// dictionaries win, so every key it defines overrides the light/dark palette.
    /// </summary>
    private void ApplyHighContrastPalette(Application application, bool enabled)
    {
        if (!enabled)
        {
            if (_highContrastPalette is not null)
            {
                application.Resources.MergedDictionaries.Remove(_highContrastPalette);
                _highContrastPalette = null;
            }

            return;
        }

        if (_highContrastPalette is not null)
        {
            return;
        }

        try
        {
            var include = new ResourceInclude(new Uri("avares://AirPdf/"))
            {
                Source = new Uri("avares://AirPdf/Styles/HighContrast.axaml")
            };

            if (include.Loaded is { } palette)
            {
                application.Resources.MergedDictionaries.Add(palette);
                _highContrastPalette = palette;
            }
        }
        catch
        {
            // A missing or malformed palette must not stop the app from starting; the
            // window simply keeps the light/dark palette.
        }
    }

    private static string NormalizeThemeMode(string? mode) => mode switch
    {
        ThemeModes.Light => ThemeModes.Light,
        ThemeModes.Dark => ThemeModes.Dark,
        ThemeModes.HighContrast => ThemeModes.HighContrast,
        _ => ThemeModes.System
    };

    private static string NormalizeZoomMode(string? mode) => mode switch
    {
        ZoomModes.FitPage => ZoomModes.FitPage,
        ZoomModes.Manual => ZoomModes.Manual,
        _ => ZoomModes.FitWidth
    };

    private static bool ResolveIsDark(string mode) => mode switch
    {
        ThemeModes.Dark or ThemeModes.HighContrast => true,
        ThemeModes.Light => false,
        _ => SystemIsDark()
    };

    private static bool SystemIsDark()
    {
        try
        {
            return Application.Current?.PlatformSettings?.GetColorValues().ThemeVariant
                == PlatformThemeVariant.Dark;
        }
        catch
        {
            // Headless or unusual platform hosts may not expose theme values.
            return false;
        }
    }

    private void OnPlatformColorValuesChanged(object? sender, PlatformColorValues e)
    {
        if (!IsSystemTheme)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            if (!IsSystemTheme)
            {
                return;
            }

            IsDarkTheme = e.ThemeVariant == PlatformThemeVariant.Dark;
            ApplyTheme();
        });
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

    private static class ThemeModes
    {
        public const string System = "System";
        public const string Light = "Light";
        public const string Dark = "Dark";
        public const string HighContrast = "HighContrast";
    }

    private static class ZoomModes
    {
        public const string FitWidth = "fit-width";
        public const string FitPage = "fit-page";
        public const string Manual = "manual";
    }

    public void Dispose()
    {
        _localization.LanguageChanged -= OnLanguageChanged;

        if (Application.Current?.PlatformSettings is { } platformSettings)
        {
            platformSettings.ColorValuesChanged -= OnPlatformColorValuesChanged;
        }

        CancelSafely(_loadCancellation);
        CancelSafely(_searchCancellation);
        _reading.Dispose();
        CloseDocumentCore();
    }
}
