using System.Globalization;
using AirPdf.App.Rendering;
using AirPdf.App.Services;
using AirPdf.Core.Models;
using AirPdf.Core.Services;
using Avalonia;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AirPdf.App.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject
{
    private static readonly double[] ZoomSteps =
        [0.25, 0.33, 0.5, 0.67, 0.75, 0.9, 1.0, 1.1, 1.25, 1.5, 1.75, 2.0, 2.5, 3.0, 4.0];

    private readonly PdfDocumentService _documentService;
    private readonly PdfSearchService _searchService;
    private readonly SettingsService _settingsService;
    private readonly IUserDialogService _dialogs;
    private readonly LocalizationService _localization;
    private readonly AppSettings _settings;
    private CancellationTokenSource? _loadCancellation;
    private CancellationTokenSource? _searchCancellation;
    private PdfDocumentInfo? _document;
    private PdfPageRenderer? _pageRenderer;
    private string? _password;
    private double _zoom = 1.0;
    private int _currentPage = 1;
    private SearchResultViewModel? _selectedSearchResult;
    private string? _errorMessage;
    private string? _errorKey;
    private string? _busyFileName;
    private bool _searchFailed;

    public MainWindowViewModel(
        PdfDocumentService documentService,
        PdfSearchService searchService,
        SettingsService settingsService,
        IUserDialogService dialogs,
        AppSettings settings,
        LocalizationService localization)
    {
        _documentService = documentService;
        _searchService = searchService;
        _settingsService = settingsService;
        _dialogs = dialogs;
        _localization = localization;
        _settings = settings;
        _isSidebarVisible = _settings.SidebarVisible;
        _documentSubtitle = _localization.T("L.Ready");

        var systemVariant = Application.Current?.PlatformSettings?.GetColorValues().ThemeVariant;
        var systemIsDark = systemVariant == Avalonia.Platform.PlatformThemeVariant.Dark;
        _isDarkTheme = _settings.Theme switch
        {
            "Dark" => true,
            "Light" => false,
            _ => systemIsDark
        };
        ApplyTheme();

        _localization.LanguageChanged += OnLanguageChanged;
        RefreshRecentFiles();
    }

    public event EventHandler<int>? ScrollToPageRequested;

    public event EventHandler? DocumentOpened;

    public event EventHandler? ZoomChanged;

    public event EventHandler? SearchOpened;

    public event EventHandler<SearchResultViewModel>? ScrollToSearchResultRequested;

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

    public bool HasRecentFiles => RecentFiles.Count > 0;

    public bool HasNoRecentFiles => !HasRecentFiles;

    public bool HasBookmarks => Bookmarks.Count > 0;

    public bool HasNoBookmarks => !HasBookmarks;

    public bool HasMetadata => Metadata.Count > 0;

    public bool HasSearchQuery => !string.IsNullOrWhiteSpace(SearchQuery);

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
        IsDarkTheme = !IsDarkTheme;
        _settings.Theme = IsDarkTheme ? "Dark" : "Light";
        _settingsService.Save(_settings);
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
        Zoom = next;
    }

    [RelayCommand]
    private void ZoomOut()
    {
        var previous = ZoomSteps.LastOrDefault(step => step < _zoom - 0.001, 0.25);
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

        Zoom = numeric;
    }

    [RelayCommand]
    private void ActualSize() => Zoom = 1.0;

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

        var index = SelectedSearchResult is null
            ? -1
            : SearchResults.ToList().IndexOf(SelectedSearchResult);
        SelectedSearchResult = SearchResults[(index + 1) % SearchResults.Count];
    }

    [RelayCommand]
    private void PreviousSearchResult()
    {
        if (SearchResults.Count == 0)
        {
            return;
        }

        var index = SelectedSearchResult is null
            ? 0
            : SearchResults.ToList().IndexOf(SelectedSearchResult);
        SelectedSearchResult = SearchResults[(index - 1 + SearchResults.Count) % SearchResults.Count];
    }

    [RelayCommand]
    private void DismissError() => ClearError();

    public async Task OpenPathAsync(string filePath, bool recordRecent = true)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return;
        }

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
        Zoom = Math.Min(byWidth, byHeight);
    }

    public void NavigateToPage(int pageNumber)
    {
        if (!IsDocumentOpen || PageCount == 0)
        {
            return;
        }

        SetCurrentPage(pageNumber);
        ScrollToPageRequested?.Invoke(this, _currentPage);
    }

    public void UpdateCurrentPageFromScroll(int pageNumber)
    {
        if (!IsDocumentOpen)
        {
            return;
        }

        SetCurrentPage(pageNumber);
    }

    public void CommitPageNumberInput() => GoToPageInput();

    public void SaveWindowState(double width, double height, bool isMaximized)
    {
        if (width > 500 && height > 400)
        {
            _settings.WindowWidth = width;
            _settings.WindowHeight = height;
        }

        _settings.WindowMaximized = isMaximized;
        _settings.SidebarVisible = IsSidebarVisible;
        _settingsService.Save(_settings);
    }

    public void SaveSettings() => _settingsService.Save(_settings);

    private void LoadDocument(PdfDocumentInfo info, string? password)
    {
        CloseDocumentCore(clearDocument: false);

        _document = info;
        _password = password;
        _pageRenderer = new PdfPageRenderer(_documentService, info.FilePath, password);

        Pages = info.Pages.Select(page => new PageViewModel(_pageRenderer, page, _localization)).ToList();
        Thumbnails = info.Pages.Select(page => new ThumbnailViewModel(_pageRenderer, page)).ToList();
        Bookmarks = info.Bookmarks.Select(bookmark => new BookmarkViewModel(bookmark)).ToList();
        Metadata = BuildMetadata(info);

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

        _zoom = 1.0;
        OnPropertyChanged(nameof(Zoom));
        OnPropertyChanged(nameof(ZoomText));
        ApplyZoomToPages();

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
        OnPropertyChanged(nameof(CurrentPage));
        OnPropertyChanged(nameof(PageStatusText));
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
            .Take(6)
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

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(LanguageShortLabel));
        OnPropertyChanged(nameof(PageStatusText));
        OnPropertyChanged(nameof(PageCountText));

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
        if (Application.Current is { } application)
        {
            application.RequestedThemeVariant = IsDarkTheme ? ThemeVariant.Dark : ThemeVariant.Light;
        }
    }
}
