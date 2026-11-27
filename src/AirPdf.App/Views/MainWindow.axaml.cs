using System.Runtime.InteropServices;
using AirPdf.App.Controls;
using AirPdf.App.Layout;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using AirPdf.App.Services;
using AirPdf.App.ViewModels;
using AirPdf.Core.Models;
using AirPdf.Core.Services;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace AirPdf.App.Views;

public partial class MainWindow : Window
{
    private readonly AppServices _services;
    private readonly IWindowService _windows;
    private readonly IUserDialogService _dialogs;
    private readonly WindowViewModel _tabs;
    private readonly SettingsService _settingsService;
    private readonly string? _initialFile;
    private readonly bool _restoreSession;
    private readonly IDataTemplate _singlePageTemplate;
    private readonly IDataTemplate _spreadPageTemplate;
    private MainWindowViewModel _viewModel = null!;
    private ScrollViewer? _tabScrollViewer;
    private DispatcherTimer? _tabAutoScrollTimer;
    private int _tabAutoScrollDirection;
    private double _tabDragPointerX;
    private bool _suppressScrollSync;
    private int _scrollIntent;
    private int _markupScrollRequest;
    private MarkupNavigationPoint? _pendingMarkupNavigation;
    private DateTime _pendingMarkupNavigationExpiresUtc;
    private bool _suppressThumbnailSync;
    private bool _thumbnailSyncPending;
    private int _pageLayoutRestoreVersion;
    private bool _handlingScrollChanged;
    private bool _tabStripWidthUpdatePending;
    private WindowState _windowStateBeforeFullScreen = WindowState.Normal;
    private DispatcherTimer? _fullscreenHintTimer;
    private MainWindowViewModel? _dragTab;
    private Border? _dragTabBorder;
    private IPointer? _dragPointer;
    private Point _dragTabStart;
    private bool _tabDragStarted;
    private readonly List<double> _tabDragRightEdges = [];
    private int _tabDragDocumentOffset;
    private bool _isSelectingText;
    private bool _selectionPressInProgress;
    private (int Page, double X, double Y)? _contextPoint;
    private static readonly TimeSpan TabReorderDuration = TimeSpan.FromMilliseconds(160);
    private const double TabAutoScrollEdge = 28;
    private (PageViewModel Page, Control View, List<(double X, double Y)> Points)? _inkStroke;
    private (PageViewModel Page, int Number)? _erasing;
    private bool _selectionMoved;
    private bool _restoringDocument;
    private bool _linkCursorActive;
    private bool _closeConfirmed;
    private bool _closePromptInProgress;
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
        bool restoreWindowPosition,
        IUserDialogService? dialogs = null)
    {
        InitializeComponent();

        _singlePageTemplate = (IDataTemplate)Resources["SinglePageItemTemplate"]!;
        _spreadPageTemplate = (IDataTemplate)Resources["SpreadPageItemTemplate"]!;

        _services = services;
        _windows = windows;
        _dialogs = dialogs ?? new DialogService(() => this, services.Localization);
        _settingsService = services.SettingsService;
        var settings = services.Settings;

        _initialFile = initialFile;
        _restoreSession = restoreWindowPosition;

        if (OperatingSystem.IsWindows())
        {
            // Windows-only managed chrome spike: drop the non-client caption while keeping
            // the resizable border, then provide the caption controls in the workspace bar.
            SystemDecorations = SystemDecorations.BorderOnly;
            ExtendClientAreaToDecorationsHint = true;
            ExtendClientAreaChromeHints = ExtendClientAreaChromeHints.NoChrome;
            TabStrip.Padding = new Thickness(12, 0, 0, 0);
            WindowCaptionButtons.IsVisible = true;
            TitleDragRegion.IsVisible = true;
            Win32Properties.SetNonClientHitTestResult(
                MaximizeWindowButton,
                Win32Properties.Win32HitTestValue.MaxButton);
            // BorderOnly drops WS_SYSMENU; add it back so the native system menu exists.
            Win32Properties.AddWindowStylesCallback(this, AddSystemMenuStyle);
            Win32Properties.AddWndProcHookCallback(this, OnWindowsMessage);
            UpdateWindowStateVisuals();
            SetCaptionActivity(IsActive);
        }

        // The tab list binds to the host, not to the active document's view model. The
        // surrounding workspace bar stays on the document because it also carries Open/Home.
        _tabs = new WindowViewModel(CreateTabViewModel);
        _tabs.PropertyChanged += OnTabsPropertyChanged;
        TabList.DataContext = _tabs;

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

        PagesList.SizeChanged += OnPagesListSizeChanged;
        SizeChanged += OnWindowSizeChanged;
        BrandPanel.SizeChanged += (_, _) => ScheduleTabStripWidthUpdate();
        Activated += (_, _) => SetCaptionActivity(true);
        Deactivated += (_, _) => SetCaptionActivity(false);

        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
        AddHandler(PointerPressedEvent, OnSelectionPointerPressed, RoutingStrategies.Tunnel);
        AddHandler(PointerMovedEvent, OnSelectionPointerMoved, RoutingStrategies.Tunnel);
        AddHandler(PointerEnteredEvent, OnReadingChromePointerEntered, RoutingStrategies.Tunnel);
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
            _dialogs,
            _windows,
            host,
            isHome);
    }

    private void OnTabsPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(WindowViewModel.SelectedTab):
                // A null selection must not leave the window pointed at the tab it just closed.
                ShowTab(_tabs.SelectedTab ?? _tabs.HomeTab);
                break;
            case nameof(WindowViewModel.HasTabs):
                SyncTabSelection();
                break;
        }
    }

    private void SyncTabSelection()
    {
        // The workspace bar is always visible because it also carries the brand and Open
        // action. Only the list selection needs to follow the host when tabs arrive/leave.
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
            _viewModel.AbandonTextMode();
            UnhookViewModel(_viewModel);

            // The editor belongs to the tab that opened it, so it never survives a switch.
            CloseTextEditor(discard: true);
        }

        // Freeze scroll sync across the swap. Rebuilding the list for another document emits
        // ScrollChanged events, and by then _viewModel is the incoming tab, so an unsuppressed
        // event would write the outgoing tab's offset into it.
        _suppressScrollSync = true;

        // Read the incoming tab's position now, before any of that noise. A tab opened in
        // the background has not had its first view restore yet, so its pending reading
        // position is still waiting.
        var pendingReading = tab.TakePendingReadingPosition();
        var position = pendingReading is not null
            ? new NavigationPoint(pendingReading.Page, pendingReading.PageOffset)
            : tab.CurrentPosition;

        _viewModel = tab;
        DataContext = tab;
        HookViewModel(tab);
        ApplyPageTemplate();
        SyncTabSelection();

        if (WindowState == WindowState.FullScreen)
        {
            tab.EnterReadingMode();
            tab.HideReadingChrome();
            Dispatcher.UIThread.Post(
                () =>
                {
                    if (ReferenceEquals(_viewModel, tab) &&
                        WindowState == WindowState.FullScreen)
                    {
                        UpdateViewport();
                        tab.FitWidthForReadingMode();
                    }
                },
                DispatcherPriority.Loaded);
        }
        else
        {
            tab.LeaveReadingMode();
        }

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
                if (tab.NeedsInitialZoom)
                {
                    tab.ApplyInitialZoom();
                }

                tab.FitWidthForReadingMode();
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
        viewModel.ScrollToMarkupRequested += OnScrollToMarkupRequested;
        viewModel.ReturnToPositionRequested += OnReturnToPositionRequested;
        viewModel.CopySelectionRequested += OnCopySelectionRequested;
        viewModel.DocumentOpened += OnDocumentOpened;
        viewModel.ZoomChanged += OnZoomChanged;
        viewModel.SearchOpened += OnSearchOpened;
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void UnhookViewModel(MainWindowViewModel viewModel)
    {
        viewModel.ScrollToPageRequested -= OnScrollToPageRequested;
        viewModel.ScrollToSearchResultRequested -= OnScrollToSearchResultRequested;
        viewModel.ScrollToMarkupRequested -= OnScrollToMarkupRequested;
        viewModel.ReturnToPositionRequested -= OnReturnToPositionRequested;
        viewModel.CopySelectionRequested -= OnCopySelectionRequested;
        viewModel.DocumentOpened -= OnDocumentOpened;
        viewModel.ZoomChanged -= OnZoomChanged;
        viewModel.SearchOpened -= OnSearchOpened;
        viewModel.PropertyChanged -= OnViewModelPropertyChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.IsTwoPageMode))
        {
            ApplyPageLayoutMode();
        }
        else if (e.PropertyName == nameof(MainWindowViewModel.HasSelection))
        {
            Dispatcher.UIThread.Post(PositionSelectionToolbar, DispatcherPriority.Loaded);

            // The page view may still be measuring when the selection notification arrives.
            // Two one-shot retries cover that without observing LayoutUpdated, which can loop
            // forever on a very long document when the toolbar's margin changes the layout.
            DispatcherTimer.RunOnce(PositionSelectionToolbar, TimeSpan.FromMilliseconds(80));
            DispatcherTimer.RunOnce(PositionSelectionToolbar, TimeSpan.FromMilliseconds(240));
        }
        else if (e.PropertyName == nameof(MainWindowViewModel.IsTextOverlayVisible) &&
                 !_viewModel.IsTextOverlayVisible)
        {
            // The view model can leave text mode on its own (a closing document, a tab switch),
            // and the editor must not outlive the overlay it lives in.
            HideTextEditorChrome();
            HideTextGhost();
        }
    }

    private void OnMarkupColorClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { Tag: MarkupColor color })
        {
            _viewModel.SetHighlightColor(color);
            MarkupColorButton.Flyout?.Hide();
        }
    }

    private void PositionSelectionToolbar()
    {
        if (!_viewModel.HasSelection || SelectionToolbar.Parent is not Control parent)
        {
            return;
        }

        var pageView = PagesList.GetVisualDescendants()
            .OfType<PdfPageView>()
            .FirstOrDefault(view => view.DataContext is PageViewModel { SelectionRects.Count: > 0 });
        if (pageView?.DataContext is not PageViewModel page ||
            page.SelectionRects.Count == 0)
        {
            return;
        }

        var rect = page.SelectionRects[0];
        var anchor = pageView.TranslatePoint(
            new Point(
                rect.X * pageView.Bounds.Width,
                rect.Y * pageView.Bounds.Height),
            parent);
        if (anchor is null)
        {
            return;
        }

        // The property-change notification can arrive before the binding has made the
        // toolbar visible, when its measured size is still zero. Fixed fallback dimensions
        // keep the position correct for the first layout pass; the binding then shows it.
        var width = SelectionToolbar.Bounds.Width > 0
            ? SelectionToolbar.Bounds.Width
            : 190;
        var height = SelectionToolbar.Bounds.Height > 0
            ? SelectionToolbar.Bounds.Height
            : 38;
        if (parent.Bounds.Width <= width)
        {
            return;
        }

        var selectionWidth = rect.Width * pageView.Bounds.Width;
        var x = Math.Clamp(
            anchor.Value.X + (selectionWidth / 2) - (width / 2),
            8,
            Math.Max(8, parent.Bounds.Width - width - 8));
        var y = anchor.Value.Y - height - 10;
        if (y < 8)
        {
            y = anchor.Value.Y + (rect.Height * pageView.Bounds.Height) + 10;
        }

        var margin = new Thickness(x, y, 0, 0);
        if (SelectionToolbar.Margin != margin)
        {
            SelectionToolbar.Margin = margin;
        }
    }

    private void ApplyPageTemplate()
    {
        PagesList.ItemTemplate = _viewModel.IsTwoPageMode
            ? _spreadPageTemplate
            : _singlePageTemplate;
    }

    private void ApplyPageLayoutMode()
    {
        ApplyPageTemplate();

        if (!_viewModel.IsDocumentOpen)
        {
            return;
        }

        var position = _viewModel.CurrentPosition;
        var viewModel = _viewModel;
        var version = ++_pageLayoutRestoreVersion;
        var twoPage = _viewModel.IsTwoPageMode;
        UpdateViewport();
        RebuildPageLayout();

        bool CurrentPageIsRealized()
        {
            foreach (var item in PagesList.GetRealizedContainers().OfType<ListBoxItem>())
            {
                // During the swap the old single-page containers are still alive for a
                // moment; they must not make the new two-page list look settled.
                if (twoPage)
                {
                    if (item.DataContext is PageSpreadViewModel spread &&
                        (spread.LeftPage.Number == position.Page ||
                         spread.RightPage?.Number == position.Page))
                    {
                        return true;
                    }
                }
                else if (item.DataContext is PageViewModel page &&
                         page.Number == position.Page)
                {
                    return true;
                }
            }

            return false;
        }

        void RestorePosition()
        {
            if (version != _pageLayoutRestoreVersion ||
                !ReferenceEquals(_viewModel, viewModel) ||
                CurrentPageIsRealized())
            {
                return;
            }

            // The virtualizing panel is rebuilt after the ItemsSource/template change and can
            // briefly keep its old offset. Keep nudging it until the spread containing the
            // reader's page has actually been realized.
            RebuildPageLayout();
            PagesList.ScrollToVerticalOffset(0);
            ScrollToReadingPosition(position.Page, position.PageOffset);
        }

        Dispatcher.UIThread.Post(RestorePosition, DispatcherPriority.Loaded);
        var restoreAttempts = 0;
        var restoreTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(120)
        };
        restoreTimer.Tick += (_, _) =>
        {
            if (version != _pageLayoutRestoreVersion ||
                !ReferenceEquals(_viewModel, viewModel) ||
                CurrentPageIsRealized() ||
                ++restoreAttempts >= 30)
            {
                restoreTimer.Stop();
                return;
            }

            RestorePosition();
        };
        restoreTimer.Start();
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

        var layoutPage = _pageLayout.GetPageAtViewportTop(viewer.Offset.Y, viewer.Viewport.Height);
        var page = PageNumberForLayoutItem(layoutPage);
        _viewModel.UpdateCurrentPageFromScroll(
            page,
            _pageLayout.GetPageOffset(layoutPage, viewer.Offset.Y));
    }

    private void OnNewTabClick(object? sender, RoutedEventArgs e) => _tabs.AddTab();

    private void OnTabPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border { DataContext: MainWindowViewModel tab } border ||
            tab.IsHomeTab ||
            !e.GetCurrentPoint(border).Properties.IsLeftButtonPressed)
        {
            return;
        }

        // The close button owns its presses; starting a drag there makes it hard to click.
        if (e.Source is Visual source && source.FindAncestorOfType<Button>() is not null)
        {
            return;
        }

        EndTabDrag();
        _dragTab = tab;
        _dragTabBorder = border;
        _dragPointer = e.Pointer;
        _dragTabStart = e.GetPosition(TabList);
    }

    private void OnTabPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragTab is null || _dragPointer is null)
        {
            return;
        }

        if (!e.GetCurrentPoint(TabList).Properties.IsLeftButtonPressed)
        {
            EndTabDrag();
            return;
        }

        var position = e.GetPosition(TabList);
        if (!_tabDragStarted)
        {
            var moved = Math.Abs(position.X - _dragTabStart.X) +
                        Math.Abs(position.Y - _dragTabStart.Y);
            if (moved < 6)
            {
                return;
            }

            _tabDragStarted = true;
            SnapshotTabSlots();
            _dragTabBorder?.Classes.Add("tabDragging");
            _dragPointer.Capture(TabList);
        }

        _tabDragPointerX = position.X;
        UpdateTabAutoScroll(position.X);
        ReorderTab(position.X);
        e.Handled = true;
    }

    private void OnTabPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_dragTab is null)
        {
            return;
        }

        if (_tabDragStarted)
        {
            e.Handled = true;
        }

        EndTabDrag();
    }

    private void OnTabPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e) =>
        EndTabDrag(releaseCapture: false);

    private void EndTabDrag(bool releaseCapture = true)
    {
        var pointer = _dragPointer;
        var border = _dragTabBorder;

        _dragTab = null;
        _dragTabBorder = null;
        _dragPointer = null;
        _tabDragStarted = false;
        _tabDragRightEdges.Clear();
        _tabDragDocumentOffset = 0;
        _tabAutoScrollDirection = 0;
        _tabAutoScrollTimer?.Stop();
        border?.Classes.Remove("tabDragging");

        if (releaseCapture)
        {
            pointer?.Capture(null);
        }
    }

    /// <summary>
    /// Records where every document tab sits before the drag reorders anything, in content
    /// coordinates so the slots stay valid while the strip auto-scrolls.
    /// </summary>
    private void SnapshotTabSlots()
    {
        _tabDragRightEdges.Clear();
        _tabDragDocumentOffset = CountLeadingHomeTabs();

        var scroll = GetTabScrollViewer()?.Offset.X ?? 0;
        var ordered = TabList.GetRealizedContainers()
            .OfType<ListBoxItem>()
            .Where(container => container.DataContext is MainWindowViewModel { IsHomeTab: false })
            .Select(container => new
            {
                Container = container,
                Index = _tabs.Tabs.IndexOf((MainWindowViewModel)container.DataContext!)
            })
            .Where(item => item.Index >= 0)
            .OrderBy(item => item.Index)
            .ToList();

        if (ordered.Count == 0)
        {
            return;
        }

        var left = ordered[0].Container.TranslatePoint(new Point(0, 0), TabList)?.X ?? 0;
        var widths = ordered
            .Select(item => item.Container.Bounds.Width)
            .ToList();

        _tabDragRightEdges.AddRange(TabDragSlots.BuildEdges(left + scroll, widths));
    }

    /// <summary>Home occupies index zero and is never a drop target.</summary>
    private int CountLeadingHomeTabs() =>
        _tabs.Tabs.Count > 0 && _tabs.Tabs[0].IsHomeTab ? 1 : 0;

    /// <summary>
    /// Moves the dragged tab into the slot the pointer is over. Slots come from the snapshot
    /// taken at drag start, so crossing one boundary reorders once and crossing back undoes it.
    /// </summary>
    private void ReorderTab(double pointerX)
    {
        if (_dragTab is null || _tabDragRightEdges.Count < 2)
        {
            return;
        }

        var contentX = pointerX + (GetTabScrollViewer()?.Offset.X ?? 0);
        var slot = TabDragSlots.Resolve(_tabDragRightEdges, contentX);
        var targetIndex = _tabDragDocumentOffset + slot;

        var sourceIndex = _tabs.Tabs.IndexOf(_dragTab);
        if (sourceIndex < 0 || sourceIndex == targetIndex)
        {
            return;
        }

        MoveTabWithAnimation(_dragTab, targetIndex);
    }

    /// <summary>
    /// Reorders the collection first, then offsets every realized tab by the distance it
    /// jumped. A short transition slides each tab from its old position into the new layout
    /// instead of making the strip snap.
    /// </summary>
    private void MoveTabWithAnimation(MainWindowViewModel tab, int targetIndex)
    {
        var sourceIndex = _tabs.Tabs.IndexOf(tab);
        if (sourceIndex < 0 || sourceIndex == targetIndex)
        {
            return;
        }

        TabList.UpdateLayout();
        var before = new Dictionary<MainWindowViewModel, double>();
        foreach (var container in TabList.GetRealizedContainers().OfType<ListBoxItem>())
        {
            if (container.DataContext is MainWindowViewModel candidate &&
                container.TranslatePoint(new Point(0, 0), TabList) is { } origin)
            {
                before[candidate] = origin.X;
            }
        }

        _tabs.MoveTab(tab, targetIndex);
        TabList.UpdateLayout();

        foreach (var container in TabList.GetRealizedContainers().OfType<ListBoxItem>())
        {
            if (container.DataContext is not MainWindowViewModel candidate ||
                !before.TryGetValue(candidate, out var oldX) ||
                container.TranslatePoint(new Point(0, 0), TabList) is not { } newOrigin)
            {
                continue;
            }

            var delta = oldX - newOrigin.X;
            if (Math.Abs(delta) < 0.5)
            {
                continue;
            }

            var transform = new TranslateTransform
            {
                X = delta,
                Transitions = new Transitions
                {
                    new DoubleTransition
                    {
                        Property = TranslateTransform.XProperty,
                        Duration = TabReorderDuration,
                        Easing = new CubicEaseOut()
                    }
                }
            };
            container.RenderTransform = transform;
            transform.X = 0;
        }
    }

    private void UpdateTabAutoScroll(double pointerX)
    {
        if (_dragTab is null || !_tabDragStarted)
        {
            StopTabAutoScroll();
            return;
        }

        var width = TabList.Bounds.Width;
        var direction = pointerX <= TabAutoScrollEdge
            ? -1
            : pointerX >= width - TabAutoScrollEdge
                ? 1
                : 0;

        if (direction == 0)
        {
            StopTabAutoScroll();
            return;
        }

        _tabAutoScrollDirection = direction;
        _tabAutoScrollTimer ??= CreateTabAutoScrollTimer();
        if (!_tabAutoScrollTimer.IsEnabled)
        {
            _tabAutoScrollTimer.Start();
        }
    }

    private DispatcherTimer CreateTabAutoScrollTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        timer.Tick += OnTabAutoScrollTick;
        return timer;
    }

    private void OnTabAutoScrollTick(object? sender, EventArgs e)
    {
        if (_dragTab is null || _tabAutoScrollDirection == 0)
        {
            StopTabAutoScroll();
            return;
        }

        var viewer = GetTabScrollViewer();
        if (viewer is null)
        {
            return;
        }

        var maxOffset = Math.Max(0, viewer.Extent.Width - viewer.Viewport.Width);
        if (maxOffset <= 0)
        {
            StopTabAutoScroll();
            return;
        }

        var step = Math.Max(12, viewer.Viewport.Width * 0.05) * _tabAutoScrollDirection;
        var next = Math.Clamp(viewer.Offset.X + step, 0, maxOffset);
        if (Math.Abs(next - viewer.Offset.X) < 0.5)
        {
            return;
        }

        viewer.Offset = new Vector(next, viewer.Offset.Y);
        TabList.UpdateLayout();
        ReorderTab(_tabDragPointerX);
    }

    private ScrollViewer? GetTabScrollViewer() =>
        _tabScrollViewer ??= TabList.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();

    private void StopTabAutoScroll()
    {
        _tabAutoScrollDirection = 0;
        _tabAutoScrollTimer?.Stop();
    }

    private async void OnCloseTabClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: MainWindowViewModel tab })
        {
            e.Handled = true;
            await CloseTabAsync(tab);
        }
    }

    private void OnTabContextOpenFolderClick(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: MainWindowViewModel tab })
        {
            tab.OpenContainingFolderCommand.Execute(null);
        }
    }

    private async void OnTabContextCloseClick(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: MainWindowViewModel tab })
        {
            await CloseTabAsync(tab);
        }
    }

    private async void OnTabContextCloseOthersClick(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: MainWindowViewModel target })
        {
            await CloseOtherTabsAsync(target);
        }
    }

    /// <summary>
    /// Closing a tab with unsaved annotations asks first. Save is attempted before the tab is
    /// disposed; if it fails, the tab stays open with its edits and the error on screen.
    /// </summary>
    private async Task<bool> CloseTabAsync(MainWindowViewModel tab)
    {
        if (tab.IsHomeTab || !await tab.ConfirmCloseAsync())
        {
            return false;
        }

        // Release PDFium off the UI thread before the tab's Dispose runs; Dispose still closes
        // synchronously and used to freeze the window behind another document's render.
        await tab.ReleaseDocumentAsync();
        _tabs.CloseTab(tab);
        return true;
    }

    private async Task CloseOtherTabsAsync(MainWindowViewModel target)
    {
        if (target.IsHomeTab || !_tabs.Tabs.Contains(target))
        {
            return;
        }

        var others = _tabs.Tabs
            .Where(tab => !ReferenceEquals(tab, target) && !tab.IsHomeTab)
            .ToList();

        foreach (var tab in others)
        {
            // A cancelled save leaves the remaining tabs alone instead of silently closing
            // documents the reader has not confirmed yet.
            if (!await CloseTabAsync(tab))
            {
                break;
            }
        }

        if (_tabs.Tabs.Contains(target))
        {
            _tabs.SelectedTab = target;
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
        _fullscreenHintTimer?.Stop();

        // The host owns every tab, so disposing it disposes each document handle.
        _tabs.PropertyChanged -= OnTabsPropertyChanged;
        _tabs.Dispose();
    }

    /// <summary>
    /// Keeps the toolbar usable at the minimum window width: the outer rhythm tightens
    /// and fit-width/fit-page fold into the existing zoom menu. At normal sizes the
    /// design spacing and every control are left alone.
    /// </summary>
    private void OnWindowSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        var compact = e.NewSize.Width < 1000;
        if (compact)
        {
            if (!Toolbar.Classes.Contains("compact"))
            {
                Toolbar.Classes.Add("compact");
            }
        }
        else
        {
            Toolbar.Classes.Remove("compact");
        }

        // Fit width/page remain in the zoom menu, so the two shortcuts fold away rather
        // than pushing the right-hand controls past the window edge at the minimum width.
        FitWidthButton.IsVisible = !compact;
        FitPageButton.IsVisible = !compact;

        ScheduleTabStripWidthUpdate();
    }

    /// <summary>
    /// Caps the tab list at the space left between the brand and the new-tab button. In an
    /// Auto column that makes + follow the last tab while the tabs fit, and pins it to the
    /// visible right edge once the list starts scrolling.
    /// </summary>
    private void ScheduleTabStripWidthUpdate()
    {
        if (_tabStripWidthUpdatePending)
        {
            return;
        }

        _tabStripWidthUpdatePending = true;
        Dispatcher.UIThread.Post(
            () =>
            {
                _tabStripWidthUpdatePending = false;
                UpdateTabStripWidth();
            },
            DispatcherPriority.Loaded);
    }

    private void UpdateTabStripWidth()
    {
        var captionWidth = WindowCaptionButtons.IsVisible ? WindowCaptionButtons.Bounds.Width : 0;
        var dragWidth = TitleDragRegion.IsVisible ? TitleDragRegion.MinWidth : 0;
        var width = TabStrip.Bounds.Width
            - TabStrip.Padding.Left
            - TabStrip.Padding.Right
            - BrandPanel.Bounds.Width
            - TabList.Margin.Left
            - NewTabButton.Bounds.Width
            - NewTabButton.Margin.Left
            - captionWidth
            - dragWidth;
        var maxWidth = Math.Max(0, width);

        // Setting the same value again would schedule another layout pass for nothing.
        if (Math.Abs(TabList.MaxWidth - maxWidth) > 0.5)
        {
            TabList.MaxWidth = maxWidth;
        }
    }

    /// <summary>
    /// WA: the maximize button is a non-client HTMAXBUTTON so Windows 11 can raise Snap
    /// Layouts. Non-client messages no longer reach Avalonia's pointer pipeline, so mirror
    /// the hover background for that one button here.
    /// </summary>
    private IntPtr OnWindowsMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const uint WmMouseMove = 0x0200;
        const uint WmNcHitTest = 0x0084;
        const uint WmNcMouseMove = 0x00A0;
        const uint WmNcMouseLeave = 0x02A3;
        const uint WmSysCommand = 0x0112;
        const int HtClient = 1;
        const int HtMaxButton = 9;
        const int ScKeyMenu = 0xF100;
        const int VkSpace = 0x20;

        if (message == WmNcHitTest &&
            WindowState == WindowState.FullScreen &&
            IsFullscreenTopEdge(hwnd, lParam))
        {
            // Fullscreen keeps the resizable top border. Treat that strip as client
            // space so the custom titlebar can start a drag from the very top edge.
            handled = true;
            return new IntPtr(HtClient);
        }
        else if (message == WmNcMouseMove)
        {
            SetMaximizeHover(wParam.ToInt32() == HtMaxButton);
        }
        else if (message == WmNcMouseLeave || message == WmMouseMove)
        {
            SetMaximizeHover(false);
        }
        else if (message == WmSysCommand
                 && (wParam.ToInt32() & 0xFFF0) == ScKeyMenu
                 && lParam.ToInt32() == VkSpace)
        {
            ShowSystemMenu(hwnd, Avalonia.VisualExtensions.PointToScreen(this, new Point(8, 44)));
            handled = true;
        }

        return IntPtr.Zero;
    }

    private static bool IsFullscreenTopEdge(IntPtr hwnd, IntPtr lParam)
    {
        // The resize frame is DPI-scaled; cover a little more than the measured 8px so
        // high-DPI fullscreen windows do not keep a dead strip at the very top.
        const int topEdgeHeight = 16;

        var packed = lParam.ToInt64();
        var point = new NativeMenu.POINT
        {
            X = unchecked((short)(packed & 0xFFFF)),
            Y = unchecked((short)((packed >> 16) & 0xFFFF))
        };

        return NativeMenu.ScreenToClient(hwnd, ref point) &&
               NativeMenu.GetClientRect(hwnd, out var client) &&
               point.X >= 0 &&
               point.X < client.Right &&
               point.Y >= 0 &&
               point.Y < topEdgeHeight;
    }

    private void SetMaximizeHover(bool hovered)
    {
        if (hovered)
        {
            if (!MaximizeWindowButton.Classes.Contains("nonClientHover"))
            {
                MaximizeWindowButton.Classes.Add("nonClientHover");
            }
        }
        else
        {
            MaximizeWindowButton.Classes.Remove("nonClientHover");
        }
    }

    private static (uint Style, uint ExStyle) AddSystemMenuStyle(uint style, uint exStyle) =>
        (style | 0x00080000u, exStyle);

    private void SetCaptionActivity(bool active)
    {
        if (WindowCaptionButtons is not null)
        {
            WindowCaptionButtons.Opacity = active ? 1 : 0.55;
        }

        if (!active)
        {
            SetMaximizeHover(false);
        }
    }

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var properties = e.GetCurrentPoint(this).Properties;

        if (properties.IsRightButtonPressed)
        {
            if (TryGetPlatformHandle()?.Handle is { } hwnd)
            {
                ShowSystemMenu(hwnd, Avalonia.VisualExtensions.PointToScreen(this, e.GetPosition(this)));
            }

            e.Handled = true;
            return;
        }

        if (!properties.IsLeftButtonPressed)
        {
            return;
        }

        if (WindowState == WindowState.FullScreen)
        {
            // BeginMoveDrag needs a normal window to move; leaving fullscreen first also
            // matches the expected "drag the top edge out of fullscreen" gesture.
            WindowState = WindowState.Normal;
        }

        if (e.ClickCount == 2)
        {
            ToggleMaximize();
        }
        else
        {
            BeginMoveDrag(e);
        }

        e.Handled = true;
    }

    private void ShowSystemMenu(IntPtr hwnd, PixelPoint point)
    {
        const uint WmSysCommand = 0x0112;
        const uint TpmReturnCmd = 0x0100;
        const uint TpmRightButton = 0x0002;

        var menu = NativeMenu.GetSystemMenu(hwnd, false);
        if (menu == IntPtr.Zero)
        {
            return;
        }

        NativeMenu.SetForegroundWindow(hwnd);
        var command = NativeMenu.TrackPopupMenu(
            menu,
            TpmReturnCmd | TpmRightButton,
            point.X,
            point.Y,
            0,
            hwnd,
            IntPtr.Zero);

        if (command != 0)
        {
            NativeMenu.PostMessage(hwnd, WmSysCommand, (IntPtr)command, IntPtr.Zero);
        }
    }

    private static class NativeMenu
    {
        [DllImport("user32.dll")]
        internal static extern IntPtr GetSystemMenu(IntPtr hWnd, bool bRevert);

        [DllImport("user32.dll")]
        internal static extern int TrackPopupMenu(
            IntPtr hMenu,
            uint uFlags,
            int x,
            int y,
            int nReserved,
            IntPtr hWnd,
            IntPtr prcRect);

        [DllImport("user32.dll")]
        internal static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        internal static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        internal static extern bool ScreenToClient(IntPtr hWnd, ref POINT point);

        [DllImport("user32.dll")]
        internal static extern bool GetClientRect(IntPtr hWnd, out RECT rectangle);

        [StructLayout(LayoutKind.Sequential)]
        internal struct POINT
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }
    }

    private void OnMinimizeWindowClick(object? sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void OnToggleMaximizeWindowClick(object? sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.FullScreen)
        {
            ExitFullScreen();
        }
        else
        {
            ToggleMaximize();
        }
    }

    private void OnExitFullScreenClick(object? sender, RoutedEventArgs e) => ExitFullScreen();

    private void OnCloseWindowClick(object? sender, RoutedEventArgs e) => Close();

    private void ToggleFullScreen()
    {
        if (WindowState == WindowState.FullScreen)
        {
            ExitFullScreen();
            return;
        }

        _windowStateBeforeFullScreen = WindowState == WindowState.Maximized
            ? WindowState.Maximized
            : WindowState.Normal;
        WindowState = WindowState.FullScreen;
    }

    private void ExitFullScreen() =>
        WindowState = _windowStateBeforeFullScreen == WindowState.Maximized && CanMaximize
            ? WindowState.Maximized
            : WindowState.Normal;

    private void ToggleMaximize()
    {
        if (WindowState == WindowState.Maximized)
        {
            WindowState = WindowState.Normal;
        }
        else if (WindowState == WindowState.Normal && CanMaximize)
        {
            WindowState = WindowState.Maximized;
        }
    }

    private void UpdateWindowStateVisuals()
    {
        var fullScreen = WindowState == WindowState.FullScreen;

        if (_viewModel is not null)
        {
            if (fullScreen)
            {
                _viewModel.EnterReadingMode();

                // The page list gets wider only after the chrome has collapsed. Refit after
                // that layout pass so reading mode actually uses the extra width.
                Dispatcher.UIThread.Post(
                    () =>
                    {
                        if (WindowState != WindowState.FullScreen)
                        {
                            return;
                        }

                        UpdateViewport();
                        _viewModel.FitWidthForReadingMode();
                    },
                    DispatcherPriority.Loaded);
            }
            else
            {
                _viewModel.LeaveReadingMode();
            }
        }

        if (this.FindControl<Button>("MaximizeWindowButton") is { } maximizeButton)
        {
            maximizeButton.IsVisible = !fullScreen;
        }

        if (this.FindControl<Button>("ExitFullScreenButton") is { } exitFullScreenButton)
        {
            exitFullScreenButton.IsVisible = fullScreen;
        }

        if (this.FindControl<Avalonia.Controls.Shapes.Path>("MaximizeGlyph") is { } maximize)
        {
            maximize.IsVisible = !fullScreen && WindowState != WindowState.Maximized;
        }

        if (this.FindControl<Avalonia.Controls.Shapes.Path>("RestoreGlyph") is { } restore)
        {
            restore.IsVisible = !fullScreen && WindowState == WindowState.Maximized;
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == WindowStateProperty)
        {
            UpdateWindowStateVisuals();

            if (WindowState == WindowState.FullScreen)
            {
                ShowFullscreenHint();
            }
            else
            {
                HideFullscreenHint();
            }
        }
    }

    private void ShowFullscreenHint()
    {
        if (FullscreenHint is null)
        {
            return;
        }

        if (_fullscreenHintTimer is null)
        {
            _fullscreenHintTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(3.5)
            };
            _fullscreenHintTimer.Tick += (_, _) => HideFullscreenHint();
        }

        FullscreenHint.IsVisible = true;
        _fullscreenHintTimer.Stop();
        _fullscreenHintTimer.Start();
    }

    private void HideFullscreenHint()
    {
        _fullscreenHintTimer?.Stop();

        if (FullscreenHint is not null)
        {
            FullscreenHint.IsVisible = false;
        }
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        // Screens and the native window handle are only guaranteed once the window is
        // shown, so the restored position is retried here if the constructor could not
        // validate it yet.
        TryApplyPendingPosition();
        UpdateViewport();
        ScheduleTabStripWidthUpdate();
        if (!string.IsNullOrWhiteSpace(_initialFile))
        {
            _ = _viewModel.OpenPathAsync(_initialFile);
            return;
        }

        if (_restoreSession)
        {
            RestoreSession();
        }
    }

    private void RestoreSession()
    {
        var paths = _services.Settings.OpenTabs
            .Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
            .Distinct(OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal)
            .ToList();

        if (paths.Count > 0)
        {
            _tabs.RestoreSession(paths, _services.Settings.SelectedTabPath);
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

        // A markup jump can race the final fit-width calculation on a very large document.
        // If the zoom changes under it, the old offset no longer points at the page that was
        // laid out; redo the markup navigation against the new layout instead of restoring
        // the pre-jump reading position.
        if (TakePendingMarkupNavigation() is { } pendingMarkup)
        {
            // ScrollToMarkup drops the jump by itself if the tab changes before it runs.
            var markupTab = _viewModel;
            Dispatcher.UIThread.Post(
                () => ScrollToMarkup(pendingMarkup, markupTab),
                DispatcherPriority.Loaded);
            return;
        }

        var position = _viewModel.CurrentPosition;
        var viewModel = _viewModel;
        Dispatcher.UIThread.Post(
            () =>
            {
                if (ReferenceEquals(_viewModel, viewModel))
                {
                    // Keep the reader's place within the page; ScrollToPage would re-centre
                    // the whole page on every zoom step.
                    ScrollToReadingPosition(position.Page, position.PageOffset);
                }
            },
            DispatcherPriority.Loaded);
    }

    private MarkupNavigationPoint? TakePendingMarkupNavigation()
    {
        var pending = _pendingMarkupNavigation;
        _pendingMarkupNavigation = null;
        return pending is not null &&
               DateTime.UtcNow <= _pendingMarkupNavigationExpiresUtc
            ? pending
            : null;
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

    private void OnScrollToMarkupRequested(object? sender, MarkupNavigationPoint point)
    {
        var viewModel = _viewModel;
        Dispatcher.UIThread.Post(
            () =>
            {
                if (ReferenceEquals(_viewModel, viewModel))
                {
                    ScrollToMarkup(point, viewModel);
                }
            },
            DispatcherPriority.Loaded);
    }

    private void ScrollToMarkup(MarkupNavigationPoint point, MainWindowViewModel tab, int attempt = 0)
    {
        // The retry chains below outlive the click that started them. Every one of them re-checks
        // the tab it was started for, so a jump queued against one document cannot scroll a
        // different one to the same page number after a tab switch.
        if (!ReferenceEquals(_viewModel, tab) ||
            point.PageNumber < 1 ||
            point.PageNumber > tab.Pages.Count)
        {
            return;
        }

        if (attempt == 0)
        {
            _pendingMarkupNavigation = point;
            _pendingMarkupNavigationExpiresUtc = DateTime.UtcNow.AddSeconds(30);
        }

        var viewer = PagesList.Viewer;
        if (viewer is null || viewer.Viewport.Height <= 0)
        {
            if (attempt < 8)
            {
                Dispatcher.UIThread.Post(
                    () => ScrollToMarkup(point, tab, attempt + 1),
                    DispatcherPriority.Background);
            }

            return;
        }

        RebuildPageLayout();
        var layoutPage = LayoutItemForPage(point.PageNumber);
        var pageHeight = _pageLayout.GetHeight(layoutPage);
        var center = Math.Clamp(point.Bounds.Y + (point.Bounds.Height / 2), 0, 1);
        var target = _pageLayout.GetTop(layoutPage) +
                     (center * pageHeight) -
                     (viewer.Viewport.Height * 0.40);
        var maxOffset = Math.Max(0, viewer.Extent.Height - viewer.Viewport.Height);
        target = Math.Clamp(target, 0, maxOffset);

        var request = ++_markupScrollRequest;
        ApplyScrollOffset(target, point.PageNumber, center);
        DispatcherTimer.RunOnce(
            () => AlignMarkupInViewport(point, tab, request, attempt: 0),
            TimeSpan.FromMilliseconds(60));
    }

    private void AlignMarkupInViewport(
        MarkupNavigationPoint point,
        MainWindowViewModel tab,
        int request,
        int attempt)
    {
        if (request != _markupScrollRequest || !ReferenceEquals(_viewModel, tab))
        {
            return;
        }

        var viewer = PagesList.Viewer;
        if (viewer is null || viewer.Viewport.Height <= 0)
        {
            if (attempt < 8)
            {
                DispatcherTimer.RunOnce(
                    () => AlignMarkupInViewport(point, tab, request, attempt + 1),
                    TimeSpan.FromMilliseconds(60));
            }

            return;
        }

        var pageView = FindPageView(point.PageNumber);
        if (pageView is null)
        {
            if (attempt < 12)
            {
                // The target page was just outside the realised window (a common edge on a
                // document with thousands of pages). Nudge one screenful in the direction of
                // the target, then re-check, instead of ScrollIntoView, which blocks the UI on
                // a list this large.
                var realized = PagesList.GetVisualDescendants()
                    .OfType<PdfPageView>()
                    .Select(view => (view.DataContext as PageViewModel)?.Number ?? 0)
                    .Where(number => number > 0)
                    .ToList();
                var direction = realized.Count == 0 || point.PageNumber > realized.Max()
                    ? 1
                    : -1;
                var nudge = Math.Max(160, viewer.Viewport.Height * 0.6) * direction;
                var next = Math.Clamp(
                    viewer.Offset.Y + nudge,
                    0,
                    Math.Max(0, viewer.Extent.Height - viewer.Viewport.Height));
                PagesList.ScrollToVerticalOffset(next);

                DispatcherTimer.RunOnce(
                    () => AlignMarkupInViewport(point, tab, request, attempt + 1),
                    TimeSpan.FromMilliseconds(70));
            }

            return;
        }

        var center = Math.Clamp(point.Bounds.Y + (point.Bounds.Height / 2), 0, 1);
        var pageTop = pageView.TranslatePoint(new Point(0, 0), this);
        var viewerTop = viewer.TranslatePoint(new Point(0, 0), this);
        if (pageTop is null || viewerTop is null)
        {
            return;
        }

        var annotationY = pageTop.Value.Y + (center * pageView.Bounds.Height);
        var desiredY = viewerTop.Value.Y + (viewer.Viewport.Height * 0.40);
        var maxOffset = Math.Max(0, viewer.Extent.Height - viewer.Viewport.Height);
        var target = Math.Clamp(
            viewer.Offset.Y + (annotationY - desiredY),
            0,
            maxOffset);

        ApplyScrollOffset(target, point.PageNumber, center);
        if (attempt >= 4)
        {
            _pendingMarkupNavigation = null;
        }

        // Keep aligning for a short window. A large document can finish its fit-width pass
        // well after ScrollIntoView; recomputing from the live page view catches that without
        // trusting cached page heights. The window is bounded so a later user scroll is not
        // pulled back to the annotation.
        if (attempt < 4)
        {
            var delay = attempt switch
            {
                0 => 320,
                1 => 440,
                2 => 600,
                _ => 900
            };
            DispatcherTimer.RunOnce(
                () => AlignMarkupInViewport(point, tab, request, attempt + 1),
                TimeSpan.FromMilliseconds(delay));
        }
    }

    private PdfPageView? FindPageView(int pageNumber) =>
        PagesList.GetVisualDescendants()
            .OfType<PdfPageView>()
            .FirstOrDefault(view => view.DataContext is PageViewModel page &&
                                    page.Number == pageNumber);

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

    private void OnPagesListSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        UpdateViewport();
        _viewModel?.FitWidthForReadingMode();
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

        // Scrolling/zooming switches pages to a cheaper render size and interpolation mode.
        // Restore is excluded so opening a document renders once at its final size.
        if (!_restoringDocument)
        {
            _viewModel.BeginPageInteraction();
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

        var layoutPage = _pageLayout.GetPageAtViewportTop(viewer.Offset.Y, viewer.Viewport.Height);
        var page = PageNumberForLayoutItem(layoutPage);
        var fraction = _pageLayout.GetPageOffset(layoutPage, viewer.Offset.Y);
        _viewModel.UpdateCurrentPageFromScroll(page, fraction);
        UpdateThumbnailSelection();
        if (_viewModel.HasSelection)
        {
            Dispatcher.UIThread.Post(PositionSelectionToolbar, DispatcherPriority.Loaded);
        }
    }

    private int LayoutItemCount => _viewModel.IsTwoPageMode
        ? _viewModel.Spreads.Count
        : _viewModel.Pages.Count;

    private int LayoutItemForPage(int pageNumber) => _viewModel.IsTwoPageMode
        ? ((pageNumber - 1) / 2) + 1
        : pageNumber;

    private int PageNumberForLayoutItem(int layoutPage) => _viewModel.IsTwoPageMode
        ? ((layoutPage - 1) * 2) + 1
        : layoutPage;

    private void RebuildPageLayout()
    {
        var heights = _viewModel.IsTwoPageMode
            ? _viewModel.Spreads
                .Select(spread => Math.Max(
                    _viewModel.PageDisplayHeight(spread.LeftPage),
                    spread.RightPage is { } right
                        ? _viewModel.PageDisplayHeight(right)
                        : 0))
                .ToList()
            : _viewModel.Pages.Select(_viewModel.PageDisplayHeight).ToList();

        _pageLayout = new PageLayout(heights);
    }

    private void EnsurePageLayout()
    {
        if (_pageLayout.Count != LayoutItemCount)
        {
            RebuildPageLayout();
        }
    }

    private void ScrollToPage(int pageNumber, bool center = true) =>
        ScrollToPage(pageNumber, center, attempt: 0, _viewModel);

    private void ScrollToPage(int pageNumber, bool center, int attempt, MainWindowViewModel tab)
    {
        // The retry below can outlive the click that asked for it. A page number means nothing
        // in another document, so the chain stops as soon as the tab changes.
        if (!ReferenceEquals(_viewModel, tab) || tab.Pages.Count == 0)
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
                    () => ScrollToPage(pageNumber, center, attempt + 1, tab),
                    DispatcherPriority.Loaded);
            }

            return;
        }

        var targetPage = Math.Clamp(pageNumber, 1, tab.Pages.Count);
        var layoutPage = LayoutItemForPage(targetPage);
        var top = _pageLayout.GetTop(layoutPage);
        var pageHeight = _pageLayout.GetHeight(layoutPage);
        var target = center
            ? top - Math.Max(12, (viewer.Viewport.Height - pageHeight) / 2)
            : top - 12;
        var fraction = Math.Clamp((target - top) / Math.Max(1, pageHeight), 0, 1);

        ApplyScrollOffset(target, targetPage, fraction);
    }

    private void ScrollToReadingPosition(int pageNumber, double fraction) =>
        ScrollToReadingPosition(pageNumber, fraction, attempt: 0, _viewModel);

    private void ScrollToReadingPosition(int pageNumber, double fraction, int attempt, MainWindowViewModel tab)
    {
        if (!ReferenceEquals(_viewModel, tab) || tab.Pages.Count == 0)
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
                    () => ScrollToReadingPosition(pageNumber, fraction, attempt + 1, tab),
                    DispatcherPriority.Background);
            }

            return;
        }

        var pageNumberClamped = Math.Clamp(pageNumber, 1, tab.Pages.Count);
        var layoutPage = LayoutItemForPage(pageNumberClamped);
        var top = _pageLayout.GetTop(layoutPage);
        var pageHeight = _pageLayout.GetHeight(layoutPage);
        var normalized = Math.Clamp(fraction, 0, 1);
        var target = top + (normalized * pageHeight);

        // Scrolling past the current extent is silently clamped, and then the page never gets
        // realised: that is the blank page seen after jumping from the welcome screen, where
        // the list has not been measured yet.
        if (viewer.Extent.Height < target + 1 && attempt < 4)
        {
            Dispatcher.UIThread.Post(
                () => ScrollToReadingPosition(pageNumber, fraction, attempt + 1, tab),
                DispatcherPriority.Background);
            return;
        }

        ApplyScrollOffset(target, pageNumberClamped, normalized);
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

        var layoutPage = LayoutItemForPage(result.PageNumber);
        var top = _pageLayout.GetTop(layoutPage);
        var pageHeight = _pageLayout.GetHeight(layoutPage);

        var highlightTop = result.Rects.Count > 0
            ? top + (result.Rects[0].Y * pageHeight)
            : top;
        var target = highlightTop - (viewer.Viewport.Height * 0.34);
        var fraction = Math.Clamp((highlightTop - top) / Math.Max(1, pageHeight), 0, 1);
        ApplyScrollOffset(target, result.PageNumber, fraction);
    }

    private void ApplyScrollOffset(double target, int pageNumber, double fraction)
    {
        _suppressScrollSync = true;

        // The write below only reports where this scroll landed. By the time the timer runs the
        // reader may already be somewhere else - a page-number jump, a bookmark, a search result,
        // or a closing tab - and reporting the older page would pull the current page and the
        // saved reading position back to it. That is how a position set right after opening a
        // document ended up saved as page one.
        var viewModel = _viewModel;
        var pageBeforeScroll = viewModel.CurrentPage;
        var intent = ++_scrollIntent;

        PagesList.ScrollToVerticalOffset(target);
        DispatcherTimer.RunOnce(() =>
        {
            // A newer scroll (or a different document) owns the viewport now. Do not clear the
            // suppression flag or report this older position; the newer ApplyScrollOffset will
            // do both when its own timer expires.
            if (intent != _scrollIntent)
            {
                return;
            }

            _suppressScrollSync = false;
            if (ReferenceEquals(_viewModel, viewModel) && viewModel.CurrentPage == pageBeforeScroll)
            {
                viewModel.UpdateCurrentPageFromScroll(pageNumber, fraction);
            }

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

    private void OnAnnotationSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (AnnotationList.SelectedItem is AnnotationItemViewModel annotation)
        {
            _viewModel.GoToAnnotationCommand.Execute(annotation);
            AnnotationList.SelectedItem = null;
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

        // Text mode owns the press: left places text or commits the open editor, right leaves
        // the mode and opens the menu as usual.
        if (_viewModel.IsTextMode)
        {
            if (HandleTextModePress(e))
            {
                return;
            }
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

        // A double click on an existing text annotation reopens the editor over it. Anything
        // else keeps the normal word selection, so this never consumes the press.
        if (e.ClickCount >= 2)
        {
            var doubleClickView = FindPageView(e.Source) ?? FindPageViewAt(e.GetPosition(this));
            if (doubleClickView?.DataContext is PageViewModel doubleClickPage)
            {
                var local = e.GetPosition(doubleClickView);
                if (new Rect(doubleClickView.Bounds.Size).Contains(local))
                {
                    _ = TryBeginReeditAsync(doubleClickView, local);
                }
            }
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

        // The press has to originate inside a page view, not merely be over a page that is
        // scrolled partly behind the toolbar. The position fallback used to treat a toolbar
        // click as a text-selection press and swallow the button.
        var pageView = FindPageView(e.Source);
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
        catch (Exception unexpected)
        {
            // The press could not be turned into a selection. Abandon the press, but say so in
            // the log: a silent stop here looks exactly like a page with no text layer.
            CrashLog.Write("BeginSelection", unexpected);
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
        catch (Exception unexpected)
        {
            // Without the button state the drag cannot be started or trusted, so the press is
            // abandoned; the log is what keeps it from looking like a dead page.
            CrashLog.Write("SelectionPointerState", unexpected);
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

    private void OnReadingChromePointerEntered(object? sender, PointerEventArgs e) =>
        UpdateReadingChromeFromPointer(e);

    /// <summary>
    /// Reading mode keeps the chrome out of the way, but the top edge has to behave like a
    /// hidden titlebar: enter it to reveal the tabs/toolbar and leave it to go back to the
    /// content-only view.
    /// </summary>
    private void UpdateReadingChromeFromPointer(PointerEventArgs e)
    {
        if (_viewModel is null || !_viewModel.IsReadingMode)
        {
            return;
        }

        var y = e.GetPosition(this).Y;
        if (!_viewModel.IsReadingChromeRevealed)
        {
            if (y <= 8)
            {
                _viewModel.RevealReadingChrome();
            }

            return;
        }

        var chromeHeight = TabStrip.Bounds.Height + Toolbar.Bounds.Height;
        if (y > chromeHeight + 12)
        {
            _viewModel.HideReadingChrome();
        }
    }

    private void OnSelectionPointerMoved(object? sender, PointerEventArgs e)
    {
        UpdateReadingChromeFromPointer(e);
        UpdateToolCursor(e);

        if (_viewModel.IsTextMode)
        {
            UpdateTextGhost(e);
        }

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

    private Cursor? _penCursor;
    private Cursor? _eraserCursor;

    /// <summary>
    /// Builds a cursor from one of the tool icons. A bitmap cursor is set once when the pointer
    /// enters a page and left alone after that; drawing a glyph on every pointer move is what made
    /// the tools feel slow.
    /// </summary>
    private Cursor CreateToolCursor(string iconKey, PixelPoint hotspot)
    {
        const int size = 28;
        var bitmap = new RenderTargetBitmap(new PixelSize(size, size), new Vector(96, 96));
        using (var context = bitmap.CreateDrawingContext())
        {
            if (this.FindResource(iconKey) is Geometry geometry)
            {
                using (context.PushTransform(Matrix.CreateScale(size / 24.0, size / 24.0)))
                {
                    context.DrawGeometry(
                        Brushes.White,
                        new Pen(Brushes.Black, 2.2, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round),
                        geometry);
                }
            }
        }

        return new Cursor(bitmap, hotspot);
    }

    /// <summary>
    /// Puts the tool in the pointer while it is over a page, and leaves the ordinary arrow
    /// everywhere else, so where the tool works is visible without guessing.
    /// </summary>
    private void UpdateToolCursor(PointerEventArgs e)
    {
        var drawing = _viewModel.IsInkMode || _viewModel.IsEraserMode;
        var overPage = drawing && FindPageView(e.Source) is { DataContext: PageViewModel };

        Cursor wanted;
        if (overPage)
        {
            try
            {
                _penCursor ??= CreateToolCursor("IconPen", new PixelPoint(5, 24));
                _eraserCursor ??= CreateToolCursor("IconEraser", new PixelPoint(14, 14));
                wanted = _viewModel.IsEraserMode ? _eraserCursor : _penCursor;
            }
            catch (Exception unexpected)
            {
                // A cursor that cannot be built must not take the window down mid-hover.
                CrashLog.Write("ToolCursor", unexpected);
                wanted = new Cursor(StandardCursorType.Cross);
            }
        }
        else
        {
            wanted = Cursor.Default;
        }

        if (!ReferenceEquals(Cursor, wanted))
        {
            Cursor = wanted;
        }
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
        // A page scrolled above the viewport still has a translated rectangle that can
        // reach under the toolbar. Hit-testing by position is only valid while the point is
        // inside the page list itself.
        if (this.TranslatePoint(windowPoint, PagesList) is not { } listPoint ||
            !new Rect(PagesList.Bounds.Size).Contains(listPoint))
        {
            return null;
        }

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

        _ = _viewModel.EnsureDocumentDetailsAsync();
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

    private void OnInkPaletteOpened(object? sender, EventArgs e)
    {
        if (InkPaletteButton.Flyout is not Flyout { Content: Control content })
        {
            return;
        }

        // ItemsControl containers are generated during the flyout's layout pass.
        Dispatcher.UIThread.Post(
            () => UpdateInkSwatchSelection(content),
            DispatcherPriority.Loaded);
    }

    private void UpdateInkSwatchSelection(Control content)
    {
        foreach (var button in content.GetVisualDescendants().OfType<Button>())
        {
            if (button.Tag is MarkupColor color)
            {
                button.Classes.Set("selected", Equals(color, _viewModel.InkColor));
            }
        }
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
        if (InkPaletteButton.Flyout is { } ink)
        {
            ink.Hide();
        }
    }

    /// <summary>
    /// The pen and its palette arrow are one visual control, so the whole group lights up while
    /// either half is under the pointer. Tracking it here keeps that state on the group rather
    /// than on whichever button the pointer happens to be over.
    /// </summary>
    private void OnInkToolPointerEntered(object? sender, PointerEventArgs e) =>
        InkToolGroup.Classes.Set("hover", true);

    private void OnInkToolPointerExited(object? sender, PointerEventArgs e) =>
        InkToolGroup.Classes.Set("hover", false);
    private async void OnAddNoteClick(object? sender, RoutedEventArgs e)
    {
        if (_contextPoint is not { } point)
        {
            return;
        }

        await _viewModel.AddNoteAtAsync(point.Page, point.X, point.Y);
        _contextPoint = null;
    }

    // -------------------------------------------------------------------------------------
    // Text mode
    //
    // The mode is sticky: a commit leaves it on, so the next click starts another piece of
    // text, and only Esc leaves it. The ghost is drawn in the overlay rather than as a custom
    // cursor so it can preview the box as well as the insertion point.
    // -------------------------------------------------------------------------------------

    private static readonly double[] TextSizePresets = [8, 9, 10, 11, 12, 14, 16, 18, 20, 24, 28, 32, 36, 48];
    private static readonly Cursor HiddenCursor = new(StandardCursorType.None);

    private PdfPageView? _textEditorPage;
    private bool _textColourPicked;

    /// <summary>Menu item: toggles the sticky text mode.</summary>
    private async void OnAddTextClick(object? sender, RoutedEventArgs e)
    {
        _contextPoint = null;

        if (_viewModel.IsTextMode)
        {
            CloseTextEditor(discard: true);
            _viewModel.ExitTextMode();
            return;
        }

        if (await _viewModel.BeginTextModeAsync())
        {
            // A new session starts with the contrast default, not the colour left over from the
            // last one.
            _textColourPicked = false;
            CloseTextEditor(discard: true);
            UpdateTextCursor(overPage: false);
        }
    }

    /// <summary>
    /// Free text is drawn in the reader's colour, and black on a dark page is unreadable. Until
    /// the reader picks a colour in this session, the default comes from the page pixels under
    /// the click: white over dark areas, black over light ones.
    /// </summary>
    private void ApplyContrastingTextColour(PdfPageView pageView, double pageX, double pageY)
    {
        if (_textColourPicked ||
            pageView.DataContext is not PageViewModel page ||
            page.Image is not { } image)
        {
            return;
        }

        var width = image.PixelSize.Width;
        var height = image.PixelSize.Height;
        if (width <= 0 || height <= 0 || pageView.Bounds.Width <= 1 || pageView.Bounds.Height <= 1)
        {
            return;
        }

        var centreX = (int)Math.Clamp(pageX / pageView.Bounds.Width * width, 0, width - 1);
        var centreY = (int)Math.Clamp(pageY / pageView.Bounds.Height * height, 0, height - 1);
        const int radius = 14;
        var left = Math.Max(0, centreX - radius);
        var top = Math.Max(0, centreY - radius);
        var right = Math.Min(width - 1, centreX + radius);
        var bottom = Math.Min(height - 1, centreY + radius);

        long total = 0;
        var samples = 0;

        try
        {
            using var frame = image.Lock();
            var bgra = frame.Format == PixelFormat.Bgra8888;
            if (!bgra && frame.Format != PixelFormat.Rgba8888)
            {
                return;
            }

            for (var y = top; y <= bottom; y += 2)
            {
                var row = frame.Address + (y * frame.RowBytes);
                for (var x = left; x <= right; x += 2)
                {
                    var offset = x * 4;
                    var first = Marshal.ReadByte(row, offset);
                    var green = Marshal.ReadByte(row, offset + 1);
                    var third = Marshal.ReadByte(row, offset + 2);
                    var (red, blue) = bgra ? (third, first) : (first, third);
                    total += ((red * 299) + (green * 587) + (blue * 114)) / 1000;
                    samples++;
                }
            }
        }
        catch (Exception unexpected)
        {
            // A bitmap that cannot be sampled must not take the click down; the control's
            // current colour stands.
            CrashLog.Write("TextContrast", unexpected);
            return;
        }

        if (samples == 0)
        {
            return;
        }

        var wanted = total / samples < 110 ? "White" : "Black";
        if (_viewModel.Palette.FirstOrDefault(colour => colour.Name == wanted) is { } match)
        {
            _viewModel.SetTextColor(match);
        }
    }

    private void UpdateTextGhost(PointerEventArgs e)
    {
        if (!_viewModel.IsTextMode || TextEditor.IsVisible)
        {
            HideTextGhost();
            return;
        }

        var pageView = FindPageView(e.Source) ?? FindPageViewAt(e.GetPosition(this));
        if (pageView?.DataContext is not PageViewModel ||
            pageView.TranslatePoint(new Point(0, 0), TextLayer) is not { } origin)
        {
            HideTextGhost();
            return;
        }

        var point = e.GetPosition(pageView);
        if (!new Rect(pageView.Bounds.Size).Contains(point))
        {
            HideTextGhost();
            return;
        }

        var line = Math.Max(8, _viewModel.TextFontSizePx * 1.25);
        var minWidth = 60 * (96.0 / 72.0) * _viewModel.Zoom;
        var boxWidth = Math.Max(4, Math.Min(minWidth, pageView.Bounds.Width - point.X));
        var left = origin.X + point.X;
        var top = origin.Y + point.Y;

        Canvas.SetLeft(TextGhostBox, left);
        Canvas.SetTop(TextGhostBox, top);
        TextGhostBox.Width = boxWidth;
        TextGhostBox.Height = line + 4;

        Canvas.SetLeft(TextGhostCaret, left);
        Canvas.SetTop(TextGhostCaret, top);
        TextGhostCaret.Height = line;

        Canvas.SetLeft(TextGhostBadge, left - 2);
        Canvas.SetTop(TextGhostBadge, Math.Max(0, top - 19));

        SetTextGhostVisible(true);
    }

    private void HideTextGhost() => SetTextGhostVisible(false);

    private void SetTextGhostVisible(bool visible)
    {
        TextGhostBox.IsVisible = visible;
        TextGhostCaret.IsVisible = visible;
        TextGhostBadge.IsVisible = visible;
        UpdateTextCursor(visible);
    }

    /// <summary>In text mode the ghost is the cursor, so the arrow gets out of the way.</summary>
    private void UpdateTextCursor(bool overPage)
    {
        var wanted = overPage && _viewModel.IsTextMode && !TextEditor.IsVisible
            ? HiddenCursor
            : null;
        if (!ReferenceEquals(PagesList.Cursor, wanted))
        {
            PagesList.Cursor = wanted;
        }
    }

    private async Task BeginTextAtAsync(PdfPageView pageView, Point inPage)
    {
        if (pageView.DataContext is not PageViewModel page)
        {
            return;
        }

        // Free text is written in unrotated page space, so on a quarter-turned page it would
        // render a quarter turn away from the page text. Refuse instead of writing that.
        if (await _viewModel.GetPageRotationForInputAsync(page.Number) != 0)
        {
            _viewModel.ReportStatus("L.TextRotatedUnsupported");
            HideTextGhost();
            return;
        }

        var x = Math.Clamp(inPage.X, 0, Math.Max(0, pageView.Bounds.Width - 8));
        var y = Math.Clamp(inPage.Y, 0, Math.Max(0, pageView.Bounds.Height - 8));
        ApplyContrastingTextColour(pageView, x, y);
        OpenTextEditor(pageView, x, y, string.Empty, isReedit: false);
    }

    /// <summary>
    /// Double-click on an existing text annotation reopens the editor over it. Only the text
    /// annotation is claimed; a double-click anywhere else keeps the normal word selection.
    /// </summary>
    private async Task TryBeginReeditAsync(PdfPageView pageView, Point inPage)
    {
        if (pageView.DataContext is not PageViewModel page ||
            pageView.Bounds.Width <= 0 ||
            pageView.Bounds.Height <= 0)
        {
            return;
        }

        var x = inPage.X / pageView.Bounds.Width;
        var y = inPage.Y / pageView.Bounds.Height;

        if (await _viewModel.FindTextAnnotationAsync(page.Number, x, y) is not { } hit)
        {
            return;
        }

        if (!await _viewModel.BeginReeditAsync(hit.Text))
        {
            return;
        }

        // The double-click also selected a word under the annotation; the editor owns the spot now.
        _viewModel.ClearTextSelection();

        OpenTextEditor(
            pageView,
            hit.Display.X * pageView.Bounds.Width,
            hit.Display.Y * pageView.Bounds.Height,
            hit.Text.Text,
            isReedit: true);
    }

    private void OpenTextEditor(
        PdfPageView pageView,
        double pageX,
        double pageY,
        string text,
        bool isReedit)
    {
        if (pageView.TranslatePoint(new Point(0, 0), TextLayer) is not { } origin)
        {
            return;
        }

        _textEditorPage = pageView;
        HideTextGhost();

        if (!isReedit)
        {
            _viewModel.BeginTextEditing();
        }

        var minWidth = 60 * (96.0 / 72.0) * _viewModel.Zoom;
        TextEditor.Text = text;
        TextEditor.MinWidth = minWidth;
        TextEditor.MaxWidth = Math.Max(minWidth, pageView.Bounds.Width - pageX - 6);
        TextEditor.IsVisible = true;
        Canvas.SetLeft(TextEditor, origin.X + pageX);
        Canvas.SetTop(TextEditor, origin.Y + pageY);

        TextFormatBar.IsVisible = true;
        LayoutTextEditor();

        TextEditor.Focus();
        TextEditor.CaretIndex = TextEditor.Text?.Length ?? 0;
        UpdateTextCursor(overPage: false);
    }

    private void HideTextEditorChrome()
    {
        TextEditor.IsVisible = false;
        TextFormatBar.IsVisible = false;
        _textEditorPage = null;
        _viewModel.IsTextEditing = false;
    }

    private void CloseTextEditor(bool discard)
    {
        HideTextEditorChrome();
        if (discard)
        {
            _viewModel.CancelTextEditing();
        }

        UpdateTextCursor(overPage: false);
    }

    private void OnTextEditorSizeChanged(object? sender, SizeChangedEventArgs e) => LayoutTextEditor();

    /// <summary>
    /// Keeps the editor inside the page and the format bar near it. Driven by SizeChanged rather
    /// than a layout-updated handler: moving the editor cannot change its size, so this settles
    /// in one pass instead of looping.
    /// </summary>
    private void LayoutTextEditor()
    {
        if (!TextEditor.IsVisible || _textEditorPage is not { } pageView ||
            pageView.TranslatePoint(new Point(0, 0), TextLayer) is not { } origin)
        {
            return;
        }

        var left = Canvas.GetLeft(TextEditor);
        var top = Canvas.GetTop(TextEditor);
        var width = TextEditor.Bounds.Width;
        var height = TextEditor.Bounds.Height;

        var minLeft = origin.X + 2;
        var minTop = origin.Y + 2;
        var maxRight = origin.X + pageView.Bounds.Width - 2;
        var maxBottom = origin.Y + pageView.Bounds.Height - 2;

        if (left + width > maxRight)
        {
            left = Math.Max(minLeft, maxRight - width);
        }

        if (top + height > maxBottom)
        {
            top = Math.Max(minTop, maxBottom - height);
        }

        if (Math.Abs(Canvas.GetLeft(TextEditor) - left) > 0.5)
        {
            Canvas.SetLeft(TextEditor, left);
        }

        if (Math.Abs(Canvas.GetTop(TextEditor) - top) > 0.5)
        {
            Canvas.SetTop(TextEditor, top);
        }

        PositionTextFormatBar(origin, pageView, left, top, height);
    }

    private void PositionTextFormatBar(
        Point origin,
        PdfPageView pageView,
        double editorLeft,
        double editorTop,
        double editorHeight)
    {
        var barWidth = TextFormatBar.Bounds.Width > 0 ? TextFormatBar.Bounds.Width : 224;
        var barHeight = TextFormatBar.Bounds.Height > 0 ? TextFormatBar.Bounds.Height : 38;

        var minLeft = origin.X + 2;
        var maxLeft = Math.Max(minLeft, origin.X + pageView.Bounds.Width - barWidth - 2);
        var left = Math.Clamp(editorLeft, minLeft, maxLeft);

        var top = editorTop - barHeight - 8;
        if (top < origin.Y + 2)
        {
            top = editorTop + editorHeight + 8;
        }

        Canvas.SetLeft(TextFormatBar, left);
        Canvas.SetTop(TextFormatBar, top);
    }

    private async Task CommitTextFromEditorAsync()
    {
        if (!TextEditor.IsVisible || _textEditorPage is not { } pageView ||
            pageView.DataContext is not PageViewModel page ||
            pageView.TranslatePoint(new Point(0, 0), TextLayer) is not { } origin ||
            pageView.Bounds.Width <= 0 ||
            pageView.Bounds.Height <= 0)
        {
            return;
        }

        var display = new PdfSearchRect(
            Math.Clamp((Canvas.GetLeft(TextEditor) - origin.X) / pageView.Bounds.Width, 0, 1),
            Math.Clamp((Canvas.GetTop(TextEditor) - origin.Y) / pageView.Bounds.Height, 0, 1),
            Math.Clamp(TextEditor.Bounds.Width / pageView.Bounds.Width, 0.001, 1),
            Math.Clamp(TextEditor.Bounds.Height / pageView.Bounds.Height, 0.001, 1));

        var text = TextEditor.Text;
        HideTextEditorChrome();

        await _viewModel.CommitTextAsync(page.Number, text, display);
        UpdateTextCursor(overPage: false);
    }

    private void OnTextCommitClick(object? sender, RoutedEventArgs e) => _ = CommitTextFromEditorAsync();

    private async void OnTextDeleteClick(object? sender, RoutedEventArgs e)
    {
        if (!TextEditor.IsVisible)
        {
            return;
        }

        var wasReedit = _viewModel.IsReediting;
        HideTextEditorChrome();

        if (wasReedit)
        {
            await _viewModel.DeleteReeditTargetAsync();
        }
        else
        {
            _viewModel.CancelTextEditing();
            _viewModel.ReportStatus("L.TextDiscarded");
        }

        UpdateTextCursor(overPage: false);
    }

    private void OnTextSizeDownClick(object? sender, RoutedEventArgs e) => NudgeTextSize(-1);

    private void OnTextSizeUpClick(object? sender, RoutedEventArgs e) => NudgeTextSize(1);

    private void OnTextSizeCycleClick(object? sender, RoutedEventArgs e)
    {
        var current = _viewModel.TextFontSize;
        var next = TextSizePresets.FirstOrDefault(size => size > current + 0.01, TextSizePresets[0]);
        _viewModel.TextFontSize = next;
        LayoutTextEditor();
    }

    private void NudgeTextSize(double delta)
    {
        _viewModel.NudgeTextSize(delta);
        LayoutTextEditor();
    }

    private void OnTextPaletteOpened(object? sender, EventArgs e)
    {
        if (TextColorButton.Flyout is not Flyout { Content: Control content })
        {
            return;
        }

        // ItemsControl containers are generated during the flyout's layout pass.
        Dispatcher.UIThread.Post(
            () =>
            {
                foreach (var button in content.GetVisualDescendants().OfType<Button>())
                {
                    if (button.Tag is MarkupColor color)
                    {
                        button.Classes.Set("selected", Equals(color, _viewModel.TextColor));
                    }
                }
            },
            DispatcherPriority.Loaded);
    }

    private void OnTextSwatchClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { Tag: MarkupColor color })
        {
            _viewModel.SetTextColor(color);
            _textColourPicked = true;
            TextColorButton.Flyout?.Hide();
        }
    }

    /// <summary>
    /// Routes a press while text mode is on. Returns true when the press was consumed; a right
    /// click falls through so the page menu still opens at that point. Presses on the chrome are
    /// never consumed, so the sidebar and toolbar keep working while the mode is on.
    /// </summary>
    private bool HandleTextModePress(PointerPressedEventArgs e)
    {
        var properties = e.GetCurrentPoint(this).Properties;

        if (properties.IsRightButtonPressed)
        {
            CloseTextEditor(discard: true);
            _viewModel.ExitTextMode();
            return false;
        }

        if (!properties.IsLeftButtonPressed)
        {
            return true;
        }

        var source = e.Source as Visual;

        if (TextEditor.IsVisible)
        {
            // The editor and its bar own their presses; marking those handled would stop the
            // TextBox from placing its caret.
            if (source is not null && (IsWithin(source, TextEditor) || IsWithin(source, TextFormatBar)))
            {
                return true;
            }

            if (source is not null && IsWithin(source, PagesList))
            {
                e.Handled = true;
                _ = CommitTextFromEditorAsync();
                return true;
            }

            return false;
        }

        var pageView = FindPageView(e.Source) ?? FindPageViewAt(e.GetPosition(this));
        if (pageView?.DataContext is not PageViewModel)
        {
            return false;
        }

        var local = e.GetPosition(pageView);
        if (new Rect(pageView.Bounds.Size).Contains(local))
        {
            e.Handled = true;
            _ = BeginTextAtAsync(pageView, local);
        }

        return true;
    }

    /// <summary>True when <paramref name="visual"/> is <paramref name="ancestor"/> or inside it.</summary>
    private static bool IsWithin(Visual? visual, Visual ancestor)
    {
        for (var current = visual; current is not null; current = current.GetVisualParent())
        {
            if (ReferenceEquals(current, ancestor))
            {
                return true;
            }
        }

        return false;
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

        if (ctrl && e.Key == Key.N)
        {
            _windows.OpenNewWindow();
            e.Handled = true;
            return;
        }

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
            // what Ctrl+W did before tabs existed. Unsaved annotations are confirmed first.
            _ = CloseTabAsync(_viewModel);
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
            // Text mode owns Esc: it places what is typed, and leaves the mode when nothing is
            // being typed. That is the only way out of the sticky mode.
            if (TextEditor.IsVisible)
            {
                _ = CommitTextFromEditorAsync();
                e.Handled = true;
                return;
            }

            if (_viewModel.IsTextMode)
            {
                _viewModel.ExitTextMode();
                HideTextGhost();
                e.Handled = true;
                return;
            }

            if (_viewModel.IsInkMode)
            {
                _viewModel.ToggleInkModeCommand.Execute(null);
            }
            else if (_viewModel.IsEraserMode)
            {
                _viewModel.ToggleEraserModeCommand.Execute(null);
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
                ExitFullScreen();
            }

            e.Handled = true;
            return;
        }

        if (e.Key == Key.F11)
        {
            ToggleFullScreen();
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
                _tabs.OpenDocument(path);
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
    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        // A close that arrives while the unsaved-changes prompt is open belongs to that prompt.
        if (_closePromptInProgress)
        {
            e.Cancel = true;
            return;
        }

        if (!_closeConfirmed)
        {
            var unsaved = _tabs.Tabs.Where(tab => tab.HasUnsavedAnnotations).ToList();
            var hasDocuments = _tabs.Tabs.Any(tab => tab.IsDocumentOpen);
            if (unsaved.Count > 0 || hasDocuments)
            {
                e.Cancel = true;
                _closePromptInProgress = true;
                var canClose = false;

                try
                {
                    foreach (var tab in unsaved)
                    {
                        _tabs.SelectedTab = tab;
                        if (!await tab.ConfirmCloseAsync())
                        {
                            return;
                        }
                    }

                    // Release every document handle off the UI thread before the window's
                    // Dispose path runs its synchronous fallback close.
                    foreach (var tab in _tabs.Tabs)
                    {
                        await tab.ReleaseDocumentAsync();
                    }

                    canClose = true;
                }
                catch (Exception unexpected)
                {
                    // Keep the window open. This is the path that discards the annotations the
                    // prompt existed for, so a failure here must not continue the close. The
                    // catch also matters because an exception escaping an async void handler
                    // reaches the dispatcher and ends the process.
                    CrashLog.Write("CloseWindow", unexpected);
                    _viewModel.ReportError("L.ErrorCloseAborted");
                    return;
                }
                finally
                {
                    _closePromptInProgress = false;
                }

                if (canClose)
                {
                    _closeConfirmed = true;
                    Close();
                }

                return;
            }
        }

        PersistWindowState();
    }

    private void PersistWindowState()
    {
        foreach (var tab in _tabs.Tabs)
        {
            tab.SaveReadingPositionNow();
        }

        _tabs.SaveSession();

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