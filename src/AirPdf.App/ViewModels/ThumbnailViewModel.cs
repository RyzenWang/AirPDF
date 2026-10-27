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
        _renderCancellation?.Cancel();
        IsRendering = false;
    }

    public void Dispose()
    {
        _renderCancellation?.Cancel();
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
            previous?.Dispose();
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

            cancellation.Dispose();
        }
    }
}
