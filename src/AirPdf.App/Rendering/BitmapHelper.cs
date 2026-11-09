using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using SkiaSharp;

namespace AirPdf.App.Rendering;

internal static class BitmapHelper
{
    /// <summary>
    /// Copies an Skia bitmap into an Avalonia writeable bitmap, staying in BGRA8888
    /// so no PNG encode/decode round-trip is needed.
    /// </summary>
    public static WriteableBitmap CreateBitmap(SKBitmap source, WriteableBitmap? reusable = null)
    {
        var width = source.Width;
        var height = source.Height;
        if (width <= 0 || height <= 0)
        {
            throw new InvalidOperationException("The renderer returned an empty page.");
        }

        var size = new PixelSize(width, height);
        var target = reusable is not null && reusable.PixelSize == size ? reusable : null;
        if (reusable is not null && target is null)
        {
            reusable.Dispose();
        }

        SKBitmap? normalized = null;
        var bitmap = source;
        try
        {
            if (source.ColorType != SKColorType.Bgra8888 || source.AlphaType != SKAlphaType.Premul)
            {
                normalized = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
                using var canvas = new SKCanvas(normalized);
                canvas.Clear(SKColors.White);
                canvas.DrawBitmap(source, 0, 0, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None), null);
                canvas.Flush();
                bitmap = normalized;
            }

            target ??= new WriteableBitmap(
                size,
                new Vector(96, 96),
                PixelFormat.Bgra8888,
                AlphaFormat.Premul);

            using var framebuffer = target.Lock();
            var sourcePixels = bitmap.GetPixels();
            if (sourcePixels == IntPtr.Zero)
            {
                target.Dispose();
                throw new InvalidOperationException("The renderer returned no pixels.");
            }

            unsafe
            {
                var sourceBase = (byte*)sourcePixels;
                var targetBase = (byte*)framebuffer.Address;
                var sourceRowBytes = bitmap.RowBytes;
                var targetRowBytes = framebuffer.RowBytes;
                var copyBytes = Math.Min(sourceRowBytes, targetRowBytes);

                if (sourceRowBytes == targetRowBytes)
                {
                    // The common BGRA case: one bulk copy instead of one call per row.
                    Buffer.MemoryCopy(
                        sourceBase,
                        targetBase,
                        (long)targetRowBytes * height,
                        (long)sourceRowBytes * height);
                }
                else
                {
                    for (var y = 0; y < height; y++)
                    {
                        Buffer.MemoryCopy(
                            sourceBase + (y * sourceRowBytes),
                            targetBase + (y * targetRowBytes),
                            targetRowBytes,
                            copyBytes);
                    }
                }
            }

            return target;
        }
        finally
        {
            normalized?.Dispose();
        }
    }

    /// <summary>
    /// Creates a thumbnail by downscaling an existing page bitmap instead of asking PDFium
    /// to render the page a second time. Must be called on the UI thread because it reads the
    /// page's locked framebuffer; the source pixels are wrapped, not copied.
    /// </summary>
    public static WriteableBitmap CreateScaledBitmap(
        WriteableBitmap source,
        int targetWidth,
        WriteableBitmap? reusable = null)
    {
        var sourceWidth = source.PixelSize.Width;
        var sourceHeight = source.PixelSize.Height;
        if (sourceWidth <= 0 || sourceHeight <= 0 || targetWidth <= 0)
        {
            throw new InvalidOperationException("The bitmap has no pixels.");
        }

        var targetHeight = Math.Max(1, (int)Math.Round(targetWidth * (sourceHeight / (double)sourceWidth)));

        using var framebuffer = source.Lock();
        using var sourceBitmap = new SKBitmap();
        var sourceInfo = new SKImageInfo(
            sourceWidth,
            sourceHeight,
            SKColorType.Bgra8888,
            SKAlphaType.Premul);
        if (!sourceBitmap.InstallPixels(sourceInfo, framebuffer.Address, framebuffer.RowBytes))
        {
            throw new InvalidOperationException("The source bitmap could not be wrapped.");
        }

        using var scaled = new SKBitmap(
            new SKImageInfo(targetWidth, targetHeight, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(scaled))
        {
            canvas.Clear(SKColors.White);
            canvas.DrawBitmap(
                sourceBitmap,
                new SKRect(0, 0, sourceWidth, sourceHeight),
                new SKRect(0, 0, targetWidth, targetHeight),
                new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear),
                null);
            canvas.Flush();
        }

        return CreateBitmap(scaled, reusable);
    }
}
