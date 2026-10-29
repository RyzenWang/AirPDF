using AirPdf.Core.Models;
using AirPdf.Core.Pdfium;
using SkiaSharp;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Outline;

namespace AirPdf.Core.Services;

/// <summary>
/// Opens PDF documents and renders pages with PDFium.
/// </summary>
/// <remarks>
/// The opened document keeps a PDFium handle for as long as it is displayed, so rendering
/// a page reuses one parsed document instead of reparsing the whole file per page. PDFium
/// is not thread-safe, so every native call is serialized through one process-wide gate.
/// </remarks>
public sealed class PdfDocumentService : IDisposable
{
    private static readonly SemaphoreSlim RenderGate = new(1, 1);

    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private PdfiumDocument? _session;
    private string? _sessionPath;
    private bool _disposed;

    public Task<PdfDocumentInfo> OpenAsync(
        string filePath,
        string? password = null,
        CancellationToken cancellationToken = default)
    {
        var fullPath = NormalizePath(filePath);
        return Task.Run(() => OpenCore(fullPath, password, cancellationToken), cancellationToken);
    }

    public Task<SKBitmap> RenderPageAsync(
        string filePath,
        int pageNumber,
        int targetWidth,
        string? password = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pageNumber, 1);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var fullPath = NormalizePath(filePath);
        var width = Math.Clamp(targetWidth, 160, 2600);

        // Rendering is CPU bound, so it must not run on the caller's thread: the gate can
        // be acquired synchronously and would otherwise block the UI thread.
        return Task.Run(
            async () =>
            {
                await RenderGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var session = _session;
                    if (session is not null && string.Equals(_sessionPath, fullPath, PathComparison))
                    {
                        return session.RenderPage(pageNumber, width);
                    }

                    // No resident handle for this file — for example a render that raced a
                    // document switch. Render from a short-lived handle instead of failing.
                    using var fallback = PdfiumDocument.Open(fullPath, password);
                    return fallback.RenderPage(pageNumber, width);
                }
                finally
                {
                    RenderGate.Release();
                }
            },
            cancellationToken);
    }

    /// <summary>Releases the resident PDFium handle for the open document.</summary>
    public void CloseDocument()
    {
        RenderGate.Wait();
        try
        {
            var session = _session;
            _session = null;
            _sessionPath = null;
            session?.Dispose();
        }
        finally
        {
            RenderGate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CloseDocument();
    }

    private PdfDocumentInfo OpenCore(
        string fullPath,
        string? password,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(fullPath))
        {
            throw new PdfDocumentException(PdfFailure.NotFound, "The file does not exist.");
        }

        var fileInfo = new FileInfo(fullPath);

        int pageCount;
        List<PdfPageSize> pageSizes = [];

        RenderGate.Wait(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            var session = PdfiumDocument.Open(fullPath, password);

            // Measure the new document before swapping it in, so a failure leaves the
            // currently open document untouched.
            try
            {
                pageCount = session.PageCount;
                if (pageCount <= 0)
                {
                    throw new PdfDocumentException(PdfFailure.Corrupt, "The PDF contains no readable pages.");
                }

                pageSizes = new List<PdfPageSize>(pageCount);
                for (var page = 1; page <= pageCount; page++)
                {
                    pageSizes.Add(session.GetPageSize(page));
                }
            }
            catch
            {
                session.Dispose();
                throw;
            }

            var previous = _session;
            _session = session;
            _sessionPath = fullPath;
            previous?.Dispose();
        }
        finally
        {
            RenderGate.Release();
        }

        cancellationToken.ThrowIfCancellationRequested();

        // PdfPig gives us metadata and the outline; PDFium stays the renderer.
        //
        // Page rotation is deliberately not read here. Reading it requires parsing every
        // page dictionary, which dominated open time on long documents (measured at 45.1 s
        // of a 54.7 s open on a 9,277-page file) while nothing consumed the value. The code
        // that genuinely needs rotation reads it per page, for the pages it touches.
        TryReadPdfPigDetails(fullPath, password, out var metadata, out var bookmarks, out var version);

        var pages = new List<PdfPageInfo>(pageCount);
        for (var index = 0; index < pageCount; index++)
        {
            var size = pageSizes[index];
            pages.Add(new PdfPageInfo(index + 1, size.Width, size.Height));
        }

        return new PdfDocumentInfo
        {
            FilePath = fullPath,
            PageCount = pageCount,
            Pages = pages,
            FileSizeBytes = fileInfo.Length,
            PdfVersion = version,
            Metadata = metadata,
            Bookmarks = bookmarks
        };
    }

    private static void TryReadPdfPigDetails(
        string fullPath,
        string? password,
        out PdfMetadata metadata,
        out IReadOnlyList<PdfBookmark> bookmarks,
        out double version)
    {
        metadata = new PdfMetadata();
        bookmarks = [];
        version = 1.7;

        try
        {
            using var document = PdfDocument.Open(fullPath, new ParsingOptions
            {
                Password = password!,
                UseLenientParsing = true,
                SkipMissingFonts = true
            });

            version = document.Version;

            var info = document.Information;
            metadata = new PdfMetadata
            {
                Title = Clean(info.Title),
                Author = Clean(info.Author),
                Subject = Clean(info.Subject),
                Keywords = Clean(info.Keywords),
                Creator = Clean(info.Creator),
                Producer = Clean(info.Producer),
                CreatedAt = SafeDate(info.GetCreatedDateTimeOffset),
                ModifiedAt = SafeDate(info.GetModifiedDateTimeOffset)
            };

            if (document.TryGetBookmarks(out var outline, false))
            {
                bookmarks = MapBookmarks(outline.Roots);
            }
        }
        catch
        {
            // Metadata is optional. A file that renders correctly should still open even
            // when a producer wrote a malformed document-information dictionary.
        }
    }

    private static IReadOnlyList<PdfBookmark> MapBookmarks(IReadOnlyList<BookmarkNode> nodes)
    {
        if (nodes.Count == 0)
        {
            return [];
        }

        var result = new List<PdfBookmark>(nodes.Count);
        foreach (var node in nodes)
        {
            var children = MapBookmarks(node.Children);
            var page = FindBookmarkPage(node, children);
            if (page <= 0)
            {
                continue;
            }

            result.Add(new PdfBookmark
            {
                Title = string.IsNullOrWhiteSpace(node.Title) ? $"Page {page}" : node.Title.Trim(),
                PageNumber = page,
                Children = children
            });
        }

        return result;
    }

    private static int FindBookmarkPage(BookmarkNode node, IReadOnlyList<PdfBookmark> children)
    {
        if (node is DocumentBookmarkNode documentNode && documentNode.PageNumber > 0)
        {
            return documentNode.PageNumber;
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

    private static string? Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Trim();
    }

    private static DateTimeOffset? SafeDate(Func<DateTimeOffset?> read)
    {
        try
        {
            return read();
        }
        catch
        {
            return null;
        }
    }

    private static string NormalizePath(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new PdfDocumentException(PdfFailure.NotFound, "No file was selected.");
        }

        try
        {
            return Path.GetFullPath(filePath);
        }
        catch (Exception ex)
        {
            throw new PdfDocumentException(PdfFailure.NotFound, "The selected path is not valid.", ex);
        }
    }
}
