using Avalonia;
using Avalonia.Media.Imaging;

namespace AirPdf.App.Rendering;

/// <summary>
/// A small pool of retired page bitmaps. Reusing a bitmap avoids allocating another
/// multi-megabyte object and copying into fresh memory on every zoom/annotation render.
/// The pool is generation-scoped so bitmaps retired by a closed document are disposed
/// instead of being carried into the next one.
/// </summary>
internal static class PageBitmapPool
{
    private const int MaxEntries = 3;
    private const long MaxBytes = 64L * 1024 * 1024;

    private static readonly object Gate = new();
    private static readonly List<WriteableBitmap> Entries = [];
    private static int _generation;

    public static int Generation
    {
        get
        {
            lock (Gate)
            {
                return _generation;
            }
        }
    }

    public static WriteableBitmap? Rent(PixelSize size)
    {
        lock (Gate)
        {
            for (var index = 0; index < Entries.Count; index++)
            {
                var candidate = Entries[index];
                if (candidate.PixelSize != size)
                {
                    continue;
                }

                Entries.RemoveAt(index);
                return candidate;
            }
        }

        return null;
    }

    public static void Return(WriteableBitmap bitmap, int generation)
    {
        lock (Gate)
        {
            if (generation != _generation)
            {
                bitmap.Dispose();
                return;
            }

            var bytes = BytesFor(bitmap.PixelSize);
            if (Entries.Count >= MaxEntries ||
                Entries.Sum(entry => BytesFor(entry.PixelSize)) + bytes > MaxBytes)
            {
                bitmap.Dispose();
                return;
            }

            Entries.Add(bitmap);
        }
    }

    /// <summary>Drops every retired bitmap and invalidates timers from the old document.</summary>
    public static void Reset()
    {
        List<WriteableBitmap> retired;
        lock (Gate)
        {
            _generation++;
            retired = [.. Entries];
            Entries.Clear();
        }

        foreach (var bitmap in retired)
        {
            bitmap.Dispose();
        }
    }

    private static long BytesFor(PixelSize size) =>
        (long)size.Width * size.Height * 4;
}
