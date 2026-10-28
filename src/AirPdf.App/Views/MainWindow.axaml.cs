using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AirPdf.App.Services;
using AirPdf.App.ViewModels;
using AirPdf.Core.Services;

namespace AirPdf.App.Views;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel;
    private readonly SettingsService _settingsService;
    private readonly string? _initialFile;
    private bool _suppressScrollSync;
    private bool _suppressThumbnailSync;
    private bool _isSelectingText;
    private bool _selectionMoved;
    private bool _restoringDocument;

    public MainWindow()
        : this(null)
    {
    }

    public MainWindow(string? initialFile)
    {
        _settingsService = new SettingsService();
        var settings = _settingsService.Load();
        var localization = new LocalizationService();
        localization.Apply(settings.Language);

        InitializeComponent();

        _initialFile = initialFile;
        var documentService = new PdfDocumentService();
        var searchService = new PdfSearchService();
        var textService = new PdfTextService();
        var dialogs = new DialogService(() => this, localization);

        _viewModel = new MainWindowViewModel(
            documentService,
            searchService,
            textService,
            _settingsService,
            dialogs,
            settings,
            localization);
        DataContext = _viewModel;

        if (InfoButton.Flyout is Flyout { Content: Control infoContent })
        {
            infoContent.DataContext = _viewModel;
        }

        Width = Math.Max(MinWidth, settings.WindowWidth);
        Height = Math.Max(MinHeight, settings.WindowHeight);
        if (settings.WindowMaximized)
        {
            WindowState = WindowState.Maximized;
        }

        _viewModel.ScrollToPageRequested += OnScrollToPageRequested;
        _viewModel.ScrollToSearchResultRequested += OnScrollToSearchResultRequested;
        _viewModel.CopySelectionRequested += OnCopySelectionRequested;
        _viewModel.DocumentOpened += OnDocumentOpened;
        _viewModel.ZoomChanged += OnZoomChanged;
        _viewModel.SearchOpened += OnSearchOpened;

        PagesList.SizeChanged += (_, _) => UpdateViewport();

        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
        AddHandler(PointerPressedEvent, OnSelectionPointerPressed, RoutingStrategies.Tunnel);
        AddHandler(PointerMovedEvent, OnSelectionPointerMoved, RoutingStrategies.Tunnel);
        AddHandler(PointerReleasedEvent, OnSelectionPointerReleased, RoutingStrategies.Tunnel);
        PagesList.AddHandler(PointerWheelChangedEvent, OnPagesPointerWheel, RoutingStrategies.Tunnel);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
        AddHandler(DragDrop.DropEvent, OnDrop);
        DragDrop.SetAllowDrop(this, true);

        Opened += OnOpened;
        Closing += OnClosing;
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        UpdateViewport();
        if (!string.IsNullOrWhiteSpace(_initialFile))
        {
            _ = _viewModel.OpenPathAsync(_initialFile);
        }
    }

    private void OnDocumentOpened(object? sender, EventArgs e)
    {
        var readingPosition = _viewModel.TakePendingReadingPosition();

        Dispatcher.UIThread.Post(() =>
        {
            _restoringDocument = true;
            UpdateViewport();
            _viewModel.FitWidthToViewport();

            if (readingPosition is not null)
            {
                ScrollToReadingPosition(readingPosition.Page, readingPosition.PageOffset);
            }
            else
            {
                ScrollToPage(1, center: false);
            }

            UpdateThumbnailSelection();
            DispatcherTimer.RunOnce(() => _restoringDocument = false, TimeSpan.FromMilliseconds(300));
        }, DispatcherPriority.Loaded);
    }

    private void OnZoomChanged(object? sender, EventArgs e)
    {
        if (_restoringDocument)
        {
            return;
        }

        var page = _viewModel.CurrentPage;
        Dispatcher.UIThread.Post(() => ScrollToPage(page), DispatcherPriority.Loaded);
    }

    private void OnScrollToPageRequested(object? sender, int pageNumber)
    {
        Dispatcher.UIThread.Post(() => ScrollToPage(pageNumber), DispatcherPriority.Loaded);
    }

    private void OnScrollToSearchResultRequested(object? sender, SearchResultViewModel result)
    {
        Dispatcher.UIThread.Post(() => ScrollToSearchResult(result), DispatcherPriority.Loaded);
    }

    private void OnSearchOpened(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
        }, DispatcherPriority.Loaded);
    }

    private void UpdateViewport()
    {
        _viewModel.UpdateViewport(PagesList.Bounds.Width, PagesList.Bounds.Height);
    }

    private void OnPagesScrollChanged(object? sender, EventArgs e)
    {
        if (_suppressScrollSync || !_viewModel.IsDocumentOpen)
        {
            return;
        }

        var page = GetPageAtViewportTop();
        var viewer = PagesList.Viewer;
        if (page > 0 && viewer is not null)
        {
            var pageModel = _viewModel.Pages.FirstOrDefault(candidate => candidate.Number == page);
            var pageTop = GetPageTop(page);
            var fraction = pageModel is null
                ? 0
                : Math.Clamp(
                    (viewer.Offset.Y - pageTop) / Math.Max(1, pageModel.DisplayHeight),
                    0,
                    1);
            _viewModel.UpdateCurrentPageFromScroll(page, fraction);
            UpdateThumbnailSelection();
        }
    }

    private double GetPageTop(int pageNumber)
    {
        var index = Math.Clamp(pageNumber, 1, Math.Max(1, _viewModel.Pages.Count)) - 1;
        var top = 24.0;
        for (var i = 0; i < index; i++)
        {
            top += _viewModel.Pages[i].DisplayHeight + 28;
        }

        return top;
    }

    private int GetPageAtViewportTop()
    {
        var viewer = PagesList.Viewer;
        if (viewer is null || _viewModel.Pages.Count == 0)
        {
            return 1;
        }

        var offset = viewer.Offset.Y;
        var viewportHeight = viewer.Viewport.Height;
        if (viewportHeight <= 0)
        {
            return _viewModel.CurrentPage;
        }

        var top = 24.0;
        var bestOverlap = -1.0;
        var bestPage = 1;
        var viewportBottom = offset + viewportHeight;

        foreach (var page in _viewModel.Pages)
        {
            var bottom = top + page.DisplayHeight;
            var overlap = Math.Min(bottom, viewportBottom) - Math.Max(top, offset);
            if (overlap > bestOverlap)
            {
                bestOverlap = overlap;
                bestPage = page.Number;
            }

            top = bottom + 28;
        }

        return bestPage;
    }

    private void ScrollToPage(int pageNumber, bool center = true, int attempt = 0)
    {
        if (_viewModel.Pages.Count == 0)
        {
            return;
        }

        var viewer = PagesList.Viewer;
        if (viewer is null)
        {
            if (attempt < 2)
            {
                Dispatcher.UIThread.Post(() => ScrollToPage(pageNumber, center, attempt + 1), DispatcherPriority.Loaded);
            }

            return;
        }

        var index = Math.Clamp(pageNumber, 1, _viewModel.Pages.Count) - 1;
        var top = GetPageTop(pageNumber);
        var page = _viewModel.Pages[index];
        var target = center
            ? top - Math.Max(12, (viewer.Viewport.Height - page.DisplayHeight) / 2)
            : top - 12;
        var fraction = Math.Clamp((target - top) / Math.Max(1, page.DisplayHeight), 0, 1);

        ApplyScrollOffset(target, pageNumber, fraction);
    }

    private void ScrollToReadingPosition(int pageNumber, double fraction, int attempt = 0)
    {
        if (_viewModel.Pages.Count == 0)
        {
            return;
        }

        var viewer = PagesList.Viewer;
        if (viewer is null)
        {
            if (attempt < 2)
            {
                Dispatcher.UIThread.Post(
                    () => ScrollToReadingPosition(pageNumber, fraction, attempt + 1),
                    DispatcherPriority.Background);
            }

            return;
        }

        var index = Math.Clamp(pageNumber, 1, _viewModel.Pages.Count) - 1;
        var page = _viewModel.Pages[index];
        var top = GetPageTop(page.Number);
        var normalized = Math.Clamp(fraction, 0, 1);
        var target = top + (normalized * page.DisplayHeight);

        ApplyScrollOffset(target, page.Number, normalized);
    }

    private void ScrollToSearchResult(SearchResultViewModel result)
    {
        if (result.PageNumber < 1 || result.PageNumber > _viewModel.Pages.Count)
        {
            return;
        }

        var viewer = PagesList.Viewer;
        if (viewer is null)
        {
            return;
        }

        var page = _viewModel.Pages[result.PageNumber - 1];
        var top = GetPageTop(result.PageNumber);

        var highlightTop = result.Rects.Count > 0
            ? top + (result.Rects[0].Y * page.DisplayHeight)
            : top;
        var target = highlightTop - (viewer.Viewport.Height * 0.34);
        var fraction = Math.Clamp((highlightTop - top) / Math.Max(1, page.DisplayHeight), 0, 1);
        ApplyScrollOffset(target, result.PageNumber, fraction);
    }

    private void ApplyScrollOffset(double target, int pageNumber, double fraction)
    {
        _suppressScrollSync = true;
        PagesList.ScrollToVerticalOffset(target);
        DispatcherTimer.RunOnce(() =>
        {
            _suppressScrollSync = false;
            _viewModel.UpdateCurrentPageFromScroll(pageNumber, fraction);
            UpdateThumbnailSelection();
        }, TimeSpan.FromMilliseconds(140));

        UpdateThumbnailSelection();
    }

    private void UpdateThumbnailSelection()
    {
        var index = _viewModel.CurrentPage - 1;
        if (index < 0 || index >= _viewModel.Thumbnails.Count)
        {
            return;
        }

        _suppressThumbnailSync = true;
        ThumbnailList.SelectedIndex = index;
        if (index < ThumbnailList.ItemCount)
        {
            ThumbnailList.ScrollIntoView(index);
        }

        Dispatcher.UIThread.Post(() => _suppressThumbnailSync = false, DispatcherPriority.Background);
    }

    private void OnThumbnailSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressThumbnailSync || ThumbnailList.SelectedIndex < 0)
        {
            return;
        }

        _viewModel.NavigateToPage(ThumbnailList.SelectedIndex + 1);
    }

    private void OnOutlineSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (OutlineTree.SelectedItem is BookmarkViewModel bookmark)
        {
            _viewModel.NavigateToPage(bookmark.PageNumber);
        }
    }

    private void OnPageNumberKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            _viewModel.CommitPageNumberInput();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            _viewModel.PageNumberInput = _viewModel.CurrentPage.ToString();
            e.Handled = true;
        }
    }

    private void OnPageNumberLostFocus(object? sender, RoutedEventArgs e)
    {
        _viewModel.CommitPageNumberInput();
    }

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            if (_viewModel.SearchCommand.CanExecute(null))
            {
                _viewModel.SearchCommand.Execute(null);
            }

            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            _viewModel.CloseSearchCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnSelectionPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!_viewModel.IsDocumentOpen || _isSelectingText ||
            !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        var pageView = FindPageView(e.Source) ?? FindPageViewAt(e.GetPosition(this));
        if (pageView?.DataContext is not PageViewModel page)
        {
            return;
        }

        PagesList.Focus();

        var point = e.GetPosition(pageView);
        if (!new Rect(pageView.Bounds.Size).Contains(point))
        {
            return;
        }

        if (!_viewModel.BeginTextSelection(
                page.Number,
                point.X / Math.Max(1, pageView.Bounds.Width),
                point.Y / Math.Max(1, pageView.Bounds.Height),
                e.ClickCount))
        {
            return;
        }

        _isSelectingText = true;
        _selectionMoved = e.ClickCount >= 2;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    private void OnSelectionPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isSelectingText)
        {
            return;
        }

        var pageView = FindPageViewAt(e.GetPosition(this));
        if (pageView?.DataContext is PageViewModel page)
        {
            var point = e.GetPosition(pageView);
            if (_viewModel.UpdateTextSelection(
                    page.Number,
                    point.X / Math.Max(1, pageView.Bounds.Width),
                    point.Y / Math.Max(1, pageView.Bounds.Height)))
            {
                _selectionMoved = true;
            }
        }

        e.Handled = true;
    }

    private void OnSelectionPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isSelectingText)
        {
            return;
        }

        if (_selectionMoved)
        {
            _viewModel.CompleteTextSelection();
        }
        else
        {
            _viewModel.ClearTextSelection();
        }

        _isSelectingText = false;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    private void OnCopySelectionRequested(object? sender, string text)
    {
        _ = CopyTextAsync(text);
    }

    private async Task CopyTextAsync(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is not null)
            {
                await clipboard.SetTextAsync(text);
            }
        }
        catch
        {
            // Clipboard access can be denied by the desktop environment.
        }
    }

    private static PdfPageView? FindPageView(object? source)
    {
        if (source is PdfPageView pageView)
        {
            return pageView;
        }

        return (source as Visual)?.GetVisualAncestors().OfType<PdfPageView>().FirstOrDefault();
    }

    private PdfPageView? FindPageViewAt(Point windowPoint)
    {
        foreach (var pageView in PagesList.GetVisualDescendants().OfType<PdfPageView>())
        {
            var origin = pageView.TranslatePoint(new Point(0, 0), this);
            if (origin is null)
            {
                continue;
            }

            var bounds = new Rect(origin.Value, pageView.Bounds.Size);
            if (bounds.Contains(windowPoint))
            {
                return pageView;
            }
        }

        return null;
    }

    private void OnFitWidthMenuClick(object? sender, RoutedEventArgs e) =>
        _viewModel.FitWidthCommand.Execute(null);

    private void OnFitPageMenuClick(object? sender, RoutedEventArgs e) =>
        _viewModel.FitPageCommand.Execute(null);

    private void OnActualSizeMenuClick(object? sender, RoutedEventArgs e) =>
        _viewModel.ActualSizeCommand.Execute(null);

    private void OnZoomPresetMenuClick(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string value })
        {
            _viewModel.SetZoomCommand.Execute(value);
        }
    }

    private void OnLanguageMenuItemClick(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string language })
        {
            _viewModel.SetLanguageCommand.Execute(language);
        }
    }

    private void OnInfoFlyoutOpened(object? sender, EventArgs e)
    {
        if (sender is Flyout { Content: Control content })
        {
            content.DataContext = _viewModel;
        }
    }

    private void OnPagesPointerWheel(object? sender, PointerWheelEventArgs e)
    {
        if ((e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) == 0)
        {
            return;
        }

        if (e.Delta.Y > 0)
        {
            _viewModel.ZoomInCommand.Execute(null);
        }
        else if (e.Delta.Y < 0)
        {
            _viewModel.ZoomOutCommand.Execute(null);
        }

        e.Handled = true;
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control) ||
                   e.KeyModifiers.HasFlag(KeyModifiers.Meta);

        if (!(e.Source is TextBox) && ctrl && e.Key == Key.C && _viewModel.HasSelection)
        {
            _viewModel.CopySelectionCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (!(e.Source is TextBox) && ctrl && e.Key == Key.A && _viewModel.IsDocumentOpen)
        {
            _viewModel.SelectAllTextCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (ctrl && e.Key == Key.O)
        {
            if (_viewModel.OpenCommand.CanExecute(null))
            {
                _viewModel.OpenCommand.Execute(null);
            }

            e.Handled = true;
            return;
        }

        if (ctrl && e.Key == Key.W)
        {
            _viewModel.CloseDocumentCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (ctrl && e.Key == Key.F)
        {
            if (_viewModel.IsSearchOpen)
            {
                SearchBox.Focus();
                SearchBox.SelectAll();
            }
            else
            {
                _viewModel.ToggleSearchCommand.Execute(null);
            }

            e.Handled = true;
            return;
        }

        if (ctrl && e.KeyModifiers.HasFlag(KeyModifiers.Shift) && e.Key == Key.L)
        {
            _viewModel.ToggleLanguageCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (ctrl && (e.Key == Key.OemPlus || e.Key == Key.Add))
        {
            _viewModel.ZoomInCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (ctrl && (e.Key == Key.OemMinus || e.Key == Key.Subtract))
        {
            _viewModel.ZoomOutCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (ctrl && e.Key == Key.D0)
        {
            _viewModel.FitPageCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (ctrl && e.Key == Key.D1)
        {
            _viewModel.ActualSizeCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (ctrl && e.Key == Key.D2)
        {
            _viewModel.FitWidthCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape)
        {
            if (_viewModel.IsSearchOpen)
            {
                _viewModel.CloseSearchCommand.Execute(null);
            }
            else if (_viewModel.HasError)
            {
                _viewModel.DismissErrorCommand.Execute(null);
            }
            else if (WindowState == WindowState.FullScreen)
            {
                WindowState = WindowState.Normal;
            }

            e.Handled = true;
            return;
        }

        if (e.Key == Key.F11)
        {
            WindowState = WindowState == WindowState.FullScreen ? WindowState.Normal : WindowState.FullScreen;
            e.Handled = true;
        }
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        var pdf = GetDroppedPdf(e);
        e.DragEffects = pdf is null ? DragDropEffects.None : DragDropEffects.Copy;
        _viewModel.IsDragOver = pdf is not null;
        e.Handled = true;
    }

    private void OnDragLeave(object? sender, RoutedEventArgs e)
    {
        _viewModel.IsDragOver = false;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        _viewModel.IsDragOver = false;
        var path = GetDroppedPdf(e);
        if (path is not null)
        {
            _ = _viewModel.OpenPathAsync(path);
        }

        e.Handled = true;
    }

    private static string? GetDroppedPdf(DragEventArgs e)
    {
        var files = e.DataTransfer.TryGetFiles();
        if (files is null)
        {
            return null;
        }

        return files
            .Select(file => file.TryGetLocalPath())
            .FirstOrDefault(path =>
                path is not null &&
                string.Equals(Path.GetExtension(path), ".pdf", StringComparison.OrdinalIgnoreCase));
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        var maximized = WindowState == WindowState.Maximized;
        _viewModel.SaveWindowState(Width, Height, maximized);
    }
}
