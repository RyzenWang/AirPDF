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
    private string? _sessionPassword;
    private string? _sessionRecoveryPath;
    private bool _disposed;

    /// <summary>
    /// When an in-place save cannot replace the original file, the complete saved copy is
    /// kept here (and opened as the resident session) so the edits are not lost. Null when
    /// the last save succeeded or no save has failed.
    /// </summary>
    public string? RecoveryPath { get; private set; }

    public Task<PdfDocumentInfo> OpenAsync(
        string filePath,
        string? password = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

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

    /// <summary>
    /// Returns the link annotations of one page. Links are read from the resident document
    /// when it matches <paramref name="filePath"/>; otherwise an empty list is returned
    /// rather than reopening the file.
    /// </summary>
    public Task<IReadOnlyList<PdfLink>> GetLinksAsync(
        string filePath,
        int pageNumber,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pageNumber, 1);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var fullPath = NormalizePath(filePath);

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
                        return session.GetLinks(pageNumber);
                    }

                    return (IReadOnlyList<PdfLink>)[];
                }
                finally
                {
                    RenderGate.Release();
                }
            },
            cancellationToken);
    }

    /// <summary>
    /// Adds a highlight to the resident document.
    /// </summary>
    /// <remarks>
    /// Unlike rendering, this cannot fall back to a throwaway handle: a change applied to a copy
    /// that is then discarded would look like it worked and silently do nothing.
    /// </remarks>
    public Task AddHighlightAsync(
        string filePath,
        int pageNumber,
        IReadOnlyList<PdfQuad> quads,
        byte red = 255,
        byte green = 221,
        byte blue = 71,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pageNumber, 1);
        ArgumentNullException.ThrowIfNull(quads);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var fullPath = NormalizePath(filePath);

        return Task.Run(
            async () =>
            {
                await RenderGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ResidentSession(fullPath).AddHighlight(pageNumber, quads, red, green, blue);
                }
                finally
                {
                    RenderGate.Release();
                }
            },
            cancellationToken);
    }

    /// <summary>Annotation count of one page in the resident document.</summary>
    public Task<int> GetAnnotationCountAsync(
        string filePath,
        int pageNumber,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pageNumber, 1);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var fullPath = NormalizePath(filePath);

        return Task.Run(
            async () =>
            {
                await RenderGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return ResidentSession(fullPath).GetAnnotationCount(pageNumber);
                }
                finally
                {
                    RenderGate.Release();
                }
            },
            cancellationToken);
    }

    /// <summary>
    /// Rotation of one page, read on demand.
    /// </summary>
    /// <remarks>
    /// Deliberately not part of <see cref="PdfDocumentInfo"/>: reading rotation for every page
    /// cost 45 s of a 55 s open on a 9,277-page document. Callers that need it ask for the pages
    /// they are touching, which is at most a handful.
    /// </remarks>
    public Task<int> GetPageRotationAsync(
        string filePath,
        int pageNumber,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pageNumber, 1);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var fullPath = NormalizePath(filePath);

        return Task.Run(
            async () =>
            {
                await RenderGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return ResidentSession(fullPath).GetPageRotation(pageNumber);
                }
                finally
                {
                    RenderGate.Release();
                }
            },
            cancellationToken);
    }
    /// <summary>Subtype of one annotation, 9 for a highlight, for verification.</summary>
    public Task<int> GetAnnotationSubtypeAsync(
        string filePath,
        int pageNumber,
        int index,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pageNumber, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var fullPath = NormalizePath(filePath);

        return Task.Run(
            async () =>
            {
                await RenderGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return ResidentSession(fullPath).GetAnnotationSubtype(pageNumber, index);
                }
                finally
                {
                    RenderGate.Release();
                }
            },
            cancellationToken);
    }
    /// <summary>
    /// Removes the highlight whose geometry matches, which is how an added highlight is taken
    /// back without relying on an annotation index that later edits may have shifted.
    /// </summary>
    public Task<bool> RemoveHighlightMatchingAsync(
        string filePath,
        int pageNumber,
        IReadOnlyList<PdfQuad> quads,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pageNumber, 1);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var fullPath = NormalizePath(filePath);

        return Task.Run(
            async () =>
            {
                await RenderGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return ResidentSession(fullPath).RemoveHighlightMatching(pageNumber, quads);
                }
                finally
                {
                    RenderGate.Release();
                }
            },
            cancellationToken);
    }
    /// <summary>
    /// Writes the resident document back over the file it was opened from.
    /// </summary>
    /// <remarks>
    /// The file cannot simply be overwritten: PDFium reads through an open stream, and the
    /// replacement needs the handle closed first. The order is therefore: write a temporary file
    /// beside the original, close the session, move the temporary over the original, then reopen
    /// it. If the reopen fails the service is left with no session, which degrades to transient
    /// handles for rendering and refuses edits, rather than crashing.
    /// </remarks>
    public Task SaveInPlaceAsync(string filePath, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var fullPath = NormalizePath(filePath);

        return Task.Run(
            async () =>
            {
                await RenderGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var session = ResidentSession(fullPath);
                    var password = _sessionPassword;
                    var temporary = fullPath + ".airpdf-save";
                    var previousRecovery = _sessionRecoveryPath ?? RecoveryPath;
                    var keepTemporary = false;

                    // Check that the target can be written *before* giving up the session: a save
                    // that is going to fail anyway must not leave the document unusable.
                    AssertWritable(fullPath);

                    try
                    {
                        session.SaveCopy(temporary);

                        _session = null;
                        _sessionPath = null;
                        _sessionPassword = null;
                        _sessionRecoveryPath = null;
                        session.Dispose();

                        File.Move(temporary, fullPath, overwrite: true);

                        _session = PdfiumDocument.Open(fullPath, password);
                        _sessionPath = fullPath;
                        _sessionPassword = password;
                        RecoveryPath = null;
                        TryDeleteFile(previousRecovery);
                    }
                    catch
                    {
                        // The replacement failed. The temporary file is the only copy that has
                        // the reader's edits, so it must never be deleted in this path. Opening
                        // it as the session keeps those edits visible and retryable.
                        if (_session is null && File.Exists(temporary))
                        {
                            keepTemporary = true;
                            var recovery = fullPath + ".airpdf-recovered";

                            try
                            {
                                File.Move(temporary, recovery, overwrite: true);
                                keepTemporary = false;
                                RecoveryPath = recovery;

                                try
                                {
                                    _session = PdfiumDocument.Open(recovery, password);
                                    _sessionPath = fullPath;
                                    _sessionPassword = password;
                                    _sessionRecoveryPath = recovery;
                                }
                                catch
                                {
                                    // The recovery copy remains on disk; fall back below.
                                }
                            }
                            catch
                            {
                                // Keep the temporary file where it is rather than losing it.
                                RecoveryPath = temporary;
                            }
                        }

                        if (_session is null && File.Exists(fullPath))
                        {
                            try
                            {
                                _session = PdfiumDocument.Open(fullPath, password);
                                _sessionPath = fullPath;
                                _sessionPassword = password;
                            }
                            catch
                            {
                                // Left with no session: rendering degrades to transient handles.
                            }
                        }

                        throw;
                    }
                    finally
                    {
                        if (!keepTemporary)
                        {
                            TryDeleteFile(temporary);
                        }
                    }
                }
                finally
                {
                    RenderGate.Release();
                }
            },
            cancellationToken);
    }

    private static void TryDeleteFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // A leftover recovery file must not mask the real result.
        }
    }

    /// <summary>Every highlight on a page, read from the resident document.</summary>
    public Task<IReadOnlyList<PdfHighlight>> GetHighlightsAsync(
        string filePath,
        int pageNumber,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pageNumber, 1);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var fullPath = NormalizePath(filePath);

        return Task.Run(
            async () =>
            {
                await RenderGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var session = ResidentSession(fullPath);
                    var highlights = new List<PdfHighlight>();
                    for (var index = 0; index < session.GetAnnotationCount(pageNumber); index++)
                    {
                        if (session.TryReadHighlight(pageNumber, index) is { } highlight)
                        {
                            highlights.Add(highlight);
                        }
                    }

                    return (IReadOnlyList<PdfHighlight>)highlights;
                }
                finally
                {
                    RenderGate.Release();
                }
            },
            cancellationToken);
    }
    /// <summary>Draws ink strokes into the resident document.</summary>
    public Task AddInkAsync(
        string filePath,
        int pageNumber,
        IReadOnlyList<PdfInkStroke> strokes,
        byte red = 32,
        byte green = 33,
        byte blue = 36,
        float width = 1.5f,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pageNumber, 1);
        ArgumentNullException.ThrowIfNull(strokes);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var fullPath = NormalizePath(filePath);

        return Task.Run(
            async () =>
            {
                await RenderGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ResidentSession(fullPath).AddInk(pageNumber, strokes, red, green, blue, width);
                }
                finally
                {
                    RenderGate.Release();
                }
            },
            cancellationToken);
    }

    /// <summary>Every markup annotation on a page, for hit-testing the pointer against.</summary>
    public Task<IReadOnlyList<PdfMarkup>> GetMarkupAsync(
        string filePath,
        int pageNumber,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pageNumber, 1);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var fullPath = NormalizePath(filePath);

        return Task.Run(
            async () =>
            {
                await RenderGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return ResidentSession(fullPath).GetMarkup(pageNumber);
                }
                finally
                {
                    RenderGate.Release();
                }
            },
            cancellationToken);
    }
    /// <summary>Removes whatever markup is under a page-space point.</summary>
    public Task<IReadOnlyList<PdfMarkup>> RemoveMarkupAtAsync(
        string filePath,
        int pageNumber,
        double x,
        double y,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pageNumber, 1);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var fullPath = NormalizePath(filePath);

        return Task.Run(
            async () =>
            {
                await RenderGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return ResidentSession(fullPath).RemoveMarkupAt(pageNumber, x, y);
                }
                finally
                {
                    RenderGate.Release();
                }
            },
            cancellationToken);
    }
    /// <summary>Removes the ink annotation matching the snapshot, for undo.</summary>
    public Task<bool> RemoveInkMatchingAsync(string filePath, PdfInk ink, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var fullPath = NormalizePath(filePath);
        return Task.Run(
            async () =>
            {
                await RenderGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return ResidentSession(fullPath).RemoveInkMatching(ink.PageNumber, ink);
                }
                finally
                {
                    RenderGate.Release();
                }
            },
            cancellationToken);
    }

    /// <summary>Removes the sticky note matching the snapshot, for undo.</summary>
    public Task<bool> RemoveNoteMatchingAsync(string filePath, PdfNote note, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var fullPath = NormalizePath(filePath);
        return Task.Run(
            async () =>
            {
                await RenderGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return ResidentSession(fullPath).RemoveNoteMatching(note.PageNumber, note);
                }
                finally
                {
                    RenderGate.Release();
                }
            },
            cancellationToken);
    }
    /// <summary>Every ink annotation on a page.</summary>
    public Task<IReadOnlyList<PdfInk>> GetInksAsync(
        string filePath,
        int pageNumber,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pageNumber, 1);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var fullPath = NormalizePath(filePath);

        return Task.Run(
            async () =>
            {
                await RenderGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var session = ResidentSession(fullPath);
                    var inks = new List<PdfInk>();
                    for (var index = 0; index < session.GetAnnotationCount(pageNumber); index++)
                    {
                        if (session.TryReadInk(pageNumber, index) is { } ink)
                        {
                            inks.Add(ink);
                        }
                    }

                    return (IReadOnlyList<PdfInk>)inks;
                }
                finally
                {
                    RenderGate.Release();
                }
            },
            cancellationToken);
    }

    /// <summary>Adds a sticky note with text at a page-space point.</summary>
    public Task AddNoteAsync(
        string filePath,
        int pageNumber,
        PdfPoint position,
        string text,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pageNumber, 1);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var fullPath = NormalizePath(filePath);

        return Task.Run(
            async () =>
            {
                await RenderGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ResidentSession(fullPath).AddNote(pageNumber, position, text ?? string.Empty);
                }
                finally
                {
                    RenderGate.Release();
                }
            },
            cancellationToken);
    }

    /// <summary>Every sticky note on a page.</summary>
    public Task<IReadOnlyList<PdfNote>> GetNotesAsync(
        string filePath,
        int pageNumber,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pageNumber, 1);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var fullPath = NormalizePath(filePath);

        return Task.Run(
            async () =>
            {
                await RenderGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var session = ResidentSession(fullPath);
                    var notes = new List<PdfNote>();
                    for (var index = 0; index < session.GetAnnotationCount(pageNumber); index++)
                    {
                        if (session.TryReadNote(pageNumber, index) is { } note)
                        {
                            notes.Add(note);
                        }
                    }

                    return (IReadOnlyList<PdfNote>)notes;
                }
                finally
                {
                    RenderGate.Release();
                }
            },
            cancellationToken);
    }
    /// <summary>Removes the highlights under a page-space point.</summary>
    public Task<IReadOnlyList<PdfHighlight>> RemoveHighlightsAtAsync(
        string filePath,
        int pageNumber,
        PdfQuad point,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pageNumber, 1);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var fullPath = NormalizePath(filePath);

        return Task.Run(
            async () =>
            {
                await RenderGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return ResidentSession(fullPath).RemoveHighlightsAt(pageNumber, point);
                }
                finally
                {
                    RenderGate.Release();
                }
            },
            cancellationToken);
    }
    /// <summary>
    /// Writes the resident document, annotations included, to <paramref name="destination"/>.
    /// The file that was opened is never written to.
    /// </summary>
    public Task SaveCopyAsync(
        string filePath,
        string destination,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var fullPath = NormalizePath(filePath);

        return Task.Run(
            async () =>
            {
                await RenderGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ResidentSession(fullPath).SaveCopy(destination);
                }
                finally
                {
                    RenderGate.Release();
                }
            },
            cancellationToken);
    }

    /// <summary>
    /// Fails early when the file cannot be written, so a doomed save does not first throw away
    /// the open document. Opening for write does not modify the file; it only asks the sharing
    /// rules of whoever else has it open.
    /// </summary>
    private static void AssertWritable(string fullPath)
    {
        try
        {
            if (File.GetAttributes(fullPath).HasFlag(FileAttributes.ReadOnly))
            {
                throw new PdfDocumentException(PdfFailure.AccessDenied, "The file is read-only.");
            }

            using var probe = new FileStream(fullPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
        }
        catch (PdfDocumentException)
        {
            throw;
        }
        catch (UnauthorizedAccessException)
        {
            throw new PdfDocumentException(PdfFailure.AccessDenied, "The file cannot be written.");
        }
        catch (IOException)
        {
            throw new PdfDocumentException(PdfFailure.AccessDenied, "The file is in use by another program.");
        }
    }
    private PdfiumDocument ResidentSession(string fullPath)
    {
        var session = _session;
        if (session is null || !string.Equals(_sessionPath, fullPath, PathComparison))
        {
            throw new PdfDocumentException(
                PdfFailure.Unknown,
                "The document is no longer open, so it cannot be edited.");
        }

        return session;
    }
    /// <summary>Page count of the open document, or 0 when nothing is open.</summary>
    public int OpenPageCount => _session?.PageCount ?? 0;

    /// <summary>Reads one page's text layer through the resident document handle.</summary>
    public Task<PdfTextPage> GetTextPageAsync(int pageNumber, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pageNumber, 1);
        ObjectDisposedException.ThrowIf(_disposed, this);

        return Task.Run(
            async () =>
            {
                await RenderGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var session = _session;
                    if (session is null || pageNumber > session.PageCount)
                    {
                        return new PdfTextPage { PageNumber = pageNumber, Words = [] };
                    }

                    return session.GetTextPage(pageNumber);
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
            ReleaseSession();
        }
        finally
        {
            RenderGate.Release();
        }
    }

    /// <summary>
    /// Releases the resident handle without blocking the caller's thread. UI close paths use
    /// this so a slow PDFium call in another window cannot freeze the window being closed.
    /// </summary>
    public Task CloseDocumentAsync(CancellationToken cancellationToken = default)
    {
        return Task.Run(
            async () =>
            {
                await RenderGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    ReleaseSession();
                }
                finally
                {
                    RenderGate.Release();
                }
            },
            cancellationToken);
    }

    private void ReleaseSession()
    {
        var session = _session;
        var recovery = _sessionRecoveryPath;
        _session = null;
        _sessionPath = null;
        _sessionPassword = null;
        _sessionRecoveryPath = null;
        RecoveryPath = null;
        session?.Dispose();
        TryDeleteFile(recovery);
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
        var gateHeld = true;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            var session = PdfiumDocument.Open(fullPath, password);

            // A dispose can land while the document is being parsed. Do not install a session
            // the service will never close again.
            if (_disposed)
            {
                session.Dispose();
                throw new ObjectDisposedException(nameof(PdfDocumentService));
            }

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
                    // A several-thousand-page open must not hold the process-wide PDFium gate
                    // for its whole duration; give queued renders a chance between chunks.
                    if (page > 1 && (page - 1) % 256 == 0)
                    {
                        RenderGate.Release();
                        gateHeld = false;
                        RenderGate.Wait(cancellationToken);
                        gateHeld = true;
                    }

                    pageSizes.Add(session.GetPageSize(page));
                }
            }
            catch
            {
                session.Dispose();
                throw;
            }

            var previous = _session;
            var previousRecovery = _sessionRecoveryPath;
            _session = session;
            _sessionPath = fullPath;
            _sessionPassword = password;
            _sessionRecoveryPath = null;
            RecoveryPath = null;
            previous?.Dispose();
            TryDeleteFile(previousRecovery);
        }
        finally
        {
            if (gateHeld)
            {
                RenderGate.Release();
            }
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
