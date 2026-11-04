using AirPdf.App.Layout;
using AirPdf.App.Services;
using AirPdf.App.ViewModels;
using AirPdf.Core.Models;
using AirPdf.Core.Services;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace AirPdf.App.Views;

public partial class MainWindow : Window
{
    private readonly AppServices _services;
    private readonly IWindowService _windows;
    private readonly WindowViewModel _tabs;
    private readonly SettingsService _settingsService;
    private readonly string? _initialFile;
    private MainWindowViewModel _viewModel = null!;
    private bool _suppressScrollSync;
    private bool _suppressThumbnailSync;
    private bool _thumbnailSyncPending;
    private bool _handlingScrollChanged;
    private bool _isSelectingText;
    private bool _selectionPressInProgress;
    private (int Page, double X, double Y)? _contextPoint;
    private (PageViewModel Page, Control View, List<(double X, double Y)> Points)? _inkStroke;
    private (PageViewModel Page, int Number)? _erasing;
    private readonly Cursor _hiddenCursor = new(StandardCursorType.None);
    private bool _selectionMoved;
    private bool _restoringDocument;
    private bool _linkCursorActive;
    private PixelPoint? _pendingPosition;
    private PageLayout _pageLayout = PageLayout.Empty;
    private const string BookmarkDragFormat = "airpdf/bookmark";
    private UserBookmarkViewModel? _draggedBookmark;
    private Point _bookmarkDragStart;
    private bool _bookmarkDragArmed;

    public MainWindow()
        : this(new AppServices(), NullWindowService.Instance, null, restoreWindowPosition: true)
    {
    }

    /// <summary>
    /// Creates a window over the process-wide services. Only the first window restores the
    /// remembered position; later windows are cascaded by <see cref="WindowService"/>.
    /// </summary>
    public MainWindow(
        AppServices services,
        IWindowService windows,
        string? initialFile,
        bool restoreWindowPosition)
    {
        InitializeComponent();

        _services = services;
        _windows = windows;
        _settingsService = services.SettingsService;
        var settings = services.Settings;

        _initialFile = initialFile;

        // The tab strip binds to the host, not to the active document's view model.
        _tabs = new WindowViewModel(CreateTabViewModel);
        _tabs.PropertyChanged += OnTabsPropertyChanged;
        TabStrip.DataContext = _tabs;

        // The host selects its home tab during construction, before this window could
        // subscribe, so point the content at it explicitly.
        ShowTab(_tabs.SelectedTab);
        // The colour submenu is generated from the palette, so its items are handled where they
        // bubble: the parent item owns one handler for all of them.
        HighlightMenu.AddHandler(MenuItem.ClickEvent, OnHighlightPaletteClick);
        StartRenderProbe();

        if (restoreWindowPosition)
        {
            Width = Math.Max(MinWidth, settings.WindowWidth);
            Height = Math.Max(MinHeight, settings.WindowHeight);

            if (settings.WindowMaximized)
            {
                WindowState = WindowState.Maximized;
            }
            else if (settings.WindowX is { } savedX && settings.WindowY is { } savedY)
            {
                _pendingPosition = new PixelPoint(savedX, savedY);
                TryApplyPendingPosition();
            }
        }

        PagesList.SizeChanged += (_, _) => UpdateViewport();

        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
        AddHandler(PointerPressedEvent, OnSelectionPointerPressed, RoutingStrategies.Tunnel);
        AddHandler(PointerMovedEvent, OnSelectionPointerMoved, RoutingStrategies.Tunnel);
        AddHandler(PointerReleasedEvent, OnSelectionPointerReleased, RoutingStrategies.Tunnel);
        AddHandler(PointerCaptureLostEvent, OnPointerCaptureLost, RoutingStrategies.Tunnel);
        PagesList.AddHandler(PointerWheelChangedEvent, OnPagesPointerWheel, RoutingStrategies.Tunnel);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
        AddHandler(DragDrop.DropEvent, OnDrop);
        DragDrop.SetAllowDrop(this, true);

        Opened += OnOpened;
        Closing += OnClosing;
        Closed += OnClosed;
    }

    private MainWindowViewModel CreateTabViewModel(WindowViewModel host, bool isHome)
    {
        var documents = new PdfDocumentService();

        return new MainWindowViewModel(
            _services,
            documents,
            new PdfSearchService(documents),
            new PdfTextService(documents),
            new DialogService(() => this, _services.Localization),
            _windows,
            host,
            isHome);
    }

    private void OnTabsPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(WindowViewModel.SelectedTab):
                ShowTab(_tabs.SelectedTab);
                break;
            case nameof(WindowViewModel.HasTabs):
                UpdateTabStripVisibility();
                break;
        }
    }

    private void UpdateTabStripVisibility()
    {
        TabStrip.IsVisible = _tabs.HasTabs;

        // Selecting the tab the user clicked has to survive the ListBox re-binding.
        if (!ReferenceEquals(TabList.SelectedItem, _tabs.SelectedTab))
        {
            TabList.SelectedItem = _tabs.SelectedTab;
        }
    }

    /// <summary>
    /// Points the window at a tab: rebinds the content, moves the event subscriptions, and
    /// restores that tab's scroll position. The outgoing tab keeps its view model, so its
    /// document, zoom, and reading position are still there when you come back.
    /// </summary>
    private void ShowTab(MainWindowViewModel? tab)
    {
        if (tab is null || ReferenceEquals(_viewModel, tab))
        {
            return;
        }

        if (_viewModel is not null)
        {
            CaptureScrollOffset();
            UnhookViewModel(_viewModel);
        }

        // Freeze scroll sync across the swap. Rebuilding the list for another document emits
        // ScrollChanged events, and by then _viewModel is the incoming tab, so an unsuppressed
        // event would write the outgoing tab's offset into it.
        _suppressScrollSync = true;

        // Read the incoming tab's position now, before any of that noise.
        var position = tab.CurrentPosition;

        _viewModel = tab;
        DataContext = tab;
        HookViewModel(tab);
        UpdateTabStripVisibility();

        if (InfoButton.Flyout is Flyout { Content: Control infoContent })
        {
            infoContent.DataContext = tab;
        }

        Dispatcher.UIThread.Post(
            () =>
            {
                if (!ReferenceEquals(_viewModel, tab))
                {
                    return;
                }

                _restoringDocument = true;
                UpdateViewport();
                RebuildPageLayout();
                ScrollToReadingPosition(position.Page, position.PageOffset);
                UpdateThumbnailSelection();

                // ApplyScrollOffset clears the flag after its own delay; do it again so a
                // tab with no pages (or a failed restore) cannot leave sync frozen.
                DispatcherTimer.RunOnce(
                    () =>
                    {
                        _suppressScrollSync = false;
                        _restoringDocument = false;
                    },
                    TimeSpan.FromMilliseconds(240));
            },
            DispatcherPriority.Loaded);
    }

    private void HookViewModel(MainWindowViewModel viewModel)
    {
        viewModel.ScrollToPageRequested += OnScrollToPageRequested;
        viewModel.ScrollToSearchResultRequested += OnScrollToSearchResultRequested;
        viewModel.ReturnToPositionRequested += OnReturnToPositionRequested;
        viewModel.CopySelectionRequested += OnCopySelectionRequested;
        viewModel.DocumentOpened += OnDocumentOpened;
        viewModel.ZoomChanged += OnZoomChanged;
        viewModel.SearchOpened += OnSearchOpened;
    }

    private void UnhookViewModel(MainWindowViewModel viewModel)
    {
        viewModel.ScrollToPageRequested -= OnScrollToPageRequested;
        viewModel.ScrollToSearchResultRequested -= OnScrollToSearchResultRequested;
        viewModel.ReturnToPositionRequested -= OnReturnToPositionRequested;
        viewModel.CopySelectionRequested -= OnCopySelectionRequested;
        viewModel.DocumentOpened -= OnDocumentOpened;
        viewModel.ZoomChanged -= OnZoomChanged;
        viewModel.SearchOpened -= OnSearchOpened;
    }

    /// <summary>Records where the outgoing tab was scrolled to before its view goes away.</summary>
    private void CaptureScrollOffset()
    {
        var viewer = PagesList.Viewer;
        if (viewer is null || !_viewModel.IsDocumentOpen)
        {
            return;
        }

        EnsurePageLayout();
        if (_pageLayout.Count == 0)
        {
            return;
        }

        var page = _pageLayout.GetPageAtViewportTop(viewer.Offset.Y, viewer.Viewport.Height);
        _viewModel.UpdateCurrentPageFromScroll(page, _pageLayout.GetPageOffset(page, viewer.Offset.Y));
    }

    private void OnNewTabClick(object? sender, RoutedEventArgs e) => _tabs.AddTab();

    private void OnOpenInNewTabClick(object? sender, RoutedEventArgs e)
    {
        if (_viewModel.IsDocumentOpen && !string.IsNullOrWhiteSpace(_viewModel.FilePath))
        {
            _tabs.AddTab(_viewModel.FilePath);
        }
    }

    private void OnCloseTabClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: MainWindowViewModel tab })
        {
            e.Handled = true;
            _tabs.CloseTab(tab);
        }
    }

    private void OnTabSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (TabList.SelectedItem is MainWindowViewModel selected)
        {
            _tabs.SelectedTab = selected;
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        // The host owns every tab, so disposing it disposes each document handle.
        _tabs.PropertyChanged -= OnTabsPropertyChanged;
        _tabs.Dispose();
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        // Screens and the native window handle are only guaranteed once the window is
        // shown, so the restored position is retried here if the constructor could not
        // validate it yet.
        TryApplyPendingPosition();
        UpdateViewport();
        if (!string.IsNullOrWhiteSpace(_initialFile))
        {
            _ = _viewModel.OpenPathAsync(_initialFile);
        }
    }

    private void TryApplyPendingPosition()
    {
        if (_pendingPosition is not { } position)
        {
            return;
        }

        switch (IsPositionReachable(position))
        {
            case null:
                // Screen information is not available yet; OnOpened retries.
                return;
            case false:
                // The saved monitor is gone, so let the platform place the window
                // rather than restoring it off-screen.
                _pendingPosition = null;
                return;
        }

        try
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = position;
            _pendingPosition = null;
        }
        catch
        {
            // Platforms that cannot position a window before it is shown will retry from
            // OnOpened; a persistent failure simply keeps the platform's own placement.
        }
    }

    /// <summary>
    /// Null means "cannot tell yet" (no screen information), which must not be confused
    /// with "off-screen" — only the latter should discard the saved position.
    /// </summary>
    private bool? IsPositionReachable(PixelPoint position)
    {
        try
        {
            var screens = Screens;
            if (screens is null)
            {
                return null;
            }

            var sawScreen = false;
            foreach (var screen in screens.All)
            {
                sawScreen = true;
                var area = screen.WorkingArea;

                // Require enough of the title bar to stay on a real screen, so a window
                // saved on a since-disconnected monitor cannot come back off-screen.
                if (position.X + 160 > area.X &&
                    position.X < area.X + area.Width - 80 &&
                    position.Y + 60 > area.Y &&
                    position.Y < area.Y + area.Height - 40)
                {
                    return true;
                }
            }

            return sawScreen ? false : null;
        }
        catch
        {
            return null;
        }
    }

    private void OnDocumentOpened(object? sender, EventArgs e)
    {
        // Posted work must not touch a tab the user has already switched away from.
        var viewModel = _viewModel;
        var openPosition = viewModel.TakePendingOpenPosition();
        var readingPosition = viewModel.TakePendingReadingPosition();

        RestoreDocumentView(viewModel, openPosition, readingPosition, attempt: 0);
    }

    /// <summary>
    /// Applies zoom, layout and the starting scroll once the page list has a size.
    /// </summary>
    /// <remarks>
    /// A tab can be opened before its list has been laid out - the width and height come back as
    /// zero for a moment - and applying the fit-width zoom against a zero viewport produces a
    /// tiny page and a scroll offset that belongs to nothing. Retrying until the list has a size
    /// costs nothing when it already does.
    /// </remarks>
    private void RestoreDocumentView(
        MainWindowViewModel viewModel,
        NavigationPoint? openPosition,
        ReadingPositionEntry? readingPosition,
        int attempt)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!ReferenceEquals(_viewModel, viewModel))
            {
                return;
            }

            if ((PagesList.Bounds.Width < 1 || PagesList.Bounds.Height < 1) && attempt < 8)
            {
                // Background runs after the layout and render passes, which is where the list
                // finally has a size; retrying at Loaded could spin before layout ever runs.
                Dispatcher.UIThread.Post(
                    () => RestoreDocumentView(viewModel, openPosition, readingPosition, attempt + 1),
                    DispatcherPriority.Background);
                return;
            }

            _restoringDocument = true;
            UpdateViewport();
            _viewModel.ApplyInitialZoom();
            RebuildPageLayout();

            if (openPosition is { } jumpTarget)
            {
                ScrollToReadingPosition(jumpTarget.Page, jumpTarget.PageOffset);
            }
            else if (readingPosition is not null)
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
            RebuildPageLayout();
            return;
        }

        RebuildPageLayout();
        var viewModel = _viewModel;
        var page = viewModel.CurrentPage;
        Dispatcher.UIThread.Post(
            () =>
            {
                if (ReferenceEquals(_viewModel, viewModel))
                {
                    ScrollToPage(page);
                }
            },
            DispatcherPriority.Loaded);
    }

    private void OnScrollToPageRequested(object? sender, int pageNumber)
    {
        var viewModel = _viewModel;
        Dispatcher.UIThread.Post(
            () =>
            {
                if (ReferenceEquals(_viewModel, viewModel))
                {
                    ScrollToPage(pageNumber);
                }
            },
            DispatcherPriority.Loaded);
    }

    private void OnScrollToSearchResultRequested(object? sender, SearchResultViewModel result)
    {
        var viewModel = _viewModel;
        Dispatcher.UIThread.Post(
            () =>
            {
                if (ReferenceEquals(_viewModel, viewModel))
                {
                    ScrollToSearchResult(result);
                }
            },
            DispatcherPriority.Loaded);
    }

    private void OnReturnToPositionRequested(object? sender, NavigationPoint point)
    {
        var viewModel = _viewModel;
        Dispatcher.UIThread.Post(
            () =>
            {
                if (ReferenceEquals(_viewModel, viewModel))
                {
                    ScrollToReadingPosition(point.Page, point.PageOffset);
                }
            },
            DispatcherPriority.Loaded);
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

    // Diagnostic mode (AIRPDF_PROBE=1): appends one block per document open, plus Ctrl+Shift+D
    // on demand, so a failure that happens after the second open can be captured.
    private static readonly bool ProbeEnabled = Environment.GetEnvironmentVariable("AIRPDF_PROBE") == "1";

    /// <summary>Identifies one run, so a stale block cannot be mistaken for a fresh one.</summary>
    private static readonly string ProbeRunId = DateTime.Now.ToString("HHmmss");

    /// <summary>Path the diagnostic appends to.</summary>
    private static readonly string ProbePath =
        Path.Combine(Path.GetTempPath(), "airpdf-render-probe.txt");

    private void StartRenderProbe()
    {
        if (!ProbeEnabled)
        {
            return;
        }

        DumpRenderState("startup");

        foreach (var tab in _tabs.Tabs)
        {
            tab.DocumentOpened += (_, _) => DispatcherTimer.RunOnce(
                () => DumpRenderState($"opened {tab.FileName}"),
                TimeSpan.FromSeconds(3));
        }

        _tabs.Tabs.CollectionChanged += (_, args) =>
        {
            foreach (var tab in args.NewItems?.OfType<MainWindowViewModel>() ?? [])
            {
                tab.DocumentOpened += (_, _) => DispatcherTimer.RunOnce(
                    () => DumpRenderState($"opened {tab.FileName}"),
                    TimeSpan.FromSeconds(3));
            }
        };
    }

    /// <summary>
    /// Appends one block describing what the page list is actually doing. Always available from
    /// Ctrl+Shift+D so a report of "blank pages" can be captured at the moment it happens,
    /// without asking the reader to launch the app any particular way.
    /// </summary>
    private void DumpRenderState(string stage)
    {
        try
        {
            var lines = new List<string>
            {
                $"--- {DateTime.Now:HH:mm:ss} run={ProbeRunId} {stage} ---",
                $"window {Bounds.Width:F0}x{Bounds.Height:F0} documentOpen={_viewModel.IsDocumentOpen} file='{_viewModel.FileName}' pages={_viewModel.Pages.Count} zoom={_viewModel.Zoom:F3} current={_viewModel.CurrentPage}",
                $"viewer offset={PagesList.Viewer?.Offset.Y ?? -1:F1} extent={PagesList.Viewer?.Extent.Height ?? -1:F1} viewport={PagesList.Viewer?.Viewport.Height ?? -1:F1} listActive={PagesList.IsVisible} listBounds={PagesList.Bounds.Width:F0}x{PagesList.Bounds.Height:F0}",
                $"realized={PagesList.GetRealizedContainers().Count()} items={PagesList.ItemCount} error={(_viewModel.HasError ? _viewModel.ErrorMessage : "none")}"
            };

            foreach (var page in _viewModel.Pages.Take(4))
            {
                lines.Add(
                    $"page {page.Number}: active={page.IsActive} image={(page.Image is null ? "null" : $"{page.Image.PixelSize.Width}x{page.Image.PixelSize.Height}")} placeholder={page.ShowPlaceholder} rendering={page.IsRendering} error='{page.RenderError}'");
            }

            File.AppendAllLines(ProbePath, lines);
        }
        catch
        {
            // Diagnostics must never break the app.
        }
    }
    private void OnPagesScrollChanged(object? sender, EventArgs e)
    {
        if (_suppressScrollSync || !_viewModel.IsDocumentOpen)
        {
            return;
        }

        // Laying out a page can invalidate layout again, which raises this event while it is
        // still being handled; recursing through it ends in a stack overflow.
        if (_handlingScrollChanged)
        {
            CrashLog.Write("NestedPagesScrollChanged", null);
            return;
        }

        _handlingScrollChanged = true;
        try
        {
            HandlePagesScrollChanged();
        }
        finally
        {
            _handlingScrollChanged = false;
        }
    }

    private void HandlePagesScrollChanged()
    {

        var viewer = PagesList.Viewer;
        if (viewer is null || viewer.Extent.Height <= 0)
        {
            return;
        }

        EnsurePageLayout();
        if (_pageLayout.Count == 0)
        {
            return;
        }

        var page = _pageLayout.GetPageAtViewportTop(viewer.Offset.Y, viewer.Viewport.Height);
        var fraction = _pageLayout.GetPageOffset(page, viewer.Offset.Y);
        _viewModel.UpdateCurrentPageFromScroll(page, fraction);
        UpdateThumbnailSelection();
    }

    private void RebuildPageLayout()
    {
        _pageLayout = new PageLayout(
            _viewModel.Pages.Select(page => page.DisplayHeight).ToList());
    }

    private void EnsurePageLayout()
    {
        if (_pageLayout.Count != _viewModel.Pages.Count)
        {
            RebuildPageLayout();
        }
    }

    private void ScrollToPage(int pageNumber, bool center = true, int attempt = 0)
    {
        if (_viewModel.Pages.Count == 0)
        {
            return;
        }

        EnsurePageLayout();
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
        var targetPage = index + 1;
        var top = _pageLayout.GetTop(targetPage);
        var page = _viewModel.Pages[index];
        var target = center
            ? top - Math.Max(12, (viewer.Viewport.Height - page.DisplayHeight) / 2)
            : top - 12;
        var fraction = Math.Clamp((target - top) / Math.Max(1, page.DisplayHeight), 0, 1);

        ApplyScrollOffset(target, targetPage, fraction);
    }

    private void ScrollToReadingPosition(int pageNumber, double fraction, int attempt = 0)
    {
        if (_viewModel.Pages.Count == 0)
        {
            return;
        }

        EnsurePageLayout();
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
        var top = _pageLayout.GetTop(page.Number);
        var normalized = Math.Clamp(fraction, 0, 1);
        var target = top + (normalized * page.DisplayHeight);

        // Scrolling past the current extent is silently clamped, and then the page never gets
        // realised: that is the blank page seen after jumping from the welcome screen, where
        // the list has not been measured yet.
        if (viewer.Extent.Height < target + 1 && attempt < 4)
        {
            Dispatcher.UIThread.Post(
                () => ScrollToReadingPosition(pageNumber, fraction, attempt + 1),
                DispatcherPriority.Background);
            return;
        }

        ApplyScrollOffset(target, page.Number, normalized);
    }

    private void ScrollToSearchResult(SearchResultViewModel result)
    {
        if (result.PageNumber < 1 || result.PageNumber > _viewModel.Pages.Count)
        {
            return;
        }

        EnsurePageLayout();
        var viewer = PagesList.Viewer;
        if (viewer is null)
        {
            return;
        }

        var page = _viewModel.Pages[result.PageNumber - 1];
        var top = _pageLayout.GetTop(result.PageNumber);

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

    /// <summary>
    /// Coalesces thumbnail selection updates and runs them outside the current layout pass.
    /// The scroll handler fires while the scroll viewer is laying out, and
    /// ListBox.ScrollIntoView invalidates layout, so calling it from there recursed until the
    /// stack overflowed - which is how scrolling after a tab switch killed the process.
    /// </summary>
    private void UpdateThumbnailSelection()
    {
        if (_thumbnailSyncPending)
        {
            return;
        }

        _thumbnailSyncPending = true;
        Dispatcher.UIThread.Post(
            () =>
            {
                _thumbnailSyncPending = false;
                UpdateThumbnailSelectionCore();
            },
            DispatcherPriority.Background);
    }

    private void UpdateThumbnailSelectionCore()
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
        if (OutlineTree.SelectedItem is OutlineItemViewModel outlineItem)
        {
            _viewModel.NavigateToPage(outlineItem.PageNumber);
        }
    }

    private void OnBookmarkListSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (BookmarkList.SelectedItem is UserBookmarkViewModel bookmark)
        {
            _viewModel.GoToBookmarkCommand.Execute(bookmark);

            // Clear the selection so clicking the same bookmark again still navigates.
            BookmarkList.SelectedItem = null;
        }
    }

    private void OnRenameBookmarkClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: UserBookmarkViewModel bookmark })
        {
            e.Handled = true;
            _viewModel.RenameBookmarkCommand.Execute(bookmark);
        }
        else if (sender is Control { Tag: UserBookmarkViewModel tagged })
        {
            e.Handled = true;
            _viewModel.RenameBookmarkCommand.Execute(tagged);
        }
    }

    private void OnMoveBookmarkUpClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { Tag: UserBookmarkViewModel bookmark })
        {
            e.Handled = true;
            _viewModel.MoveBookmarkUpCommand.Execute(bookmark);
        }
    }

    private void OnMoveBookmarkDownClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { Tag: UserBookmarkViewModel bookmark })
        {
            e.Handled = true;
            _viewModel.MoveBookmarkDownCommand.Execute(bookmark);
        }
    }

    private void OnOpenOverviewBookmarkClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { Tag: BookmarkOverviewViewModel bookmark })
        {
            e.Handled = true;
            _viewModel.OpenBookmarkCommand.Execute(bookmark);
        }
    }

    // Avalonia 11.3 marks the synchronous drag API obsolete in favour of IAsyncDataTransfer and
    // DataFormat<T>. That is a redesign rather than a rename: the payload becomes async and the
    // format is typed. Drag and drop cannot be exercised in a headless test, so the migration
    // waits until it can be checked by hand; until then these four handlers opt out, deliberately.
#pragma warning disable CS0618
    private void OnBookmarkPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { DataContext: UserBookmarkViewModel bookmark })
        {
            return;
        }

        _draggedBookmark = bookmark;
        _bookmarkDragStart = e.GetPosition(BookmarkList);
        _bookmarkDragArmed = true;
    }

    private async void OnBookmarkPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_bookmarkDragArmed || _draggedBookmark is null)
        {
            return;
        }

        if (!e.GetCurrentPoint(BookmarkList).Properties.IsLeftButtonPressed)
        {
            _bookmarkDragArmed = false;
            return;
        }

        var position = e.GetPosition(BookmarkList);
        if (Math.Abs(position.Y - _bookmarkDragStart.Y) < 6 && Math.Abs(position.X - _bookmarkDragStart.X) < 6)
        {
            return;
        }

        _bookmarkDragArmed = false;

        var data = new DataObject();
        data.Set(BookmarkDragFormat, _draggedBookmark);
        try
        {
            await DragDrop.DoDragDrop(e, data, DragDropEffects.Move);
        }
        finally
        {
            _draggedBookmark = null;
        }
    }

    private void OnBookmarkDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.Data.Contains(BookmarkDragFormat) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnBookmarkDrop(object? sender, DragEventArgs e)
    {
        if (!e.Data.Contains(BookmarkDragFormat)
            || e.Data.Get(BookmarkDragFormat) is not UserBookmarkViewModel dragged)
        {
            return;
        }

        var target = FindBookmark(e.Source);
        if (target is not null)
        {
            var index = -1;
            for (var i = 0; i < _viewModel.Bookmarks.Count; i++)
            {
                if (ReferenceEquals(_viewModel.Bookmarks[i].Entry, target.Entry))
                {
                    index = i;
                    break;
                }
            }

            if (index >= 0)
            {
                _viewModel.MoveBookmarkTo(dragged, index);
            }
        }

        _draggedBookmark = null;
        e.Handled = true;
    }

#pragma warning restore CS0618

    /// <summary>Walks up from whatever the pointer landed on to the bookmark it belongs to.</summary>
    private static UserBookmarkViewModel? FindBookmark(object? source)
    {
        var visual = source as Visual;
        while (visual is not null)
        {
            if (visual is ListBoxItem { DataContext: UserBookmarkViewModel bookmark })
            {
                return bookmark;
            }

            visual = visual.GetVisualParent() as Visual;
        }

        return null;
    }
    private void OnRemoveBookmarkClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { Tag: UserBookmarkViewModel bookmark })
        {
            e.Handled = true;
            _viewModel.RemoveBookmarkCommand.Execute(bookmark);
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

    private async void OnSelectionPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // A press while a context menu is open belongs to the menu: close it and let the click
        // fall through. Marking it handled here is what kept the menu on screen, because the
        // popup never saw the press it dismisses itself with.
        if (CloseOpenMenus())
        {
            return;
        }

        if (!_viewModel.IsDocumentOpen)
        {
            return;
        }

        // A right click records where the context menu was opened, so "Remove highlight" knows
        // which highlight the reader meant. The menu itself opens as usual.
        if (e.GetCurrentPoint(this).Properties.IsRightButtonPressed)
        {
            RememberContextPoint(e);
            return;
        }

        if (_isSelectingText || _selectionPressInProgress ||
            !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (_viewModel.IsEraserMode && IsInsidePageList(e.Source))
        {
            HidePalettes();

            var eraseView = FindPageView(e.Source);
            if (eraseView?.DataContext is PageViewModel erasePage)
            {
                var start = e.GetPosition(eraseView);
                if (new Rect(eraseView.Bounds.Size).Contains(start))
                {
                    _erasing = (erasePage, erasePage.Number);
                    _viewModel.BeginErase();
                    _ = EraseAsync(erasePage.Number, start.X / Math.Max(1, eraseView.Bounds.Width), start.Y / Math.Max(1, eraseView.Bounds.Height));
                    e.Pointer.Capture(PagesList);
                    e.Handled = true;
                    return;
                }
            }
        }

        // Only a press that starts inside the page list can begin a stroke. Testing the pointer
        // position instead let a press on the toolbar count as a stroke when a page happened to
        // be laid out underneath it, which swallowed the click and made the buttons dead.
        if (_viewModel.IsInkMode && IsInsidePageList(e.Source))
        {
            HidePalettes();

            var inkView = FindPageView(e.Source);
            if (inkView?.DataContext is PageViewModel inkPage)
            {
                var start = e.GetPosition(inkView);
                if (new Rect(inkView.Bounds.Size).Contains(start))
                {
                    var normalized = (
                        Math.Clamp(start.X / Math.Max(1, inkView.Bounds.Width), 0, 1),
                        Math.Clamp(start.Y / Math.Max(1, inkView.Bounds.Height), 0, 1));

                    _inkStroke = (inkPage, inkView, [normalized]);
                    inkPage.SetLiveInk(_inkStroke.Value.Points);
                    e.Pointer.Capture(PagesList);
                    e.Handled = true;
                    return;
                }
            }
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

        var pageNumber = page.Number;
        var clickCount = e.ClickCount;
        var x = point.X / Math.Max(1, pageView.Bounds.Width);
        var y = point.Y / Math.Max(1, pageView.Bounds.Height);

        // A link annotation wins over selection: clicking it should follow it, not start a
        // drag. The hand cursor already told the user it was clickable.
        var link = page.HitTestLink(x, y);
        if (link is not null)
        {
            _viewModel.OpenLink(link);
            e.Handled = true;
            return;
        }

        // A note is an annotation, not part of the page, so nothing on the page can be clicked to
        // read it. The click is checked against the notes before it becomes a selection.
        if (await _viewModel.TryShowNoteAtAsync(pageNumber, x, y))
        {
            ShowNotePopup();
            e.Handled = true;
            return;
        }

        bool began;
        _selectionPressInProgress = true;
        try
        {
            began = await _viewModel.BeginTextSelectionAsync(pageNumber, x, y, clickCount);
        }
        catch
        {
            return;
        }
        finally
        {
            _selectionPressInProgress = false;
        }

        if (!began || !_viewModel.IsDocumentOpen)
        {
            // A click that landed on no text is a click on the page: the previous selection
            // should go, which is also how a reader cancels a select-all.
            _viewModel.ClearTextSelection();
            return;
        }

        bool stillPressed;
        try
        {
            stillPressed = e.GetCurrentPoint(this).Properties.IsLeftButtonPressed;
        }
        catch
        {
            return;
        }

        if (!stillPressed)
        {
            _viewModel.ClearTextSelection();
            return;
        }

        _isSelectingText = true;
        _selectionMoved = clickCount >= 2;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    private void OnSelectionPointerMoved(object? sender, PointerEventArgs e)
    {
        UpdatePointerGlyph(e);

        if (_erasing is { } erasing)
        {
            var view = FindPageView(e.Source) ?? FindPageViewAt(e.GetPosition(this));
            if (view?.DataContext is PageViewModel erasePage && erasePage.Number == erasing.Number)
            {
                var point = e.GetPosition(view);
                if (new Rect(view.Bounds.Size).Contains(point))
                {
                    _ = EraseAsync(
                        erasePage.Number,
                        point.X / Math.Max(1, view.Bounds.Width),
                        point.Y / Math.Max(1, view.Bounds.Height));
                }
            }

            e.Handled = true;
            return;
        }

        if (_inkStroke is { } drawing)
        {
            var point = e.GetPosition(drawing.View);
            var x = Math.Clamp(point.X / Math.Max(1, drawing.View.Bounds.Width), 0, 1);
            var y = Math.Clamp(point.Y / Math.Max(1, drawing.View.Bounds.Height), 0, 1);
            var last = drawing.Points[^1];

            // Skip points that would not show up anyway; a stroke can collect hundreds.
            if (Math.Abs(x - last.X) + Math.Abs(y - last.Y) >= 0.002)
            {
                drawing.Points.Add((x, y));
                drawing.Page.SetLiveInk(drawing.Points);
            }

            e.Handled = true;
            return;
        }

        if (!_isSelectingText)
        {
            UpdateLinkCursor(e.GetPosition(this));
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

    private void UpdateLinkCursor(Point windowPoint)
    {
        PdfLink? link = null;

        if (_viewModel.IsDocumentOpen && FindPageViewAt(windowPoint) is { } pageView &&
            pageView.DataContext is PageViewModel page &&
            this.TranslatePoint(windowPoint, pageView) is { } local)
        {
            link = page.HitTestLink(
                local.X / Math.Max(1, pageView.Bounds.Width),
                local.Y / Math.Max(1, pageView.Bounds.Height));
        }

        var active = link is not null;
        if (active == _linkCursorActive)
        {
            return;
        }

        _linkCursorActive = active;
        Cursor = active ? new Cursor(StandardCursorType.Hand) : Cursor.Default;
    }

    private async Task EraseAsync(int pageNumber, double x, double y)
    {
        try
        {
            await _viewModel.EraseAtAsync(pageNumber, x, y);
        }
        catch (Exception unexpected)
        {
            CrashLog.Write("Erase", unexpected);
        }
    }

    /// <summary>
    /// Draws the tool under the pointer and hides the system cursor, but only where the tool would
    /// do something: over a page. Anywhere else the ordinary arrow stays.
    /// </summary>
    private void UpdatePointerGlyph(PointerEventArgs e)
    {
        var drawing = _viewModel.IsInkMode || _viewModel.IsEraserMode;
        var pageView = drawing ? FindPageView(e.Source) ?? FindPageViewAt(e.GetPosition(this)) : null;
        var overPage = pageView is not null && pageView.DataContext is PageViewModel;

        if (!drawing || !overPage)
        {
            if (PointerLayer.IsVisible)
            {
                PointerLayer.IsVisible = false;
            }

            Cursor = Cursor.Default;
            return;
        }

        var position = e.GetPosition(PointerLayer);
        Canvas.SetLeft(PointerGlyph, position.X + 2);
        Canvas.SetTop(PointerGlyph, position.Y + 2);
        PointerGlyph.Data = this.FindResource(_viewModel.IsEraserMode ? "IconEraser" : "IconPen") as Geometry;
        PointerLayer.IsVisible = true;
        Cursor = _hiddenCursor;
    }
    /// <summary>
    /// A capture can be lost without a release - the window loses focus, or the pointer leaves
    /// the surface. The half-drawn stroke and its capture have to go with it, or the next press
    /// anywhere in the window never reaches the control under it.
    /// </summary>
    private void OnPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (_inkStroke is not { } drawing)
        {
            return;
        }

        _inkStroke = null;
        drawing.Page.ClearLiveInk();
    }
    private void OnSelectionPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_erasing is not null)
        {
            _erasing = null;
            _viewModel.EndErase();
            e.Pointer.Capture(null);
            e.Handled = true;
            return;
        }

        if (_inkStroke is { } drawing)
        {
            _inkStroke = null;
            e.Pointer.Capture(null);
            e.Handled = true;

            if (drawing.Points.Count >= 2)
            {
                // The stroke stays on screen until the page is redrawn with it baked in; clearing
                // it here is what made it disappear and reappear.
                drawing.Page.ClearLiveInkAfterRender();
                _ = _viewModel.AddInkAtAsync(drawing.Page.Number, drawing.Points);
            }
            else
            {
                drawing.Page.ClearLiveInk();
            }

            return;
        }

        if (!_isSelectingText)
        {
            return;
        }

        if (_selectionMoved || _viewModel.HasSelection)
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

    /// <summary>True when the control that raised the event sits inside the page list.</summary>
    private bool IsInsidePageList(object? source)
    {
        if (source is not Visual start)
        {
            return false;
        }

        Visual? visual = start;
        while (visual is not null)
        {
            if (ReferenceEquals(visual, PagesList))
            {
                return true;
            }

            visual = visual.GetVisualParent() as Visual;
        }

        return false;
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

    /// <summary>
    /// Closes any open context menu, including the ones inside list item templates.
    /// </summary>
    /// <returns>True when something was open and has been closed.</returns>
    /// <remarks>
    /// The menus cannot be found by walking the window: a popup is its own visual root, so the
    /// menu object is not a descendant of the control it was attached to. They are looked up from
    /// the controls that own them instead - the page list's menu and whatever each realised
    /// bookmark item carries.
    /// </remarks>
    private bool CloseOpenMenus()
    {
        var closed = false;

        void Close(ContextMenu? menu)
        {
            if (menu is { IsOpen: true })
            {
                menu.Close();
                closed = true;
            }
        }

        Close(PagesList.ContextMenu);

        foreach (var container in BookmarkList.GetRealizedContainers())
        {
            if (container is not Control control)
            {
                continue;
            }

            Close(control.ContextMenu);
            foreach (var descendant in control.GetVisualDescendants().OfType<Control>())
            {
                Close(descendant.ContextMenu);
            }
        }

        foreach (var control in this.GetVisualDescendants().OfType<Control>())
        {
            Close(control.ContextMenu);
        }

        // The note text and the palettes are popups too, and a press is how they are dismissed.
        if (Resources["NotePopup"] is Flyout note && note.IsOpen)
        {
            note.Hide();
            _viewModel.ClearNoteText();
            closed = true;
        }

        return closed;
    }
    private void RememberContextPoint(PointerPressedEventArgs e)
    {
        var pageView = FindPageView(e.Source) ?? FindPageViewAt(e.GetPosition(this));
        if (pageView?.DataContext is not PageViewModel page)
        {
            _contextPoint = null;
            return;
        }

        var point = e.GetPosition(pageView);
        if (!new Rect(pageView.Bounds.Size).Contains(point))
        {
            _contextPoint = null;
            return;
        }

        var x = point.X / Math.Max(1, pageView.Bounds.Width);
        var y = point.Y / Math.Max(1, pageView.Bounds.Height);

        _contextPoint = (page.Number, x, y);

        // Right-clicking away from the selection clears it; right-clicking on it keeps it, which
        // is what makes "Highlight selection" usable from the menu.
        var insideSelection = page.SelectionRects.Any(rect =>
            x >= rect.X && x <= rect.X + rect.Width && y >= rect.Y && y <= rect.Y + rect.Height);

        if (!insideSelection && _viewModel.HasSelection)
        {
            _viewModel.ClearTextSelection();
        }

        // The menu opens with the right-click, so the hit test runs alongside it and the items
        // that do not apply are hidden as soon as the answer arrives.
        _ = _viewModel.UpdateContextTargetAsync(page.Number, x, y);
    }

    private void OnHighlightSwatchClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { Tag: MarkupColor color })
        {
            _viewModel.SetHighlightColor(color);

            // Picking a colour with text selected means "highlight it in this colour".
            if (_viewModel.HighlightSelectionCommand.CanExecute(null))
            {
                _viewModel.HighlightSelectionCommand.Execute(null);
            }
        }

        HidePalettes();
    }

    private void OnInkSwatchClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { Tag: MarkupColor color })
        {
            _viewModel.UseInkColor(color);
        }

        HidePalettes();
    }

    /// <summary>
    /// Opens the highlight colours at the point the page menu was opened at. The flyout is a
    /// window resource rather than something attached to the menu item, because the menu closes
    /// as soon as the item is activated and takes an attached flyout with it.
    /// </summary>
    private void OnHighlightMenuClick(object? sender, RoutedEventArgs e)
    {
        if (Resources["HighlightPalette"] is not Flyout flyout)
        {
            return;
        }

        if (flyout.Content is Control content)
        {
            content.DataContext = _viewModel;
        }

        flyout.ShowAt(PagesList);
    }

    /// <summary>
    /// The pen button: while drawing, a press leaves the mode and consumes the click so the colours
    /// do not open; otherwise the click starts drawing and the button opens its own palette.
    /// </summary>
    private void OnInkButtonPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!_viewModel.IsInkMode || e.GetCurrentPoint(this).Properties.IsRightButtonPressed)
        {
            return;
        }

        _viewModel.ToggleInkModeCommand.Execute(null);
        HidePalettes();
        e.Handled = true;
    }

    private void OnInkButtonClick(object? sender, RoutedEventArgs e)
    {
        if (!_viewModel.IsInkMode)
        {
            _viewModel.ToggleInkModeCommand.Execute(null);
            return;
        }

        // A press should have caught this; if a click arrives anyway, leave the mode rather than
        // leaving the reader stuck in it.
        _viewModel.ToggleInkModeCommand.Execute(null);
        HidePalettes();
    }

    /// <summary>Applies a colour picked from the highlight submenu.</summary>
    private void OnHighlightPaletteClick(object? sender, RoutedEventArgs e)
    {
        if (e.Source is not MenuItem { DataContext: MarkupColor color })
        {
            return;
        }

        _viewModel.SetHighlightColor(color);

        if (_viewModel.HighlightSelectionCommand.CanExecute(null))
        {
            _viewModel.HighlightSelectionCommand.Execute(null);
        }
    }
    private void HidePalettes()
    {
        if (InkButton.Flyout is { } ink)
        {
            ink.Hide();
        }


    }
    private async void OnAddNoteClick(object? sender, RoutedEventArgs e)
    {
        if (_contextPoint is not { } point)
        {
            return;
        }

        await _viewModel.AddNoteAtAsync(point.Page, point.X, point.Y);
        _contextPoint = null;
    }

    /// <summary>Shows the text of the note that was clicked.</summary>
    private void ShowNotePopup()
    {
        if (Resources["NotePopup"] is not Flyout flyout)
        {
            return;
        }

        if (flyout.Content is Control content)
        {
            content.DataContext = _viewModel;
        }

        flyout.ShowAt(PagesList);
    }
    private void OnShowNoteClick(object? sender, RoutedEventArgs e)
    {
        _viewModel.ShowNoteUnderPointer();
        ShowNotePopup();
    }

    private async void OnRemoveMarkupClick(object? sender, RoutedEventArgs e)
    {
        if (_contextPoint is not { } point)
        {
            return;
        }

        await _viewModel.RemoveMarkupAtAsync(point.Page, point.X, point.Y);
        _contextPoint = null;
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

        if (!ctrl && e.KeyModifiers.HasFlag(KeyModifiers.Alt) && e.Key == Key.Left)
        {
            if (_viewModel.CanNavigateBack)
            {
                _viewModel.NavigateBackCommand.Execute(null);
            }

            e.Handled = true;
            return;
        }

        if (e.Key == Key.F3)
        {
            if (_viewModel.HasSearchResults)
            {
                if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
                {
                    _viewModel.PreviousSearchResultCommand.Execute(null);
                }
                else
                {
                    _viewModel.NextSearchResultCommand.Execute(null);
                }
            }
            else if (_viewModel.IsDocumentOpen && !_viewModel.IsSearchOpen)
            {
                // F3 with nothing to step through brings the search panel up, like a browser.
                _viewModel.ToggleSearchCommand.Execute(null);
            }

            e.Handled = true;
            return;
        }

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

        if (ctrl && e.Key == Key.T)
        {
            _tabs.AddTab();
            e.Handled = true;
            return;
        }

        if (ctrl && e.Key == Key.Tab)
        {
            _tabs.SelectNext(e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1);
            e.Handled = true;
            return;
        }

        if (ctrl && e.KeyModifiers.HasFlag(KeyModifiers.Shift) && e.Key == Key.K)
        {
            _viewModel.ToggleInkModeCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (ctrl && e.KeyModifiers.HasFlag(KeyModifiers.Shift) && e.Key == Key.D)
        {
            DumpRenderState("manual");
            e.Handled = true;
            return;
        }
        if (ctrl && e.KeyModifiers.HasFlag(KeyModifiers.Shift) && e.Key == Key.H)
        {
            if (_viewModel.HighlightSelectionCommand.CanExecute(null))
            {
                _viewModel.HighlightSelectionCommand.Execute(null);
            }

            e.Handled = true;
            return;
        }

        if (!(e.Source is TextBox) && ctrl && e.Key == Key.Z)
        {
            if (_viewModel.UndoAnnotationCommand.CanExecute(null))
            {
                _viewModel.UndoAnnotationCommand.Execute(null);
            }

            e.Handled = true;
            return;
        }
        if (ctrl && e.KeyModifiers.HasFlag(KeyModifiers.Shift) && e.Key == Key.S)
        {
            if (_viewModel.SaveCopyCommand.CanExecute(null))
            {
                _viewModel.SaveCopyCommand.Execute(null);
            }

            e.Handled = true;
            return;
        }

        if (ctrl && e.Key == Key.S)
        {
            if (_viewModel.SaveDocumentCommand.CanExecute(null))
            {
                _viewModel.SaveDocumentCommand.Execute(null);
            }

            e.Handled = true;
            return;
        }

        if (!(e.Source is TextBox) && ctrl && e.Key == Key.Y)
        {
            if (_viewModel.RedoAnnotationCommand.CanExecute(null))
            {
                _viewModel.RedoAnnotationCommand.Execute(null);
            }

            e.Handled = true;
            return;
        }
        if (ctrl && e.Key == Key.D)
        {
            _viewModel.AddBookmarkCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (ctrl && e.Key == Key.W)
        {
            // Closing the last tab closes its document and leaves the window open, which is
            // what Ctrl+W did before tabs existed.
            _tabs.CloseTab(_viewModel);
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
            if (_viewModel.IsInkMode)
            {
                _viewModel.ToggleInkModeCommand.Execute(null);
            }
            else if (_viewModel.IsSearchOpen)
            {
                _viewModel.CloseSearchCommand.Execute(null);
            }
            else if (_viewModel.HasSelection)
            {
                _viewModel.ClearTextSelection();
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
            if (_viewModel.IsDocumentOpen)
            {
                _tabs.AddTab(path);
            }
            else
            {
                _ = _viewModel.OpenPathAsync(path);
            }
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

    /// <summary>Used by the parameterless constructor (designer, XAML tooling).</summary>
    private sealed class NullWindowService : IWindowService
    {
        public static readonly NullWindowService Instance = new();

        private NullWindowService()
        {
        }

        public void OpenNewWindow(string? filePath = null)
        {
        }

        public void OpenInNewWindow(string filePath)
        {
        }
    }
    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        foreach (var tab in _tabs.Tabs)
        {
            tab.SaveReadingPositionNow();
        }

        var state = WindowState;
        var maximized = state == WindowState.Maximized;
        var fullScreen = state == WindowState.FullScreen;

        // Only the restore geometry is remembered; while maximized or full screen the
        // current bounds belong to the screen, not to the user's preferred window.
        int? x = null;
        int? y = null;
        if (!maximized && !fullScreen)
        {
            x = Position.X;
            y = Position.Y;
        }

        _viewModel.SaveWindowState(Width, Height, maximized, x, y);
    }
}
