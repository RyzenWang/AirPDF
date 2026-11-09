using System.Buffers;
using System.Globalization;
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
/// <remarks>
/// <b>Not thread-safe.</b> PDFium serializes nothing internally, so every call must be made
/// from one thread at a time; <see cref="Services.PdfDocumentService"/> holds the process-wide
/// gate that guarantees it. Calling this class directly from more than one thread crashes the
/// process, which is why tests go through the service.
/// </remarks>
public sealed partial class PdfiumDocument : IDisposable
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
    /// Rotated pages are handled: annotation bounds arrive in unrotated page space and are
    /// mapped with <see cref="PageGeometry"/>.
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
            // PDFium already applies /Rotate to the page box it reports, while link
            // rectangles come back in the page's own unrotated space, so the box has to be
            // turned back before the transform is applied.
            var box = ReadUnrotatedBox(page);
            if (box is not { } pageBox)
            {
                return [];
            }

            var pageWidth = pageBox.Width;
            var pageHeight = pageBox.Height;
            var rotation = pageBox.Rotation;

            var links = new List<PdfLink>();
            var position = 0;

            while (PdfiumNative.LinkEnumerate(page, ref position, out var link) != 0 && link != IntPtr.Zero)
            {
                if (PdfiumNative.LinkGetAnnotRect(link, out var rect) == 0)
                {
                    continue;
                }

                var bounds = PageGeometry.ToDisplayRect(
                    rect.Left,
                    rect.Top,
                    rect.Right,
                    rect.Bottom,
                    pageWidth,
                    pageHeight,
                    rotation);

                if (bounds.Width <= 0.0001 || bounds.Height <= 0.0001)
                {
                    continue;
                }

                if (TryReadTarget(link, out var kind, out var uri, out var targetPage))
                {
                    links.Add(new PdfLink
                    {
                        Bounds = bounds,
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

    /// <summary>
    /// The page's own box (before <c>/Rotate</c>) plus the rotation in degrees. PDFium
    /// reports the page box already rotated, so quarter turns are swapped back here; callers
    /// then map content-space rectangles with <see cref="PageGeometry"/>.
    /// </summary>
    private (double Width, double Height, int Rotation)? ReadUnrotatedBox(IntPtr page)
    {
        var quarters = ((PdfiumNative.GetPageRotation(page) % 4) + 4) % 4;

        var displayWidth = PdfiumNative.GetPageWidthF(page);
        var displayHeight = PdfiumNative.GetPageHeightF(page);
        if (displayWidth <= 0 || displayHeight <= 0)
        {
            return null;
        }

        return quarters is 1 or 3
            ? (displayHeight, displayWidth, quarters * 90)
            : (displayWidth, displayHeight, quarters * 90);
    }

    /// <summary>
    /// Reads the page's text layer through PDFium: characters in reading order with boxes in
    /// unrotated page space, grouped into words and normalized for display.
    /// </summary>
    public PdfTextPage GetTextPage(int pageNumber)
    {
        ThrowIfDisposed();

        var page = PdfiumNative.LoadPage(_handle, pageNumber - 1);
        if (page == IntPtr.Zero)
        {
            return new PdfTextPage { PageNumber = pageNumber, Words = [] };
        }

        var textPage = IntPtr.Zero;
        try
        {
            if (ReadUnrotatedBox(page) is not { } box)
            {
                return new PdfTextPage { PageNumber = pageNumber, Words = [] };
            }

            textPage = PdfiumText.LoadPage(page);
            if (textPage == IntPtr.Zero)
            {
                return new PdfTextPage { PageNumber = pageNumber, Words = [] };
            }

            var count = PdfiumText.CountChars(textPage);
            if (count <= 0)
            {
                return new PdfTextPage { PageNumber = pageNumber, Words = [] };
            }

            var chars = new List<PdfTextChar>(count);
            for (var index = 0; index < count; index++)
            {
                var code = PdfiumText.GetUnicode(textPage, index);
                if (code == 0)
                {
                    continue;
                }

                var text = ToText(code);
                if (text is null)
                {
                    continue;
                }

                if (PdfiumText.GetCharBox(textPage, index, out var left, out var right, out var bottom, out var top) == 0)
                {
                    continue;
                }

                chars.Add(new PdfTextChar(
                    text,
                    Math.Min(left, right),
                    Math.Min(bottom, top),
                    Math.Max(left, right),
                    Math.Max(bottom, top),
                    PdfiumText.GetFontSize(textPage, index)));
            }

            return PdfTextPageBuilder.Build(pageNumber, chars, box.Width, box.Height, box.Rotation);
        }
        catch
        {
            // A malformed content stream must not take a page down.
            return new PdfTextPage { PageNumber = pageNumber, Words = [] };
        }
        finally
        {
            if (textPage != IntPtr.Zero)
            {
                PdfiumText.ClosePage(textPage);
            }

            PdfiumNative.ClosePage(page);
        }
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

    /// <summary>
    /// Reads the optional metadata and outline through PDFium. The caller must hold the
    /// process-wide PDFium gate; this method does not touch pages or render anything.
    /// </summary>
    public PdfDocumentDetails GetDetails()
    {
        ThrowIfDisposed();

        return new PdfDocumentDetails
        {
            PdfVersion = ReadPdfVersion(),
            Metadata = ReadMetadata(),
            Bookmarks = ReadBookmarks()
        };
    }

    private double ReadPdfVersion()
    {
        if (PdfiumNative.GetFileVersion(_handle, out var version) != 0 && version > 0)
        {
            return version / 10.0;
        }

        return 1.7;
    }

    private PdfMetadata ReadMetadata() =>
        new()
        {
            Title = ReadMetaText("Title"),
            Author = ReadMetaText("Author"),
            Subject = ReadMetaText("Subject"),
            Keywords = ReadMetaText("Keywords"),
            Creator = ReadMetaText("Creator"),
            Producer = ReadMetaText("Producer"),
            CreatedAt = ParsePdfDate(ReadMetaText("CreationDate")),
            ModifiedAt = ParsePdfDate(ReadMetaText("ModDate"))
        };

    private string? ReadMetaText(string tag)
    {
        var tagPointer = Marshal.StringToCoTaskMemUTF8(tag);
        try
        {
            var required = PdfiumNative.GetMetaText(_handle, tagPointer, IntPtr.Zero, default);
            if (required.Value == 0 || required.Value > int.MaxValue)
            {
                return null;
            }

            var size = checked((int)required.Value);
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                var written = PdfiumNative.GetMetaText(
                    _handle,
                    tagPointer,
                    buffer,
                    new CULong((uint)size));
                if (written.Value == 0)
                {
                    return null;
                }

                var text = Marshal.PtrToStringUni(buffer, (int)(written.Value / 2));
                return string.IsNullOrWhiteSpace(text) ? null : text.TrimEnd('\0').Trim();
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(tagPointer);
        }
    }

    private IReadOnlyList<PdfBookmark> ReadBookmarks()
    {
        var count = 0;
        return ReadBookmarkLevel(IntPtr.Zero, 0, ref count);
    }

    private IReadOnlyList<PdfBookmark> ReadBookmarkLevel(IntPtr parent, int depth, ref int count)
    {
        const int maxDepth = 64;
        const int maxBookmarks = 10_000;

        if (depth > maxDepth)
        {
            return [];
        }

        var result = new List<PdfBookmark>();
        var bookmark = PdfiumNative.BookmarkGetFirstChild(_handle, parent);
        while (bookmark != IntPtr.Zero && count < maxBookmarks)
        {
            count++;
            var children = ReadBookmarkLevel(bookmark, depth + 1, ref count);
            var page = BookmarkPage(bookmark, children);
            if (page > 0)
            {
                result.Add(new PdfBookmark
                {
                    Title = ReadBookmarkTitle(bookmark) ?? $"Page {page}",
                    PageNumber = page,
                    Children = children
                });
            }

            bookmark = PdfiumNative.BookmarkGetNextSibling(_handle, bookmark);
        }

        return result;
    }

    private int BookmarkPage(IntPtr bookmark, IReadOnlyList<PdfBookmark> children)
    {
        var page = DestinationPage(PdfiumNative.BookmarkGetDest(_handle, bookmark));
        if (page <= 0)
        {
            var action = PdfiumNative.BookmarkGetAction(_handle, bookmark);
            if (action != IntPtr.Zero)
            {
                page = DestinationPage(PdfiumNative.ActionGetDest(_handle, action));
            }
        }

        if (page > 0)
        {
            return page;
        }

        foreach (var child in children)
        {
            if (child.PageNumber > 0)
            {
                return child.PageNumber;
            }
        }

        return 0;
    }

    private int DestinationPage(IntPtr destination)
    {
        if (destination == IntPtr.Zero)
        {
            return 0;
        }

        var index = PdfiumNative.DestGetPageIndex(_handle, destination);
        return index < 0 ? 0 : index + 1;
    }

    private string? ReadBookmarkTitle(IntPtr bookmark)
    {
        var required = PdfiumNative.BookmarkGetTitle(bookmark, IntPtr.Zero, default);
        if (required.Value == 0 || required.Value > int.MaxValue)
        {
            return null;
        }

        var size = checked((int)required.Value);
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            var written = PdfiumNative.BookmarkGetTitle(
                bookmark,
                buffer,
                new CULong((uint)size));
            if (written.Value == 0)
            {
                return null;
            }

            var text = Marshal.PtrToStringUni(buffer, (int)(written.Value / 2));
            return string.IsNullOrWhiteSpace(text) ? null : text.TrimEnd('\0').Trim();
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static DateTimeOffset? ParsePdfDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var text = value.Trim();
        if (text.StartsWith("D:", StringComparison.Ordinal))
        {
            text = text[2..];
        }

        if (text.Length < 4 ||
            !int.TryParse(text[..4], NumberStyles.None, CultureInfo.InvariantCulture, out var year))
        {
            return null;
        }

        var month = ReadNumber(text, 4, 2, 1);
        var day = ReadNumber(text, 6, 2, 1);
        var hour = ReadNumber(text, 8, 2, 0);
        var minute = ReadNumber(text, 10, 2, 0);
        var second = ReadNumber(text, 12, 2, 0);

        var offset = TimeSpan.Zero;
        if (text.Length > 14 && text[14] is '+' or '-')
        {
            var sign = text[14] == '-' ? -1 : 1;
            var offsetHours = ReadNumber(text, 15, 2, 0);
            var offsetMinutes = ReadNumber(text, 18, 2, 0);
            offset = TimeSpan.FromMinutes(sign * ((offsetHours * 60) + offsetMinutes));
        }

        try
        {
            var safeYear = Math.Clamp(year, 1, 9999);
            var safeMonth = Math.Clamp(month, 1, 12);
            return new DateTimeOffset(
                safeYear,
                safeMonth,
                Math.Clamp(day, 1, DateTime.DaysInMonth(safeYear, safeMonth)),
                Math.Clamp(hour, 0, 23),
                Math.Clamp(minute, 0, 59),
                Math.Clamp(second, 0, 59),
                offset);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static int ReadNumber(string text, int start, int length, int fallback)
    {
        if (start + length > text.Length ||
            !int.TryParse(
                text.AsSpan(start, length),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var value))
        {
            return fallback;
        }

        return value;
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

    private static string? ToText(uint code)
    {
        // PDFium hands back UTF-16 code units; a lone surrogate cannot be encoded alone.
        if (code is >= 0xD800 and <= 0xDFFF)
        {
            return null;
        }

        return char.ConvertFromUtf32((int)code);
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
