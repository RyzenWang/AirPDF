using Avalonia.Media.Imaging;
using Avalonia.Threading;
using AirPdf.App.Rendering;
using AirPdf.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AirPdf.App.ViewModels;

public sealed class PageViewModel : ObservableObject, IDisposable
{
    public const double PixelsPerPointAt100 = 96.0 / 72.0;

    private const int MinRenderWidth = 320;
    private const int MaxRenderWidth = 2400;
    private const long MaxRenderPixels = 7_000_000;

    private readonly PdfPageRenderer _renderer;
    private CancellationTokenSource? _renderCancellation;
    private readonly int _rotation;
    private double _renderScale = 1;
    private int _renderGeneration;
    private int _renderedWidth;
    private bool _isActive;
    private double _displayWidth;
    private double _displayHeight;
    private WriteableBitmap? _image;
    private bool _isRendering;
    private string? _renderError;
    private IReadOnlyList<PdfSearchRect> _highlightRects = [];
    private IReadOnlyList<PdfSearchRect> _activeHighlightRects = [];

    public PageViewModel(PdfPageRenderer renderer, PdfPageInfo pageInfo)
    {
        _renderer = renderer;
        Number = pageInfo.Number;
        WidthPoints = pageInfo.Width;
        HeightPoints = pageInfo.Height;
        _rotation = pageInfo.Rotation;
        ApplyZoom(1);
    }

    public int Number { get; }

    public double WidthPoints { get; }

    public double HeightPoints { get; }

    public bool CanHighlight => _rotation % 360 == 0;

    public string PageLabel => Number.ToString();

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
            }
        }
    }

    public bool ShowPlaceholder => Image is null;

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
        OnPropertyChanged(nameof(DimensionsLabel));

        if (_isActive)
        {
            QueueRender();
        }
    }

    public void Activate(double renderScale)
    {
        _isActive = true;
        _renderScale = Math.Clamp(renderScale, 1, 3);
        QueueRender();
    }

    public void Deactivate()
    {
        _isActive = false;
        _renderCancellation?.Cancel();
        IsRendering = false;
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

    public void TrimImage()
    {
        if (_isActive)
        {
            return;
        }

        var image = Image;
        Image = null;
        image?.Dispose();
        _renderedWidth = 0;
    }

    public void Dispose()
    {
        _renderCancellation?.Cancel();
        _renderCancellation = null;
        var image = Image;
        Image = null;
        image?.Dispose();
    }

    private void QueueRender()
    {
        if (!_isActive)
        {
            return;
        }

        _renderCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        _renderCancellation = cancellation;
        var generation = ++_renderGeneration;
        _ = RenderAsync(generation, cancellation);
    }

    private async Task RenderAsync(int generation, CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(90, cancellation.Token);

            var scaleWidth = (int)Math.Ceiling(DisplayWidth * _renderScale);
            var areaWidth = (int)Math.Sqrt(MaxRenderPixels * (WidthPoints / Math.Max(1, HeightPoints)));
            var targetWidth = Math.Clamp(Math.Min(scaleWidth, areaWidth), MinRenderWidth, MaxRenderWidth);

            if (Image is not null && targetWidth <= _renderedWidth + 8)
            {
                return;
            }

            IsRendering = true;
            RenderError = null;

            using var skiaBitmap = await _renderer.RenderAsync(Number, targetWidth, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();

            var bitmap = await Dispatcher.UIThread.InvokeAsync(() => BitmapHelper.CreateBitmap(skiaBitmap));
            if (cancellation.IsCancellationRequested || generation != _renderGeneration)
            {
                bitmap.Dispose();
                return;
            }

            var previous = Image;
            Image = bitmap;
            _renderedWidth = targetWidth;
            previous?.Dispose();
        }
        catch (OperationCanceledException)
        {
            // Expected when scrolling or zooming quickly.
        }
        catch (Exception ex)
        {
            if (generation == _renderGeneration && !cancellation.IsCancellationRequested)
            {
                RenderError = ex.Message;
            }
        }
        finally
        {
            if (generation == _renderGeneration)
            {
                IsRendering = false;
            }

            cancellation.Dispose();
        }
    }
}
