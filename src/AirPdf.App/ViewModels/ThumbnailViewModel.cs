using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using AirPdf.App.Rendering;
using AirPdf.Core.Models;
using AirPdf.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AirPdf.App.ViewModels;

public sealed class ThumbnailViewModel : ObservableObject, IDisposable
{
    private const double ThumbnailWidth = 124;
    private const int RenderWidth = 220;

    private readonly PdfPageRenderer _renderer;
    private readonly PageViewModel? _sourcePage;
    private CancellationTokenSource? _renderCancellation;
    private bool _isActive;
    private int _generation;
    private WriteableBitmap? _image;
    private bool _isRendering;

    /// <summary>Raised whenever the thumbnail's bitmap is installed or dropped.</summary>
    public event EventHandler? BitmapChanged;

    public ThumbnailViewModel(
        PdfPageRenderer renderer,
        PdfPageInfo pageInfo,
        PageViewModel? sourcePage = null)
    {
        _renderer = renderer;
        _sourcePage = sourcePage;
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
                BitmapChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public bool ShowPlaceholder => Image is null;

    /// <summary>True while a realized rail item is showing this thumbnail.</summary>
    public bool IsActive => _isActive;

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

    /// <summary>
    /// Drops the preview bitmap of a thumbnail that has scrolled far from the current page.
    /// The rail follows the reader page by page, so without this every thumbnail ever shown
    /// would keep its bitmap for the lifetime of the document.
    /// </summary>
    public void TrimImage()
    {
        if (_isActive)
        {
            return;
        }

        var image = Image;
        if (image is null)
        {
            return;
        }

        Image = null;
        DisposeBitmapLater(image);
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

            WriteableBitmap? bitmap = null;
            var sourceImage = _sourcePage?.Image;

            // The page bitmap is already decoded at a larger size. Downscaling it avoids a
            // second PDFium page render and keeps the thumbnail lane out of the render gate.
            if (sourceImage is not null && sourceImage.PixelSize.Width > RenderWidth)
            {
                try
                {
                    var targetHeight = Math.Max(
                        1,
                        (int)Math.Round(
                            RenderWidth *
                            (sourceImage.PixelSize.Height / (double)sourceImage.PixelSize.Width)));
                    bitmap = await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        var reusable = PageBitmapPool.Rent(new PixelSize(RenderWidth, targetHeight));
                        return BitmapHelper.CreateScaledBitmap(sourceImage, RenderWidth, reusable);
                    });
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    // The page bitmap can be replaced while the rail catches up; fall back to
                    // a fresh PDFium thumbnail in that case.
                    bitmap = null;
                }
            }

            if (bitmap is null)
            {
                using var skiaBitmap = await _renderer.RenderAsync(
                    Number,
                    RenderWidth,
                    cancellation.Token,
                    PdfRenderPriority.Thumbnail);
                cancellation.Token.ThrowIfCancellationRequested();

                bitmap = await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    var reusable = PageBitmapPool.Rent(new PixelSize(skiaBitmap.Width, skiaBitmap.Height));
                    return BitmapHelper.CreateBitmap(skiaBitmap, reusable);
                });
            }

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
