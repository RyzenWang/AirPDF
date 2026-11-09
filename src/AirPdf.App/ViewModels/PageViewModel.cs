using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using AirPdf.App.Rendering;
using AirPdf.App.Services;
using AirPdf.Core.Models;
using AirPdf.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AirPdf.App.ViewModels;

public sealed class PageViewModel : ObservableObject, IDisposable
{
    public const double PixelsPerPointAt100 = 96.0 / 72.0;

    private const int MinRenderWidth = 320;
    private const int MaxRenderWidth = 2400;
    private const long MaxRenderPixels = 7_000_000;
    private const double InteractionRenderScale = 0.65;

    private readonly PdfPageRenderer _renderer;
    private readonly LocalizationService _localization;
    private readonly RenderInteractionState? _interaction;
    private readonly Func<double>? _zoomProvider;
    private bool _interactionSubscribed;
    private CancellationTokenSource? _renderCancellation;
    private double _renderScale = 1;
    private int _renderGeneration;
    private int _renderedWidth;
    private bool _isActive;
    private bool _forceRender;
    private double _displayWidth;
    private double _displayHeight;
    private WriteableBitmap? _image;
    private int _imageRevision;
    private bool _isRendering;
    private string? _renderError;
    private IReadOnlyList<PdfSearchRect> _highlightRects = [];
    private IReadOnlyList<PdfSearchRect> _activeHighlightRects = [];
    private IReadOnlyList<PdfTextWord> _words = [];
    private IReadOnlyList<PdfLink> _links = [];
    private Task? _linkLoad;
    private IReadOnlyList<PdfSearchRect> _selectionRects = [];
    private double _inkWidthPoints = 2;

    /// <summary>Raised whenever the page's bitmap is installed or dropped.</summary>
    public event EventHandler? BitmapChanged;

    /// <summary>Raised when the page enters or leaves the realized page list.</summary>
    public event EventHandler? ActiveChanged;

    public PageViewModel(
        PdfPageRenderer renderer,
        PdfPageInfo pageInfo,
        LocalizationService localization,
        RenderInteractionState? interaction = null,
        Func<double>? zoomProvider = null)
    {
        _renderer = renderer;
        _localization = localization;
        _interaction = interaction;
        _zoomProvider = zoomProvider;
        Number = pageInfo.Number;
        WidthPoints = pageInfo.Width;
        HeightPoints = pageInfo.Height;
        ApplyZoom(1);
    }

    public int Number { get; }

    public double WidthPoints { get; }

    public double HeightPoints { get; }

    public string PageLabel => Number.ToString();

    public string PagePlaceholderText => _localization.Format("L.PageLabelFormat", Number);

    public string DimensionsLabel => $"{Math.Round(WidthPoints)} × {Math.Round(HeightPoints)} pt";

    public double DisplayWidth
    {
        get => _displayWidth;
        private set => SetProperty(ref _displayWidth, value);
    }

    public double DisplayHeight
    {
        get => _displayHeight;
        private set => SetProperty(ref _displayHeight, value);
    }

    public WriteableBitmap? Image
    {
        get => _image;
        private set
        {
            if (SetProperty(ref _image, value))
            {
                OnPropertyChanged(nameof(ShowPlaceholder));
                BitmapChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public bool ShowPlaceholder => Image is null;

    /// <summary>
    /// Increments after every completed render. The pooled bitmaps mean a render can reuse
    /// the same object, so tests and diagnostics cannot use reference identity to tell
    /// whether the page was redrawn.
    /// </summary>
    public int ImageRevision => _imageRevision;

    /// <summary>
    /// High quality when the page is at rest; Medium while scrolling or zooming so the
    /// compositor is not resampling a large bitmap at full quality every frame.
    /// </summary>
    public BitmapInterpolationMode BitmapInterpolationMode =>
        _interaction?.IsInteracting == true
            ? BitmapInterpolationMode.MediumQuality
            : BitmapInterpolationMode.HighQuality;

    /// <summary>Bytes held by this page's bitmap, or zero when nothing is rendered.</summary>
    public long BitmapBytes => _image is { } image
        ? (long)image.PixelSize.Width * image.PixelSize.Height * 4
        : 0;

    public bool IsRendering
    {
        get => _isRendering;
        private set => SetProperty(ref _isRendering, value);
    }

    public string? RenderError
    {
        get => _renderError;
        private set
        {
            if (SetProperty(ref _renderError, value))
            {
                OnPropertyChanged(nameof(HasRenderError));
            }
        }
    }

    public bool HasRenderError => !string.IsNullOrWhiteSpace(RenderError);

    public IReadOnlyList<PdfSearchRect> HighlightRects
    {
        get => _highlightRects;
        private set
        {
            if (SetProperty(ref _highlightRects, value))
            {
                OnPropertyChanged(nameof(HasHighlights));
            }
        }
    }

    public bool HasHighlights => HighlightRects.Count > 0;

    public IReadOnlyList<PdfSearchRect> ActiveHighlightRects
    {
        get => _activeHighlightRects;
        private set => SetProperty(ref _activeHighlightRects, value);
    }

    public IReadOnlyList<PdfTextWord> Words => _words;

    public bool HasTextLayer => _words.Count > 0;

    public IReadOnlyList<PdfSearchRect> SelectionRects
    {
        get => _selectionRects;
        private set
        {
            if (SetProperty(ref _selectionRects, value))
            {
                OnPropertyChanged(nameof(HasSelection));
            }
        }
    }

    public bool HasSelection => SelectionRects.Count > 0;

    public void SetTextLayout(PdfTextPage page)
    {
        _words = page.Words;
        OnPropertyChanged(nameof(Words));
        OnPropertyChanged(nameof(HasTextLayer));
    }

    /// <summary>
    /// Drops the parsed words for a page that is far from the viewport. The text layer is
    /// reparsed on demand if the page is selected or searched again.
    /// </summary>
    public void ReleaseTextLayout()
    {
        if (_words.Count == 0)
        {
            return;
        }

        _words = [];
        OnPropertyChanged(nameof(Words));
        OnPropertyChanged(nameof(HasTextLayer));
    }

    /// <summary>Link annotations of this page, normalized to the page. Empty until
    /// <see cref="EnsureLinksAsync"/> has run.</summary>
    public IReadOnlyList<PdfLink> Links => _links;

    /// <summary>Loads this page's links once; later calls reuse the same task.</summary>
    public Task EnsureLinksAsync() => _linkLoad ??= LoadLinksAsync();

    public PdfLink? HitTestLink(double x, double y)
    {
        foreach (var link in _links)
        {
            if (link.Contains(x, y))
            {
                return link;
            }
        }

        return null;
    }

    private async Task LoadLinksAsync()
    {
        try
        {
            var links = await _renderer.GetLinksAsync(Number, CancellationToken.None).ConfigureAwait(true);
            _links = links;
            OnPropertyChanged(nameof(Links));
        }
        catch
        {
            // A malformed annotation must never break the page.
            _links = [];
        }
    }

    /// <summary>
    /// The character under a normalized page point, or a miss when the point is not on text.
    /// Selection is character level, so a word index alone is not precise enough to anchor on.
    /// </summary>
    public TextHit HitTestChar(double x, double y)
    {
        const double padding = 0.003;
        for (var index = 0; index < _words.Count; index++)
        {
            var bounds = _words[index].Bounds;
            if (x >= bounds.X - padding &&
                x <= bounds.X + bounds.Width + padding &&
                y >= bounds.Y - padding &&
                y <= bounds.Y + bounds.Height + padding)
            {
                return new TextHit(index, CharAtX(_words[index], x));
            }
        }

        return TextHit.Miss;
    }

    /// <summary>
    /// The character closest to a point that is not on any text. Dragging past the end of a
    /// line still has to extend the selection to something, which is what this provides.
    /// </summary>
    public TextHit NearestChar(double x, double y)
    {
        var index = NearestWordIndex(x, y);
        return index < 0 ? TextHit.Miss : new TextHit(index, CharAtX(_words[index], x));
    }

    /// <summary>
    /// The character whose box contains <paramref name="x"/>, or the closest one when the point
    /// falls in the gap between two boxes.
    /// </summary>
    private static int CharAtX(PdfTextWord word, double x)
    {
        var chars = word.Chars;
        if (chars.Count == 0)
        {
            return 0;
        }

        var best = 0;
        var bestDistance = double.MaxValue;
        for (var index = 0; index < chars.Count; index++)
        {
            var bounds = chars[index].Bounds;
            if (x >= bounds.X && x <= bounds.X + bounds.Width)
            {
                return index;
            }

            var center = bounds.X + (bounds.Width / 2);
            var distance = Math.Abs(x - center);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = index;
            }
        }

        return best;
    }

    private int NearestWordIndex(double x, double y)
    {
        if (_words.Count == 0)
        {
            return -1;
        }

        var bestIndex = -1;
        var bestDistance = double.MaxValue;
        for (var i = 0; i < _words.Count; i++)
        {
            var bounds = _words[i].Bounds;
            var centerX = bounds.X + (bounds.Width / 2);
            var centerY = bounds.Y + (bounds.Height / 2);
            var dx = Math.Max(0, Math.Abs(x - centerX) - (bounds.Width / 2));
            var dy = Math.Max(0, Math.Abs(y - centerY) - (bounds.Height / 2));
            var distance = (dx * dx) + (dy * dy);

            if (distance < bestDistance)
            {
                bestDistance = distance;
                bestIndex = i;
            }
        }

        return bestIndex;
    }

    public void SetSelectionRects(IReadOnlyList<PdfSearchRect> rects)
    {
        SelectionRects = rects;
    }

    public void ClearSelectionRects() => SelectionRects = [];

    public void ApplyZoom(double zoom)
    {
        var width = Math.Max(1, WidthPoints * PixelsPerPointAt100 * zoom);
        var height = Math.Max(1, HeightPoints * PixelsPerPointAt100 * zoom);
        if (Math.Abs(width - DisplayWidth) < 0.1 && Math.Abs(height - DisplayHeight) < 0.1)
        {
            return;
        }

        DisplayWidth = width;
        DisplayHeight = height;
        OnPropertyChanged(nameof(InkPreviewThickness));
        OnPropertyChanged(nameof(DimensionsLabel));

        if (_isActive)
        {
            QueueRender();
        }
    }

    public void Activate(double renderScale)
    {
        var wasActive = _isActive;
        _isActive = true;
        _renderScale = Math.Clamp(renderScale, 1, 3);

        if (_interaction is not null && !_interactionSubscribed)
        {
            _interaction.Changed += OnInteractionChanged;
            _interactionSubscribed = true;
        }

        OnPropertyChanged(nameof(BitmapInterpolationMode));

        if (!wasActive)
        {
            ActiveChanged?.Invoke(this, EventArgs.Empty);
        }

        QueueRender();
    }

    /// <summary>True while a page view is showing this page and it may render.</summary>
    public bool IsActive => _isActive;

    public void Deactivate()
    {
        var wasActive = _isActive;
        _clearLiveInkAfterRender = false;
        ClearLiveInk();
        _isActive = false;
        CancelRender();
        IsRendering = false;

        if (_interaction is not null && _interactionSubscribed)
        {
            _interaction.Changed -= OnInteractionChanged;
            _interactionSubscribed = false;
        }

        if (wasActive)
        {
            ActiveChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnInteractionChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(BitmapInterpolationMode));

        if (_isActive &&
            _interaction?.IsInteracting == false &&
            HighTargetWidth() > _renderedWidth + 8)
        {
            // The page may have kept an older, high-quality bitmap while the interaction
            // target was small, or may have finished a reduced render after the interaction
            // ended. Either way, now is the time to refine it.
            RequestRender();
        }
    }

    /// <summary>
    /// Re-renders the page even though it already has a bitmap, for example after an annotation
    /// was added to it.
    /// </summary>
    private IReadOnlyList<PdfSearchRect> _liveInk = [];
    private IBrush? _inkBrush;
    private bool _clearLiveInkAfterRender;

    /// <summary>
    /// The width of the stroke being drawn, in points. The document stores ink in points, so the
    /// live stroke has to be scaled by the same factor as the page or it changes size the moment
    /// it is committed.
    /// </summary>
    public double InkWidthPoints
    {
        get => _inkWidthPoints;
        set
        {
            if (SetProperty(ref _inkWidthPoints, value))
            {
                OnPropertyChanged(nameof(InkPreviewThickness));
            }
        }
    }

    /// <summary>
    /// The live stroke's thickness in display units: points scaled by the page's current scale,
    /// which is what makes the stroke under the pointer the same size as the annotation drawn
    /// over it once the page is re-rendered.
    /// </summary>
    public double InkPreviewThickness
    {
        get
        {
            var scale = _zoomProvider?.Invoke() is { } zoom
                ? zoom
                : DisplayWidth / Math.Max(1, WidthPoints);
            return _inkWidthPoints * PixelsPerPointAt100 * scale;
        }
    }

    /// <summary>
    /// The stroke being drawn right now, as normalized points, so the reader sees the line under
    /// the pointer before it becomes an annotation.
    /// </summary>
    public IReadOnlyList<PdfSearchRect> LiveInk
    {
        get => _liveInk;
        private set
        {
            if (SetProperty(ref _liveInk, value))
            {
                OnPropertyChanged(nameof(HasLiveInk));
            }
        }
    }

    public bool HasLiveInk => _liveInk.Count > 0;

    /// <summary>
    /// Brush for the stroke being drawn, so the line under the pointer is already the colour it
    /// will have once it becomes an annotation.
    /// </summary>
    public IBrush? InkBrush
    {
        get => _inkBrush;
        set => SetProperty(ref _inkBrush, value);
    }

    /// <summary>
    /// Keeps the stroke on screen until the page has been redrawn with it baked in. Clearing it
    /// when the pointer is released makes the line vanish and come back, which reads as a flicker.
    /// </summary>
    public void ClearLiveInkAfterRender()
    {
        _clearLiveInkAfterRender = true;
    }

    public void SetLiveInk(IReadOnlyList<(double X, double Y)> points) =>
        LiveInk = points.Select(point => new PdfSearchRect(point.X, point.Y, 0, 0)).ToList();

    public void ClearLiveInk()
    {
        if (_liveInk.Count > 0)
        {
            LiveInk = [];
        }
    }
    public void RequestRender()
    {
        _forceRender = true;
        QueueRender();
    }

    public void SetHighlights(IReadOnlyList<PdfSearchRect> rects, IReadOnlyList<PdfSearchRect> activeRects)
    {
        HighlightRects = rects;
        ActiveHighlightRects = activeRects;
    }

    public void ClearHighlights()
    {
        HighlightRects = [];
        ActiveHighlightRects = [];
    }

    public void RefreshLanguage() => OnPropertyChanged(nameof(PagePlaceholderText));

    public void TrimImage()
    {
        if (_isActive)
        {
            return;
        }

        var image = Image;
        Image = null;
        DisposeBitmapLater(image);
        _renderedWidth = 0;
    }

    public void Dispose()
    {
        CancelRender();

        if (_interaction is not null && _interactionSubscribed)
        {
            _interaction.Changed -= OnInteractionChanged;
            _interactionSubscribed = false;
        }

        var image = Image;
        Image = null;
        DisposeBitmapLater(image);
    }

    private void CancelRender()
    {
        var cancellation = _renderCancellation;
        _renderCancellation = null;
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
            // The render has already completed and disposed its own lifetime.
        }
    }

    private void QueueRender()
    {
        if (!_isActive)
        {
            return;
        }

        CancelRender();
        var cancellation = new CancellationTokenSource();
        _renderCancellation = cancellation;
        var generation = ++_renderGeneration;
        _ = RenderAsync(generation, cancellation);
    }

    private async Task RenderAsync(int generation, CancellationTokenSource cancellation)
    {
        try
        {
            // Debounce re-renders caused by zoom/resize, but do not make a page that has no
            // bitmap at all wait 90 ms before it can show anything.
            if (Image is not null)
            {
                await Task.Delay(90, cancellation.Token);
            }

            var interacting = _interaction?.IsInteracting == true;
            var qualityScale = interacting ? InteractionRenderScale : 1.0;
            var targetWidth = TargetWidth(qualityScale);

            // A page that already has a bitmap at this width skips the render, which is right
            // for scrolling and wrong after an annotation was added: the change would stay
            // invisible until the copy was reopened.
            if (!_forceRender && Image is not null && targetWidth <= _renderedWidth + 8)
            {
                return;
            }

            _forceRender = false;

            IsRendering = true;
            RenderError = null;

            var priority = _isActive ? PdfRenderPriority.Visible : PdfRenderPriority.Page;
            using var skiaBitmap = await _renderer.RenderAsync(Number, targetWidth, cancellation.Token, priority);
            cancellation.Token.ThrowIfCancellationRequested();

            var bitmap = await Dispatcher.UIThread.InvokeAsync(() =>
            {
                var reusable = PageBitmapPool.Rent(new PixelSize(skiaBitmap.Width, skiaBitmap.Height));
                return BitmapHelper.CreateBitmap(skiaBitmap, reusable);
            });
            if (cancellation.IsCancellationRequested || generation != _renderGeneration)
            {
                bitmap.Dispose();
                return;
            }

            var previous = Image;
            Image = bitmap;
            _renderedWidth = targetWidth;
            _imageRevision++;
            DisposeBitmapLater(previous);

            if (interacting && _interaction?.IsInteracting == false)
            {
                // The interaction ended while this reduced render was in flight.
                RequestRender();
            }

            if (_clearLiveInkAfterRender)
            {
                _clearLiveInkAfterRender = false;
                ClearLiveInk();
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when scrolling or zooming quickly.
        }
        catch (Exception ex)
        {
            if (generation == _renderGeneration && !cancellation.IsCancellationRequested)
            {
                RenderError = ex is PdfDocumentException
                    ? _localization.GetPdfFailureMessage(((PdfDocumentException)ex).Failure)
                    : _localization.T("L.RenderError");
            }
        }
        finally
        {
            if (generation == _renderGeneration)
            {
                IsRendering = false;
            }

            if (ReferenceEquals(_renderCancellation, cancellation))
            {
                _renderCancellation = null;
            }

            cancellation.Dispose();
        }
    }

    private int HighTargetWidth() => TargetWidth(1.0);

    private int TargetWidth(double qualityScale)
    {
        var scaleWidth = (int)Math.Ceiling(DisplayWidth * _renderScale * qualityScale);
        var areaWidth = (int)(Math.Sqrt(MaxRenderPixels * (WidthPoints / Math.Max(1, HeightPoints))) * qualityScale);
        return Math.Clamp(Math.Min(scaleWidth, areaWidth), MinRenderWidth, MaxRenderWidth);
    }

    private static void DisposeBitmapLater(WriteableBitmap? bitmap)
    {
        if (bitmap is null)
        {
            return;
        }

        // Avalonia's compositor may still hold the previous bitmap for a frame.
        // Return it to the pool only after the visual tree has released it.
        var generation = PageBitmapPool.Generation;
        DispatcherTimer.RunOnce(() =>
        {
            try
            {
                PageBitmapPool.Return(bitmap, generation);
            }
            catch
            {
                // A shutdown race must not take down the process.
            }
        }, TimeSpan.FromMilliseconds(300));
    }
}
