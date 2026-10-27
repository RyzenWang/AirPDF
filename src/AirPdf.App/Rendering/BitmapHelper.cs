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
    public static WriteableBitmap CreateBitmap(SKBitmap source)
    {
        var width = source.Width;
        var height = source.Height;
        if (width <= 0 || height <= 0)
        {
            throw new InvalidOperationException("The renderer returned an empty page.");
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

            var target = new WriteableBitmap(
                new PixelSize(width, height),
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

                for (var y = 0; y < height; y++)
                {
                    Buffer.MemoryCopy(
                        sourceBase + (y * sourceRowBytes),
                        targetBase + (y * targetRowBytes),
                        targetRowBytes,
                        copyBytes);
                }
            }

            return target;
        }
        finally
        {
            normalized?.Dispose();
        }
    }
}
