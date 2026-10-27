using Avalonia.Media.Imaging;
using Avalonia.Threading;
using AirPdf.App.Rendering;
using AirPdf.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AirPdf.App.ViewModels;

public sealed class ThumbnailViewModel : ObservableObject, IDisposable
{
    private const double ThumbnailWidth = 124;
    private const int RenderWidth = 220;

    private readonly PdfPageRenderer _renderer;
    private CancellationTokenSource? _renderCancellation;
    private bool _isActive;
    private int _generation;
    private WriteableBitmap? _image;
    private bool _isRendering;

    public ThumbnailViewModel(PdfPageRenderer renderer, PdfPageInfo pageInfo)
    {
        _renderer = renderer;
        Number = pageInfo.Number;
        Height = Math.Max(80, ThumbnailWidth * (pageInfo.Height / Math.Max(1, pageInfo.Width)));
    }

    public int Number { get; }

    public double Width => ThumbnailWidth;

    public double Height { get; }

    public string PageLabel => Number.ToString();

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

    public void Activate()
    {
        if (_isActive && Image is not null)
        {
            return;
        }

        _isActive = true;
        QueueRender();
    }

    public void Deactivate()
    {
        _isActive = false;
        CancelRender();
        IsRendering = false;
    }

    public void Dispose()
    {
        CancelRender();
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
        var generation = ++_generation;
        _ = RenderAsync(generation, cancellation);
    }

    private async Task RenderAsync(int generation, CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(70, cancellation.Token);
            IsRendering = true;

            using var skiaBitmap = await _renderer.RenderAsync(Number, RenderWidth, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();

            var bitmap = await Dispatcher.UIThread.InvokeAsync(() => BitmapHelper.CreateBitmap(skiaBitmap));
            if (cancellation.IsCancellationRequested || generation != _generation)
            {
                bitmap.Dispose();
                return;
            }

            var previous = Image;
            Image = bitmap;
            DisposeBitmapLater(previous);
        }
        catch (OperationCanceledException)
        {
            // Expected while scrolling the thumbnail rail.
        }
        catch
        {
            // Thumbnails are decorative. The main page view surfaces render failures.
        }
        finally
        {
            if (generation == _generation)
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

    private static void DisposeBitmapLater(WriteableBitmap? bitmap)
    {
        if (bitmap is null)
        {
            return;
        }

        DispatcherTimer.RunOnce(() =>
        {
            try
            {
                bitmap.Dispose();
            }
            catch
            {
                // A shutdown race must not take down the process.
            }
        }, TimeSpan.FromMilliseconds(300));
    }
}
