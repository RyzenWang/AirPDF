using System.Drawing;
using AirPdf.Core.Models;
using PDFtoImage;
using PDFtoImage.Exceptions;
using SkiaSharp;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Outline;

namespace AirPdf.Core.Services;

/// <summary>
/// Opens PDF documents and renders pages with PDFium (through PDFtoImage).
/// PDFium is not thread-safe, so all native rendering is serialized process-wide.
/// </summary>
public sealed class PdfDocumentService
{
    private static readonly SemaphoreSlim RenderGate = new(1, 1);

    public Task<PdfDocumentInfo> OpenAsync(
        string filePath,
        string? password = null,
        CancellationToken cancellationToken = default)
    {
        var fullPath = NormalizePath(filePath);
        return Task.Run(() => OpenCore(fullPath, password, cancellationToken), cancellationToken);
    }

    public async Task<SKBitmap> RenderPageAsync(
        string filePath,
        int pageNumber,
        int targetWidth,
        string? password = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pageNumber, 1);
        var fullPath = NormalizePath(filePath);

        await RenderGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();

                var width = Math.Clamp(targetWidth, 160, 2600);
                var options = new RenderOptions
                {
                    Width = width,
                    WithAnnotations = true,
                    WithFormFill = false,
                    WithAspectRatio = true,
                    BackgroundColor = SKColors.White,
                    AntiAliasing = PdfAntiAliasing.All,
                    UseTiling = false
                };

                try
                {
                    using var stream = new FileStream(
                        fullPath,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete);

                    return Conversion.ToImage(
                        stream,
                        pageNumber - 1,
                        leaveOpen: false,
                        password,
                        options);
                }
                catch (PdfPasswordProtectedException ex)
                {
                    throw new PdfDocumentException(
                        password is null ? PdfFailure.PasswordRequired : PdfFailure.InvalidPassword,
                        "This document is password protected.",
                        ex);
                }
                catch (PdfInvalidFormatException ex)
                {
                    throw new PdfDocumentException(PdfFailure.Corrupt, "The PDF appears to be damaged.", ex);
                }
                catch (PdfUnsupportedSecuritySchemeException ex)
                {
                    throw new PdfDocumentException(
                        PdfFailure.Unsupported,
                        "This document uses an unsupported security scheme.",
                        ex);
                }
                catch (PdfCannotOpenFileException ex)
                {
                    throw new PdfDocumentException(
                        PdfFailure.NotFound,
                        "The PDF could not be opened. It may have moved or be in use.",
                        ex);
                }
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            RenderGate.Release();
        }
    }

    private static PdfDocumentInfo OpenCore(
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
        IList<SizeF> pageSizes;

        try
        {
            using var stream = new FileStream(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);

            pageCount = Conversion.GetPageCount(stream, leaveOpen: true, password);
            cancellationToken.ThrowIfCancellationRequested();

            stream.Position = 0;
            pageSizes = Conversion.GetPageSizes(stream, leaveOpen: true, password);
        }
        catch (PdfPasswordProtectedException ex)
        {
            throw new PdfDocumentException(
                password is null ? PdfFailure.PasswordRequired : PdfFailure.InvalidPassword,
                password is null
                    ? "This document is password protected."
                    : "The password is incorrect.",
                ex);
        }
        catch (PdfUnsupportedSecuritySchemeException ex)
        {
            throw new PdfDocumentException(
                PdfFailure.Unsupported,
                "This document uses an unsupported security scheme.",
                ex);
        }
        catch (PdfInvalidFormatException ex)
        {
            throw new PdfDocumentException(PdfFailure.Corrupt, "The PDF appears to be damaged.", ex);
        }
        catch (PdfCannotOpenFileException ex)
        {
            throw new PdfDocumentException(
                PdfFailure.AccessDenied,
                "The PDF could not be opened. Check that you have permission to read it.",
                ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new PdfDocumentException(
                PdfFailure.AccessDenied,
                "You do not have permission to read this file.",
                ex);
        }
        catch (IOException ex)
        {
            throw new PdfDocumentException(
                PdfFailure.Unknown,
                "The PDF could not be read. It may be in use by another app.",
                ex);
        }

        if (pageCount <= 0 || pageSizes.Count == 0)
        {
            throw new PdfDocumentException(PdfFailure.Corrupt, "The PDF contains no readable pages.");
        }

        // PdfPig gives us metadata, rotation, and the outline. Rendering still uses PDFium
        // so the preview always matches what a browser or printer would show.
        var rotations = new int[pageCount];
        PdfMetadata metadata;
        IReadOnlyList<PdfBookmark> bookmarks;
        double version = 1.7;
        TryReadPdfPigDetails(fullPath, password, pageCount, rotations, out metadata, out bookmarks, out version);

        var pages = new List<PdfPageInfo>(pageCount);
        for (var i = 0; i < pageCount; i++)
        {
            var size = i < pageSizes.Count ? pageSizes[i] : pageSizes[^1];
            pages.Add(new PdfPageInfo(i + 1, size.Width, size.Height, rotations[i]));
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
        int pageCount,
        int[] rotations,
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

            foreach (var page in document.GetPages())
            {
                if (page.Number >= 1 && page.Number <= pageCount)
                {
                    rotations[page.Number - 1] = NormalizeRotation(page.Rotation.Value);
                }
            }

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

    private static int NormalizeRotation(int rotation)
    {
        var value = rotation % 360;
        return value < 0 ? value + 360 : value;
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
