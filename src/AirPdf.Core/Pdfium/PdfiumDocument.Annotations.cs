using System.Runtime.InteropServices;
using AirPdf.Core.Models;

namespace AirPdf.Core.Pdfium;

/// <summary>
/// Annotation editing and saving. Both live here because they belong together: an annotation
/// that cannot be written to a file is a decoration, and a save that cannot carry annotations
/// has nothing to carry.
/// </summary>
public sealed partial class PdfiumDocument
{
    /// <summary>
    /// The page's <c>/Rotate</c> in degrees. Selections are recorded in display space, so
    /// mapping them back for an annotation needs the same rotation that produced them.
    /// </summary>
    public int GetPageRotation(int pageNumber)
    {
        ThrowIfDisposed();

        var page = LoadPageOrThrow(pageNumber);
        try
        {
            return PageGeometry.NormalizeRotation(PdfiumNative.GetPageRotation(page) * 90);
        }
        finally
        {
            PdfiumNative.ClosePage(page);
        }
    }
    /// <summary>Number of annotations on a page, including the PDF's own ones.</summary>
    public int GetAnnotationCount(int pageNumber)
    {
        ThrowIfDisposed();

        var page = LoadPageOrThrow(pageNumber);
        try
        {
            return PdfiumNative.PageGetAnnotCount(page);
        }
        finally
        {
            PdfiumNative.ClosePage(page);
        }
    }

    /// <summary>Subtype of an annotation, for verification and for future editing.</summary>
    public int GetAnnotationSubtype(int pageNumber, int index)
    {
        ThrowIfDisposed();

        var page = LoadPageOrThrow(pageNumber);
        try
        {
            var annot = PdfiumNative.PageGetAnnot(page, index);
            if (annot == IntPtr.Zero)
            {
                throw new PdfDocumentException(PdfFailure.Unknown, $"Annotation {index} is missing.");
            }

            try
            {
                return PdfiumNative.AnnotGetSubtype(annot);
            }
            finally
            {
                PdfiumNative.PageCloseAnnot(annot);
            }
        }
        finally
        {
            PdfiumNative.ClosePage(page);
        }
    }

    /// <summary>
    /// Reads a highlight back out of the document: the rectangles it covers and its colour.
    /// This is what makes a removal undoable.
    /// </summary>
    public PdfHighlight? TryReadHighlight(int pageNumber, int index)
    {
        ThrowIfDisposed();

        var page = LoadPageOrThrow(pageNumber);
        try
        {
            var annot = PdfiumNative.PageGetAnnot(page, index);
            if (annot == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                if (PdfiumNative.AnnotGetSubtype(annot) != PdfiumNative.AnnotHighlight)
                {
                    return null;
                }

                var count = (int)PdfiumNative.AnnotCountAttachmentPoints(annot).ToUInt32();
                var quads = new List<PdfQuad>(count);
                for (var quad = 0; quad < count; quad++)
                {
                    if (PdfiumNative.AnnotGetAttachmentPoints(annot, (UIntPtr)(uint)quad, out var points) == 0)
                    {
                        continue;
                    }

                    quads.Add(QuadFrom(points));
                }

                if (quads.Count == 0)
                {
                    // Older highlights may carry only a rectangle; fall back to it.
                    if (PdfiumNative.AnnotGetRect(annot, out var rect) != 0)
                    {
                        quads.Add(new PdfQuad(rect.Left, rect.Top, rect.Right, rect.Bottom));
                    }
                }

                byte red = 0;
                byte green = 0;
                byte blue = 0;
                if (PdfiumNative.AnnotGetColor(annot, PdfiumNative.AnnotColorStroke, out var r, out var g, out var b, out _) != 0)
                {
                    red = (byte)Math.Min(r, 255);
                    green = (byte)Math.Min(g, 255);
                    blue = (byte)Math.Min(b, 255);
                }

                return new PdfHighlight(pageNumber, quads, red, green, blue);
            }
            finally
            {
                PdfiumNative.PageCloseAnnot(annot);
            }
        }
        finally
        {
            PdfiumNative.ClosePage(page);
        }
    }

    /// <summary>
    /// Removes the highlight whose rectangles match <paramref name="quads"/>, which is how an
    /// added highlight is taken back without depending on an annotation index that later edits
    /// may have shifted.
    /// </summary>
    /// <returns>True when a matching highlight was removed.</returns>
    public bool RemoveHighlightMatching(int pageNumber, IReadOnlyList<PdfQuad> quads)
    {
        ThrowIfDisposed();

        if (quads.Count == 0)
        {
            return false;
        }

        var page = LoadPageOrThrow(pageNumber);
        try
        {
            for (var index = PdfiumNative.PageGetAnnotCount(page) - 1; index >= 0; index--)
            {
                var existing = TryReadHighlight(pageNumber, index);
                if (existing is null || !SameGeometry(existing.Quads, quads))
                {
                    continue;
                }

                return PdfiumNative.PageRemoveAnnot(page, index) != 0;
            }

            return false;
        }
        finally
        {
            PdfiumNative.ClosePage(page);
        }
    }

    /// <summary>Bounds of one annotation, in page space.</summary>
    public PdfQuad GetAnnotationBounds(int pageNumber, int index)
    {
        ThrowIfDisposed();

        var page = LoadPageOrThrow(pageNumber);
        try
        {
            var annot = PdfiumNative.PageGetAnnot(page, index);
            if (annot == IntPtr.Zero)
            {
                throw new PdfDocumentException(PdfFailure.Unknown, $"Annotation {index} is missing.");
            }

            try
            {
                if (PdfiumNative.AnnotGetRect(annot, out var rect) == 0)
                {
                    return default;
                }

                return new PdfQuad(rect.Left, rect.Top, rect.Right, rect.Bottom);
            }
            finally
            {
                PdfiumNative.PageCloseAnnot(annot);
            }
        }
        finally
        {
            PdfiumNative.ClosePage(page);
        }
    }

    /// <summary>
    /// Removes every highlight on the page whose bounds contain <paramref name="point"/>.
    /// </summary>
    /// <returns>How many annotations were removed.</returns>
    /// <remarks>
    /// Only highlights are considered, so a page's form fields and links are never touched by a
    /// click meant for markup. Indices are walked downwards because removing one shifts the rest.
    /// </remarks>
    public IReadOnlyList<PdfHighlight> RemoveHighlightsAt(int pageNumber, PdfQuad point)
    {
        ThrowIfDisposed();

        var page = LoadPageOrThrow(pageNumber);
        try
        {
            var removed = new List<PdfHighlight>();
            for (var index = PdfiumNative.PageGetAnnotCount(page) - 1; index >= 0; index--)
            {
                var annot = PdfiumNative.PageGetAnnot(page, index);
                if (annot == IntPtr.Zero)
                {
                    continue;
                }

                try
                {
                    if (PdfiumNative.AnnotGetSubtype(annot) != PdfiumNative.AnnotHighlight)
                    {
                        continue;
                    }

                    if (PdfiumNative.AnnotGetRect(annot, out var rect) == 0)
                    {
                        continue;
                    }

                    var contains = point.Left >= rect.Left && point.Left <= rect.Right &&
                                   point.Top >= rect.Bottom && point.Top <= rect.Top;
                    if (!contains)
                    {
                        continue;
                    }

                    var snapshot = TryReadHighlight(pageNumber, index);
                    if (PdfiumNative.PageRemoveAnnot(page, index) != 0 && snapshot is not null)
                    {
                        removed.Add(snapshot);
                    }
                }
                finally
                {
                    PdfiumNative.PageCloseAnnot(annot);
                }
            }

            return removed;
        }
        finally
        {
            PdfiumNative.ClosePage(page);
        }
    }
    /// <summary>
    /// Adds one highlight covering the given page-space rectangles. All rectangles go into a
    /// single annotation, which is what a reader expects: selecting three lines and highlighting
    /// them is one highlight, not three.
    /// </summary>
    /// <param name="quads">Rectangles in unrotated page space (points, origin bottom-left).</param>
    /// <param name="red">Red component, 0-255.</param>
    /// <param name="green">Green component, 0-255.</param>
    /// <param name="blue">Blue component, 0-255.</param>
    public void AddHighlight(int pageNumber, IReadOnlyList<PdfQuad> quads, byte red, byte green, byte blue)
    {
        ThrowIfDisposed();

        if (quads.Count == 0)
        {
            return;
        }

        var page = LoadPageOrThrow(pageNumber);
        var annot = IntPtr.Zero;
        try
        {
            annot = PdfiumNative.PageCreateAnnot(page, PdfiumNative.AnnotHighlight);
            if (annot == IntPtr.Zero)
            {
                throw new PdfDocumentException(PdfFailure.Unsupported, "This page does not accept annotations.");
            }

            foreach (var quad in quads)
            {
                var points = new PdfiumNative.FsQuadPointsF
                {
                    X1 = (float)quad.Left,
                    Y1 = (float)quad.Top,
                    X2 = (float)quad.Right,
                    Y2 = (float)quad.Top,
                    X3 = (float)quad.Left,
                    Y3 = (float)quad.Bottom,
                    X4 = (float)quad.Right,
                    Y4 = (float)quad.Bottom
                };

                PdfiumNative.AnnotAppendAttachmentPoints(annot, ref points);
            }

            var bounds = BoundingBox(quads);
            var rect = new PdfiumNative.FsRectF
            {
                Left = (float)bounds.Left,
                Top = (float)bounds.Top,
                Right = (float)bounds.Right,
                Bottom = (float)bounds.Bottom
            };

            PdfiumNative.AnnotSetRect(annot, ref rect);
            PdfiumNative.AnnotSetColor(annot, PdfiumNative.AnnotColorStroke, red, green, blue, 255);
            PdfiumNative.AnnotSetColor(annot, PdfiumNative.AnnotColorFill, red, green, blue, 255);
            PdfiumNative.AnnotSetFlags(annot, PdfiumNative.AnnotFlagPrint);
        }
        finally
        {
            if (annot != IntPtr.Zero)
            {
                PdfiumNative.PageCloseAnnot(annot);
            }

            PdfiumNative.ClosePage(page);
        }
    }

    /// <summary>
    /// Writes the document, annotations included, to a new file. The document that was opened is
    /// never written to: saving a copy is the only way changes leave the application.
    /// </summary>
    public void SaveCopy(string destination)
    {
        ThrowIfDisposed();

        ArgumentException.ThrowIfNullOrWhiteSpace(destination);

        var fullPath = Path.GetFullPath(destination);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var stream = new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.None);

        var write = new PdfiumNative.FileWriteDelegate(WriteBlock);
        var handle = GCHandle.Alloc(stream);
        var context = new PdfiumNative.FileWriteContext
        {
            Base = new PdfiumNative.FpdfFileWrite
            {
                Version = 1,
                WriteBlock = Marshal.GetFunctionPointerForDelegate(write)
            },
            Stream = GCHandle.ToIntPtr(handle)
        };

        try
        {
            if (PdfiumNative.SaveAsCopy(_handle, ref context.Base, PdfiumNative.SaveNoIncremental) == 0)
            {
                throw new PdfDocumentException(PdfFailure.Unknown, "The document could not be written.");
            }
        }
        finally
        {
            handle.Free();

            // The function pointer above is only valid while the delegate is reachable.
            GC.KeepAlive(write);
        }
    }

    private static int WriteBlock(IntPtr context, IntPtr data, uint size)
    {
        try
        {
            var streamHandle = Marshal.ReadIntPtr(context, Marshal.SizeOf<PdfiumNative.FpdfFileWrite>());
            if (streamHandle == IntPtr.Zero || data == IntPtr.Zero || size == 0)
            {
                return 1;
            }

            if (GCHandle.FromIntPtr(streamHandle).Target is not Stream stream)
            {
                return 0;
            }

            var buffer = new byte[size];
            Marshal.Copy(data, buffer, 0, (int)size);
            stream.Write(buffer, 0, (int)size);
            return 1;
        }
        catch
        {
            // Returning 0 aborts the save, which is what PDFium expects on failure.
            return 0;
        }
    }

    private IntPtr LoadPageOrThrow(int pageNumber)
    {
        var page = PdfiumNative.LoadPage(_handle, pageNumber - 1);
        if (page == IntPtr.Zero)
        {
            throw new PdfDocumentException(PdfFailure.Corrupt, $"Page {pageNumber} could not be opened.");
        }

        return page;
    }

    private static PdfQuad QuadFrom(PdfiumNative.FsQuadPointsF points) =>
        new(
            Math.Min(points.X1, points.X3),
            Math.Max(points.Y1, points.Y2),
            Math.Max(points.X2, points.X4),
            Math.Min(points.Y3, points.Y4));

    private static bool SameGeometry(IReadOnlyList<PdfQuad> left, IReadOnlyList<PdfQuad> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (var index = 0; index < left.Count; index++)
        {
            var a = left[index];
            var b = right[index];
            if (Math.Abs(a.Left - b.Left) > 0.01 ||
                Math.Abs(a.Top - b.Top) > 0.01 ||
                Math.Abs(a.Right - b.Right) > 0.01 ||
                Math.Abs(a.Bottom - b.Bottom) > 0.01)
            {
                return false;
            }
        }

        return true;
    }
    private static PdfQuad BoundingBox(IReadOnlyList<PdfQuad> quads)
    {
        var left = double.MaxValue;
        var top = double.MinValue;
        var right = double.MinValue;
        var bottom = double.MaxValue;

        foreach (var quad in quads)
        {
            left = Math.Min(left, quad.Left);
            right = Math.Max(right, quad.Right);
            top = Math.Max(top, quad.Top);
            bottom = Math.Min(bottom, quad.Bottom);
        }

        return new PdfQuad(left, top, right, bottom);
    }
}
