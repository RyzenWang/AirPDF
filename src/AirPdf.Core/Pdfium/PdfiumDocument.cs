using System.Buffers;
using System.Runtime.InteropServices;
using System.Text;
using AirPdf.Core.Models;
using SkiaSharp;

namespace AirPdf.Core.Pdfium;

/// <summary>Page size in PDF points, after the page's own rotation is applied.</summary>
public readonly record struct PdfPageSize(double Width, double Height);

/// <summary>
/// A PDFium document kept open while a document is displayed, so rendering a page costs
/// one page render instead of a full document parse. The file itself stays readable by
/// other processes: reads go through a callback over a <see cref="FileShare.ReadWrite"/>
/// stream rather than a native file lock.
/// </summary>
public sealed class PdfiumDocument : IDisposable
{
    private readonly FileStream _stream;

    // PDFium keeps the function pointer, so the delegate must outlive the handle.
    private readonly PdfiumNative.GetBlockCallback _readBlock;
    private readonly PdfiumNative.FileAccess _fileAccess;

    private IntPtr _handle;
    private bool _disposed;

    private PdfiumDocument(
        string filePath,
        FileStream stream,
        PdfiumNative.GetBlockCallback readBlock,
        PdfiumNative.FileAccess fileAccess,
        IntPtr handle)
    {
        FilePath = filePath;
        _stream = stream;
        _readBlock = readBlock;
        _fileAccess = fileAccess;
        _handle = handle;
    }

    public string FilePath { get; }

    public int PageCount
    {
        get
        {
            ThrowIfDisposed();
            return PdfiumNative.GetPageCount(_handle);
        }
    }

    /// <summary>
    /// Opens <paramref name="filePath"/> and keeps the handle until disposed.
    /// </summary>
    /// <exception cref="PdfDocumentException">The document cannot be opened.</exception>
    public static PdfiumDocument Open(string filePath, string? password)
    {
        PdfiumNative.EnsureLibrary();

        var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);

        var length = stream.Length;

        // FPDF_FILEACCESS::m_FileLen is a 32-bit unsigned long on Windows, so the same
        // limit is enforced everywhere instead of silently truncating on one platform.
        if (length > uint.MaxValue)
        {
            stream.Dispose();
            throw new PdfDocumentException(
                PdfFailure.Unsupported,
                "Documents larger than 4 GB are not supported.");
        }

        PdfiumNative.GetBlockCallback? readBlock = null;
        var passwordPointer = IntPtr.Zero;
        IntPtr handle;

        try
        {
            readBlock = (_, position, buffer, size) => ReadBlock(stream, position, buffer, size);

            var fileAccess = new PdfiumNative.FileAccess
            {
                FileLength = new CULong((uint)length),
                GetBlock = Marshal.GetFunctionPointerForDelegate(readBlock),
                Param = IntPtr.Zero
            };

            // PDFium expects the password as a UTF-8 byte string.
            if (!string.IsNullOrEmpty(password))
            {
                passwordPointer = Marshal.StringToCoTaskMemUTF8(password);
            }

            handle = PdfiumNative.LoadCustomDocument(ref fileAccess, passwordPointer);

            if (handle == IntPtr.Zero)
            {
                throw MapOpenFailure(PdfiumNative.LastError, password);
            }

            return new PdfiumDocument(filePath, stream, readBlock, fileAccess, handle);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
        finally
        {
            if (passwordPointer != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(passwordPointer);
            }
        }
    }

    public PdfPageSize GetPageSize(int pageNumber)
    {
        ThrowIfDisposed();

        if (PdfiumNative.GetPageSizeByIndexF(_handle, pageNumber - 1, out var size) == 0 ||
            size.Width <= 0 ||
            size.Height <= 0)
        {
            throw new PdfDocumentException(
                PdfFailure.Corrupt,
                $"Page {pageNumber} could not be read.");
        }

        return new PdfPageSize(size.Width, size.Height);
    }

    /// <summary>
    /// Renders one page at <paramref name="pixelWidth"/>, keeping the aspect ratio. The
    /// caller owns the returned bitmap.
    /// </summary>
    public SKBitmap RenderPage(int pageNumber, int pixelWidth)
    {
        ThrowIfDisposed();

        var size = GetPageSize(pageNumber);
        var width = Math.Max(1, pixelWidth);
        var height = Math.Max(1, (int)Math.Round(width * (size.Height / size.Width)));

        var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        var page = PdfiumNative.LoadPage(_handle, pageNumber - 1);
        if (page == IntPtr.Zero)
        {
            bitmap.Dispose();
            throw new PdfDocumentException(
                PdfFailure.Corrupt,
                $"Page {pageNumber} could not be rendered.");
        }

        var nativeBitmap = IntPtr.Zero;
        try
        {
            var format = PdfiumNative.BitmapBgra;

            nativeBitmap = PdfiumNative.BitmapCreateEx(
                width,
                height,
                format,
                bitmap.GetPixels(),
                bitmap.RowBytes);

            if (nativeBitmap == IntPtr.Zero)
            {
                throw new PdfDocumentException(PdfFailure.Unknown, "PDFium could not allocate a page bitmap.");
            }

            // Opaque white background, matching what a printed page looks like.
            PdfiumNative.BitmapFillRect(nativeBitmap, 0, 0, width, height, 0xFFFFFFFF);
            PdfiumNative.RenderPageBitmap(nativeBitmap, page, 0, 0, width, height, 0, PdfiumNative.RenderFlags);
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
        finally
        {
            if (nativeBitmap != IntPtr.Zero)
            {
                PdfiumNative.BitmapDestroy(nativeBitmap);
            }

            PdfiumNative.ClosePage(page);
        }

        return bitmap;
    }

    /// <summary>
    /// Returns the link annotations of one page, normalized to the page.
    /// </summary>
    /// <remarks>
    /// Rotated pages are skipped: PDFium reports annotation bounds in unrotated page space,
    /// and the transform is the same one search highlights and text selection already lack.
    /// Links never throw — a malformed annotation dictionary must not break a page.
    /// </remarks>
    public IReadOnlyList<PdfLink> GetLinks(int pageNumber)
    {
        ThrowIfDisposed();

        var page = PdfiumNative.LoadPage(_handle, pageNumber - 1);
        if (page == IntPtr.Zero)
        {
            return [];
        }

        try
        {
            if (PdfiumNative.GetPageRotation(page) % 4 != 0)
            {
                return [];
            }

            var width = PdfiumNative.GetPageWidthF(page);
            var height = PdfiumNative.GetPageHeightF(page);
            if (width <= 0 || height <= 0)
            {
                return [];
            }

            var links = new List<PdfLink>();
            var position = 0;

            while (PdfiumNative.LinkEnumerate(page, ref position, out var link) != 0 && link != IntPtr.Zero)
            {
                if (PdfiumNative.LinkGetAnnotRect(link, out var rect) == 0)
                {
                    continue;
                }

                var left = Math.Min(rect.Left, rect.Right);
                var right = Math.Max(rect.Left, rect.Right);
                var bottom = Math.Min(rect.Top, rect.Bottom);
                var top = Math.Max(rect.Top, rect.Bottom);

                var x = Math.Clamp(left / width, 0, 1);
                var w = Math.Clamp((right - left) / width, 0, 1 - x);
                var y = Math.Clamp((height - top) / height, 0, 1);
                var h = Math.Clamp((top - bottom) / height, 0, 1 - y);

                if (w <= 0.0001 || h <= 0.0001)
                {
                    continue;
                }

                if (TryReadTarget(link, out var kind, out var uri, out var targetPage))
                {
                    links.Add(new PdfLink
                    {
                        Bounds = new PdfSearchRect(x, y, w, h),
                        Kind = kind,
                        Uri = uri,
                        PageNumber = targetPage
                    });
                }
            }

            return links;
        }
        catch
        {
            return [];
        }
        finally
        {
            PdfiumNative.ClosePage(page);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_handle != IntPtr.Zero)
        {
            PdfiumNative.CloseDocument(_handle);
            _handle = IntPtr.Zero;
        }

        _stream.Dispose();
        GC.KeepAlive(_readBlock);
        GC.KeepAlive(_fileAccess);
    }

    private bool TryReadTarget(IntPtr link, out PdfLinkKind kind, out string? uri, out int pageNumber)
    {
        kind = PdfLinkKind.Unknown;
        uri = null;
        pageNumber = 0;

        var action = PdfiumNative.LinkGetAction(link);
        if (action != IntPtr.Zero)
        {
            var actionType = PdfiumNative.ActionGetType(action).Value;

            if (actionType == PdfiumNative.ActionUri)
            {
                uri = ReadUri(action);
                if (uri is null)
                {
                    return false;
                }

                kind = PdfLinkKind.Uri;
                return true;
            }

            // GoTo is resolved below through the link's destination. Launch, remote GoTo and
            // anything else is deliberately not followed: a PDF must not run programs.
            if (actionType != PdfiumNative.ActionGoTo)
            {
                return false;
            }
        }

        var destination = PdfiumNative.LinkGetDest(_handle, link);
        if (destination == IntPtr.Zero)
        {
            return false;
        }

        var index = PdfiumNative.DestGetPageIndex(_handle, destination);
        if (index < 0)
        {
            return false;
        }

        kind = PdfLinkKind.Page;
        pageNumber = index + 1;
        return true;
    }

    private string? ReadUri(IntPtr action)
    {
        var length = PdfiumNative.ActionGetUriPath(_handle, action, IntPtr.Zero, new CULong(0u)).Value;
        if (length == 0 || length > 8192)
        {
            return null;
        }

        var buffer = Marshal.AllocHGlobal((int)length);
        try
        {
            var written = PdfiumNative.ActionGetUriPath(_handle, action, buffer, new CULong((uint)length)).Value;
            if (written == 0)
            {
                return null;
            }

            var bytes = new byte[(int)Math.Min(written, length)];
            Marshal.Copy(buffer, bytes, 0, bytes.Length);

            var text = Encoding.UTF8.GetString(bytes).TrimEnd('\0').Trim();
            return text.Length == 0 ? null : text;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static int ReadBlock(FileStream stream, CULong position, IntPtr buffer, CULong size)
    {
        var length = checked((int)size.Value);
        if (length <= 0)
        {
            return 1;
        }

        var rented = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            stream.Position = checked((long)position.Value);

            var read = 0;
            while (read < length)
            {
                var chunk = stream.Read(rented, read, length - read);
                if (chunk <= 0)
                {
                    return 0;
                }

                read += chunk;
            }

            Marshal.Copy(rented, 0, buffer, length);
            return 1;
        }
        catch (IOException)
        {
            return 0;
        }
        catch (ObjectDisposedException)
        {
            return 0;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static PdfDocumentException MapOpenFailure(uint error, string? password)
    {
        if (PdfiumNative.IsPasswordError(error))
        {
            return password is null
                ? new PdfDocumentException(
                    PdfFailure.PasswordRequired,
                    "This document is password protected.")
                : new PdfDocumentException(
                    PdfFailure.InvalidPassword,
                    "The password is incorrect.");
        }

        if (PdfiumNative.IsSecurityError(error))
        {
            return new PdfDocumentException(
                PdfFailure.Unsupported,
                "This document uses an unsupported security scheme.");
        }

        if (PdfiumNative.IsFormatError(error))
        {
            return new PdfDocumentException(
                PdfFailure.Corrupt,
                "The PDF appears to be damaged.");
        }

        return new PdfDocumentException(
            PdfFailure.AccessDenied,
            "The PDF could not be opened. Check that you have permission to read it.");
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
