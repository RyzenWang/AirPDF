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
    /// A private annotation key holding <c>"r,g,b"</c>.
    /// </summary>
    /// <remarks>
    /// PDFium generates an appearance stream the first time a page carrying one of our
    /// annotations is rendered, and removes <c>/C</c> and <c>/IC</c> while doing it. After that
    /// <c>FPDFAnnot_GetColor</c> finds nothing, so a markup removed with the eraser was restored
    /// black. The colour is therefore written both to the standard slots, which the first render
    /// consumes, and to this key, which survives. Annotations made by other applications keep
    /// their appearance and their <c>/C</c>, so they still read through the standard slots.
    /// </remarks>
    private const string ColourKey = "AirPdfColor";

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

    /// <summary>Draws freehand strokes as one ink annotation.</summary>
    /// <remarks>
    /// PDFium stores ink as a list of paths in page space, which is exactly what the pointer
    /// produced, so nothing has to be flattened or approximated.
    /// </remarks>
    public void AddInk(int pageNumber, IReadOnlyList<PdfInkStroke> strokes, byte red, byte green, byte blue, float width)
    {
        ThrowIfDisposed();

        var usable = strokes.Where(stroke => stroke.Points.Count >= 2).ToList();
        if (usable.Count == 0)
        {
            return;
        }

        var page = LoadPageOrThrow(pageNumber);
        var annot = IntPtr.Zero;
        try
        {
            annot = PdfiumNative.PageCreateAnnot(page, PdfiumNative.AnnotInk);
            if (annot == IntPtr.Zero)
            {
                throw new PdfDocumentException(PdfFailure.Unsupported, "This page does not accept annotations.");
            }

            foreach (var stroke in usable)
            {
                var points = stroke.Points
                    .Select(point => new PdfiumNative.FsPointF { X = (float)point.X, Y = (float)point.Y })
                    .ToArray();

                PdfiumNative.AnnotAddInkStroke(annot, points, (UIntPtr)(uint)points.Length);
            }

            var bounds = InkBounds(usable);
            var rect = new PdfiumNative.FsRectF
            {
                Left = (float)bounds.Left,
                Top = (float)bounds.Top,
                Right = (float)bounds.Right,
                Bottom = (float)bounds.Bottom
            };

            PdfiumNative.AnnotSetRect(annot, ref rect);

            // Both colour slots: readers disagree about which one an ink annotation uses, and a
            // stroke that comes back black is worse than one written twice.
            PdfiumNative.AnnotSetColor(annot, PdfiumNative.AnnotColorStroke, red, green, blue, 255);
            PdfiumNative.AnnotSetColor(annot, PdfiumNative.AnnotColorFill, red, green, blue, 255);
            PdfiumNative.AnnotSetStringValue(annot, ColourKey, FormatColour(red, green, blue));
            PdfiumNative.AnnotSetBorder(annot, width, width, width);
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

    /// <summary>Reads an ink annotation back, so a removal can be undone.</summary>
    public PdfInk? TryReadInk(int pageNumber, int index)
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
                if (PdfiumNative.AnnotGetSubtype(annot) != PdfiumNative.AnnotInk)
                {
                    return null;
                }

                var count = (int)PdfiumNative.AnnotGetInkListCount(annot).ToUInt32();
                var strokes = new List<PdfInkStroke>(count);
                for (var path = 0; path < count; path++)
                {
                    var pointsNeeded = (int)PdfiumNative.AnnotGetInkListPath(annot, (UIntPtr)(uint)path, null, 0);
                    if (pointsNeeded <= 0)
                    {
                        continue;
                    }

                    var buffer = new PdfiumNative.FsPointF[pointsNeeded];
                    var written = (int)PdfiumNative.AnnotGetInkListPath(annot, (UIntPtr)(uint)path, buffer, (uint)pointsNeeded);
                    if (written <= 0)
                    {
                        continue;
                    }

                    var points = new List<PdfPoint>(written);
                    for (var point = 0; point < written && point < buffer.Length; point++)
                    {
                        points.Add(new PdfPoint(buffer[point].X, buffer[point].Y));
                    }

                    strokes.Add(new PdfInkStroke(points));
                }

                var (red, green, blue) = ReadColour(annot);

                var width = 1.5f;
                if (PdfiumNative.AnnotGetBorder(annot, out _, out _, out var borderWidth) != 0 && borderWidth > 0)
                {
                    width = borderWidth;
                }

                return new PdfInk(pageNumber, strokes, red, green, blue, width);
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

    /// <summary>Adds a sticky note carrying <paramref name="text"/> at a page-space point.</summary>
    public void AddNote(int pageNumber, PdfPoint position, string text)
    {
        ThrowIfDisposed();

        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var page = LoadPageOrThrow(pageNumber);
        var annot = IntPtr.Zero;
        try
        {
            annot = PdfiumNative.PageCreateAnnot(page, PdfiumNative.AnnotText);
            if (annot == IntPtr.Zero)
            {
                throw new PdfDocumentException(PdfFailure.Unsupported, "This page does not accept annotations.");
            }

            // The note icon is a fixed size; the position is its lower-left corner.
            const double size = 20;
            var rect = new PdfiumNative.FsRectF
            {
                Left = (float)position.X,
                Top = (float)(position.Y + size),
                Right = (float)(position.X + size),
                Bottom = (float)position.Y
            };

            PdfiumNative.AnnotSetRect(annot, ref rect);
            PdfiumNative.AnnotSetColor(annot, PdfiumNative.AnnotColorStroke, 255, 214, 79, 255);
            PdfiumNative.AnnotSetStringValue(annot, "Contents", text);
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

    /// <summary>Reads a sticky note back: position and text.</summary>
    public PdfNote? TryReadNote(int pageNumber, int index)
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
                if (PdfiumNative.AnnotGetSubtype(annot) != PdfiumNative.AnnotText)
                {
                    return null;
                }

                if (PdfiumNative.AnnotGetRect(annot, out var rect) == 0)
                {
                    return null;
                }

                var bytes = (int)PdfiumNative.AnnotGetStringValue(annot, "Contents", null, 0);
                var text = string.Empty;
                if (bytes > 0)
                {
                    var buffer = new byte[bytes];
                    var written = (int)PdfiumNative.AnnotGetStringValue(annot, "Contents", buffer, (uint)bytes);
                    if (written > 0)
                    {
                        text = System.Text.Encoding.Unicode.GetString(buffer, 0, Math.Min(written, buffer.Length)).TrimEnd('\0');
                    }
                }

                return new PdfNote(pageNumber, new PdfPoint(rect.Left, rect.Bottom), text);
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
    /// Adds a free-text annotation. The bounds are in unrotated page space, the default
    /// appearance is Helvetica at <paramref name="fontSize"/> points, and the glyph colour is
    /// written both into that appearance and into the font-colour slot.
    /// </summary>
    public void AddFreeText(
        int pageNumber,
        PdfQuad bounds,
        string text,
        float fontSize,
        byte red = 0,
        byte green = 0,
        byte blue = 0)
    {
        ThrowIfDisposed();

        if (string.IsNullOrWhiteSpace(text) || bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        var page = LoadPageOrThrow(pageNumber);
        var annot = IntPtr.Zero;
        try
        {
            annot = PdfiumNative.PageCreateAnnot(page, PdfiumNative.AnnotFreeText);
            if (annot == IntPtr.Zero)
            {
                throw new PdfDocumentException(PdfFailure.Unsupported, "This page does not accept annotations.");
            }

            // A sub-pixel rectangle on purpose: the annotation carries metadata only, and its
            // generated appearance is exactly the empty frame that non-Latin text leaves behind.
            var rect = new PdfiumNative.FsRectF
            {
                Left = 0f,
                Bottom = 0f,
                Right = 0.01f,
                Top = 0.01f
            };

            PdfiumNative.AnnotSetRect(annot, ref rect);
            PdfiumNative.AnnotSetStringValue(annot, "Contents", text);
            WriteFreeTextAppearance(annot, fontSize, red, green, blue);

            // The annotation is metadata only. PDFium generates a FreeText appearance with a
            // standard Latin font, which draws an empty frame for Chinese, and the frame cannot be
            // switched off; the visible words are page text objects added below instead.
            PdfiumNative.AnnotSetStringValue(annot, BoundsKey, FormatBounds(bounds));
            PdfiumNative.AnnotSetStringValue(annot, ColourKey, FormatColour(red, green, blue));
            PdfiumNative.AnnotSetBorder(annot, 0, 0, 0);
            AddTextObjects(page, bounds, text, fontSize, red, green, blue);
            PdfiumNative.AnnotSetFlags(annot, PdfiumNative.AnnotFlagHidden);
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
    /// Rewrites a free-text annotation in place: its rectangle, text, size, and colour. The
    /// annotation is found by the bounds and text the caller last read, the same match removal
    /// uses, so an edit can never land on a different annotation.
    /// </summary>
    public bool UpdateFreeText(int pageNumber, PdfFreeText existing, PdfFreeText updated)
    {
        ThrowIfDisposed();

        if (string.IsNullOrWhiteSpace(updated.Text) ||
            updated.Bounds.Width <= 0 ||
            updated.Bounds.Height <= 0)
        {
            return false;
        }

        var page = LoadPageOrThrow(pageNumber);
        try
        {
            for (var index = PdfiumNative.PageGetAnnotCount(page) - 1; index >= 0; index--)
            {
                if (TryReadFreeText(pageNumber, index) is not { } current ||
                    !SameFreeText(current, existing))
                {
                    continue;
                }

                var annot = PdfiumNative.PageGetAnnot(page, index);
                if (annot == IntPtr.Zero)
                {
                    return false;
                }

                try
                {
                    var rect = new PdfiumNative.FsRectF
                    {
                        Left = 0f,
                        Bottom = 0f,
                        Right = 0.01f,
                        Top = 0.01f
                    };

                    PdfiumNative.AnnotSetRect(annot, ref rect);
                    PdfiumNative.AnnotSetStringValue(annot, "Contents", updated.Text);
                    WriteFreeTextAppearance(
                        annot,
                        updated.FontSize,
                        updated.Red,
                        updated.Green,
                        updated.Blue);
                    PdfiumNative.AnnotSetStringValue(
                        annot,
                        BoundsKey,
                        FormatBounds(updated.Bounds));
                    PdfiumNative.AnnotSetStringValue(
                        annot,
                        ColourKey,
                        FormatColour(updated.Red, updated.Green, updated.Blue));
                    PdfiumNative.AnnotSetFlags(annot, PdfiumNative.AnnotFlagHidden);

                    // Replace the visible words: the old objects are matched against what the
                    // caller last read, then the new text is laid out in the new box.
                    RemoveTextObjects(page, current.Bounds, current.Text);
                    AddTextObjects(
                        page,
                        updated.Bounds,
                        updated.Text,
                        updated.FontSize,
                        updated.Red,
                        updated.Green,
                        updated.Blue);
                    return true;
                }
                finally
                {
                    PdfiumNative.PageCloseAnnot(annot);
                }
            }

            return false;
        }
        finally
        {
            PdfiumNative.ClosePage(page);
        }
    }

    /// <summary>
    /// Writes the /DA default appearance: a standard font, the size, and the glyph colour as
    /// 0..1 channels. Only the PDF standard 14 fonts are reachable this way, which is why the
    /// UI offers no font or weight picker.
    /// </summary>
    private static void WriteFreeTextAppearance(
        IntPtr annot,
        float fontSize,
        byte red,
        byte green,
        byte blue)
    {
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        var size = fontSize.ToString("0.##", culture);
        var r = (red / 255d).ToString("0.###", culture);
        var g = (green / 255d).ToString("0.###", culture);
        var b = (blue / 255d).ToString("0.###", culture);

        PdfiumNative.AnnotSetStringValue(
            annot,
            "DA",
            string.Create(culture, $"/Helvetica {size} Tf {r} {g} {b} rg"));
        PdfiumNative.AnnotSetStringValue(annot, FontSizeKey, size);
    }

    /// <summary>Two free-text annotations are the same one when text and bounds agree.</summary>
    private static bool SameFreeText(PdfFreeText left, PdfFreeText right) =>
        string.Equals(left.Text, right.Text, StringComparison.Ordinal) &&
        Math.Abs(left.Bounds.Left - right.Bounds.Left) <= 0.5 &&
        Math.Abs(left.Bounds.Top - right.Bounds.Top) <= 0.5 &&
        Math.Abs(left.Bounds.Right - right.Bounds.Right) <= 0.5 &&
        Math.Abs(left.Bounds.Bottom - right.Bounds.Bottom) <= 0.5;

    // ---- visible text ----------------------------------------------------------------------
    // PDFium generates a FreeText appearance with the standard Latin fonts only, so Chinese text
    // comes out as an empty frame. The annotation therefore only carries the metadata (hidden, so
    // its own appearance is never drawn) and the words are written as page text objects with an
    // embedded font, which renders any script and leaves no frame behind.

    private IntPtr _textFont = IntPtr.Zero;
    private IntPtr _textFontData = IntPtr.Zero;
    private int _textFontDataSize;

    /// <summary>
    /// Loads a font that covers CJK, once per document. SimHei ships with Windows; the Latin
    /// fallbacks only matter on a machine without it.
    /// </summary>
    private IntPtr EnsureTextFont()
    {
        if (_textFont != IntPtr.Zero)
        {
            return _textFont;
        }

        if (!OperatingSystem.IsWindows())
        {
            return IntPtr.Zero;
        }

        foreach (var file in new[] { "simhei.ttf", "Deng.ttf", "msyh.ttc", "arial.ttf" })
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Fonts),
                file);
            if (!File.Exists(path))
            {
                continue;
            }

            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(path);
            }
            catch (IOException)
            {
                continue;
            }

            var data = Marshal.AllocHGlobal(bytes.Length);
            Marshal.Copy(bytes, 0, data, bytes.Length);

            // PDFium keeps the buffer for the life of the font, so it is only released by
            // ReleaseTextFont when the document goes away.
            var font = PdfiumNative.TextLoadFont(
                _handle,
                data,
                (uint)bytes.Length,
                PdfiumNative.FontTrueType,
                cid: 1);

            if (font == IntPtr.Zero)
            {
                Marshal.FreeHGlobal(data);
                continue;
            }

            _textFont = font;
            _textFontData = data;
            _textFontDataSize = bytes.Length;
            return font;
        }

        return IntPtr.Zero;
    }

    private void ReleaseTextFont()
    {
        _textFont = IntPtr.Zero;

        if (_textFontData != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_textFontData);
            _textFontData = IntPtr.Zero;
            _textFontDataSize = 0;
        }
    }

    /// <summary>Writes the lines of <paramref name="text"/> as page text objects inside the box.</summary>
    private void AddTextObjects(IntPtr page, PdfQuad bounds, string text, float fontSize, byte red, byte green, byte blue)
    {
        var font = EnsureTextFont();
        if (font == IntPtr.Zero)
        {
            return;
        }

        var lineHeight = fontSize * 1.25;
        var lines = SplitLines(text);
        var baseline = bounds.Top - (fontSize * 0.8);

        for (var line = 0; line < lines.Count; line++)
        {
            var content = lines[line];
            if (content.Length == 0)
            {
                continue;
            }

            var pageObject = PdfiumNative.PageObjCreateTextObj(_handle, font, fontSize);
            if (pageObject == IntPtr.Zero)
            {
                continue;
            }

            if (PdfiumNative.TextSetText(pageObject, content) == 0)
            {
                PdfiumNative.PageObjDestroy(pageObject);
                continue;
            }

            PdfiumNative.PageObjSetFillColor(pageObject, red, green, blue, 255);
            PdfiumNative.PageObjTransform(
                pageObject,
                1,
                0,
                0,
                1,
                bounds.Left,
                baseline - (line * lineHeight));

            // The page owns the object from here on.
            PdfiumNative.PageInsertObject(page, pageObject);
        }

        PdfiumNative.PageGenerateContent(page);
    }

    /// <summary>Removes the page text objects that made up the given annotation.</summary>
    private static void RemoveTextObjects(IntPtr page, PdfQuad bounds, string text)
    {
        var lines = SplitLines(text);
        var removed = false;

        for (var index = PdfiumNative.PageCountObjects(page) - 1; index >= 0; index--)
        {
            var pageObject = PdfiumNative.PageGetObject(page, index);
            if (pageObject == IntPtr.Zero ||
                PdfiumNative.PageObjGetType(pageObject) != PdfiumNative.PageObjText ||
                !ObjectBelongsTo(pageObject, bounds, lines))
            {
                continue;
            }

            if (PdfiumNative.PageRemoveObject(page, pageObject) != 0)
            {
                PdfiumNative.PageObjDestroy(pageObject);
                removed = true;
            }
        }

        if (removed)
        {
            PdfiumNative.PageGenerateContent(page);
        }
    }

    /// <summary>
    /// True when the object's words are one of the annotation's lines and its box sits inside the
    /// annotation rectangle. Bounds are compared with slack because PDFium recomputes them from
    /// the content stream, which rounds.
    /// </summary>
    private static bool ObjectBelongsTo(IntPtr pageObject, PdfQuad bounds, IReadOnlyList<string> lines)
    {
        var objectText = ReadObjectText(pageObject);
        if (objectText.Length == 0 || !lines.Contains(objectText, StringComparer.Ordinal))
        {
            return false;
        }

        if (PdfiumNative.PageObjGetBounds(pageObject, out var left, out var bottom, out var right, out var top) == 0)
        {
            return true;
        }

        return left >= bounds.Left - 4 &&
               right <= bounds.Right + 4 &&
               bottom >= bounds.Bottom - 6 &&
               top <= bounds.Top + 6;
    }

    private static string ReadObjectText(IntPtr pageObject)
    {
        var bytes = (int)PdfiumNative.TextObjGetText(pageObject, IntPtr.Zero, IntPtr.Zero, 0);
        if (bytes <= 2)
        {
            return string.Empty;
        }

        var buffer = Marshal.AllocHGlobal(bytes);
        try
        {
            var written = (int)PdfiumNative.TextObjGetText(pageObject, IntPtr.Zero, buffer, (uint)bytes);
            if (written <= 2)
            {
                return string.Empty;
            }

            return Marshal.PtrToStringUni(buffer, (written / 2) - 1) ?? string.Empty;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static IReadOnlyList<string> SplitLines(string text) =>
        text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

    /// <summary>Reads a free-text annotation back, so it can be hit-tested and undone.</summary>
    public PdfFreeText? TryReadFreeText(int pageNumber, int index)
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
                if (PdfiumNative.AnnotGetSubtype(annot) != PdfiumNative.AnnotFreeText ||
                    PdfiumNative.AnnotGetRect(annot, out var rect) == 0)
                {
                    return null;
                }

                var text = ReadStringValue(annot, "Contents");
                if (string.IsNullOrWhiteSpace(text))
                {
                    return null;
                }

                var fontSize = 12f;
                var storedSize = ReadStringValue(annot, FontSizeKey);
                if (float.TryParse(
                        storedSize,
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out var parsedSize) &&
                    parsedSize > 0)
                {
                    fontSize = parsedSize;
                }

                // The reader's box lives in a key rather than in /Rect, which is kept sub-pixel so
                // the generated appearance cannot paint a frame. Annotations made by an older
                // build (or another application) have no key and fall back to /Rect.
                var bounds = TryReadBounds(ReadStringValue(annot, BoundsKey), out var stored)
                    ? stored
                    : new PdfQuad(
                        Math.Min(rect.Left, rect.Right),
                        Math.Max(rect.Top, rect.Bottom),
                        Math.Max(rect.Left, rect.Right),
                        Math.Min(rect.Top, rect.Bottom));

                // The glyph colour lives in /DA, which PDFium does not expose as a property, so
                // it is read back from the key this application writes. A free text made by
                // another application falls back to black.
                var (red, green, blue) = TryReadStoredColour(annot, out var storedRed, out var storedGreen, out var storedBlue)
                    ? (storedRed, storedGreen, storedBlue)
                    : ((byte)0, (byte)0, (byte)0);

                return new PdfFreeText(pageNumber, bounds, text, fontSize, red, green, blue);
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

    private static string ReadStringValue(IntPtr annot, string key)
    {
        var bytes = (int)PdfiumNative.AnnotGetStringValue(annot, key, null, 0);
        if (bytes <= 0)
        {
            return string.Empty;
        }

        var buffer = new byte[bytes];
        var written = (int)PdfiumNative.AnnotGetStringValue(annot, key, buffer, (uint)bytes);
        return written <= 0
            ? string.Empty
            : System.Text.Encoding.Unicode.GetString(buffer, 0, Math.Min(written, buffer.Length)).TrimEnd('\0');
    }

    private static string ReadContents(IntPtr annot) => ReadStringValue(annot, "Contents");

    private const string FontSizeKey = "AirPdfFontSize";

    /// <summary>
    /// The real rectangle of a text annotation, as <c>l,t,r,b</c> in page space.
    /// </summary>
    /// <remarks>
    /// The visible words are page text objects, so the annotation itself must not draw: PDFium
    /// still paints its generated appearance even when the annotation is flagged hidden, and
    /// that appearance is the empty frame around Chinese text. Its own rectangle is therefore
    /// sub-pixel and the box the reader sees is kept here instead.
    /// </remarks>
    private const string BoundsKey = "AirPdfBounds";
    /// <summary>
    /// Removes every markup annotation under a page-space point - highlight, ink, note or
    /// free text - and returns what was removed so it can be put back.
    /// </summary>
    public IReadOnlyList<PdfMarkup> RemoveMarkupAt(int pageNumber, double x, double y)
    {
        ThrowIfDisposed();

        var removed = new List<PdfMarkup>();
        var page = LoadPageOrThrow(pageNumber);
        try
        {
            // Descending: removing an annotation shifts the indices after it.
            for (var index = PdfiumNative.PageGetAnnotCount(page) - 1; index >= 0; index--)
            {
                var subtype = GetAnnotationSubtype(pageNumber, index);
                PdfMarkup? markup = subtype switch
                {
                    PdfiumNative.AnnotHighlight when TryReadHighlight(pageNumber, index) is { } highlight &&
                                                     highlight.Quads.Any(quad => Contains(quad, x, y)) =>
                        new PdfMarkup(PdfMarkupKind.Highlight, Highlight: highlight),
                    PdfiumNative.AnnotUnderline or PdfiumNative.AnnotStrikeOut or PdfiumNative.AnnotSquiggly
                        when TryReadTextMarkup(pageNumber, index) is { } textMarkup &&
                             textMarkup.Quads.Any(quad => Contains(quad, x, y)) =>
                        new PdfMarkup(MarkupKindFor(textMarkup.Kind), TextMarkup: textMarkup),
                    PdfiumNative.AnnotInk when TryReadInk(pageNumber, index) is { } ink &&
                                               Contains(InkBounds(ink.Strokes), x, y) =>
                        new PdfMarkup(PdfMarkupKind.Ink, Ink: ink),
                    PdfiumNative.AnnotText when TryReadNote(pageNumber, index) is { } note &&
                                                ContainsNote(note, x, y) =>
                        new PdfMarkup(PdfMarkupKind.Note, Note: note),
                    PdfiumNative.AnnotFreeText when TryReadFreeText(pageNumber, index) is { } freeText &&
                                                   Contains(freeText.Bounds, x, y) =>
                        new PdfMarkup(PdfMarkupKind.FreeText, FreeText: freeText),
                    _ => null
                };

                if (markup is null)
                {
                    continue;
                }

                if (PdfiumNative.PageRemoveAnnot(page, index) != 0)
                {
                    removed.Add(markup);
                }
            }
        }
        finally
        {
            PdfiumNative.ClosePage(page);
        }

        return removed;
    }

    private static bool Contains(PdfQuad quad, double x, double y) =>
        x >= quad.Left - 0.5 && x <= quad.Right + 0.5 && y >= quad.Bottom - 0.5 && y <= quad.Top + 0.5;

    private static bool ContainsNote(PdfNote note, double x, double y) =>
        x >= note.Position.X - 0.5 && x <= note.Position.X + 22 &&
        y >= note.Position.Y - 0.5 && y <= note.Position.Y + 22;
    /// <summary>Every markup annotation on a page, in one pass.</summary>
    /// <summary>
    /// Reads every page's markup in one resident-session pass. The sidebar used to call
    /// <see cref="GetMarkup"/> once per page, paying a worker-thread handoff and annotation
    /// gate acquisition for every page; one pass makes opening a long document's markup list
    /// substantially cheaper.
    /// </summary>
    public IReadOnlyList<PdfMarkup> GetAllMarkup(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        var markup = new List<PdfMarkup>();
        for (var pageNumber = 1; pageNumber <= PageCount; pageNumber++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            markup.AddRange(GetMarkup(pageNumber));
        }

        return markup;
    }

    public IReadOnlyList<PdfMarkup> GetMarkup(int pageNumber)
    {
        ThrowIfDisposed();

        var markup = new List<PdfMarkup>();
        var page = LoadPageOrThrow(pageNumber);
        try
        {
            for (var index = 0; index < PdfiumNative.PageGetAnnotCount(page); index++)
            {
                switch (GetAnnotationSubtype(pageNumber, index))
                {
                    case PdfiumNative.AnnotHighlight when TryReadHighlight(pageNumber, index) is { } highlight:
                        markup.Add(new PdfMarkup(PdfMarkupKind.Highlight, Highlight: highlight));
                        break;
                    case PdfiumNative.AnnotUnderline or PdfiumNative.AnnotStrikeOut or PdfiumNative.AnnotSquiggly
                        when TryReadTextMarkup(pageNumber, index) is { } textMarkup:
                        markup.Add(new PdfMarkup(MarkupKindFor(textMarkup.Kind), TextMarkup: textMarkup));
                        break;
                    case PdfiumNative.AnnotInk when TryReadInk(pageNumber, index) is { } ink:
                        markup.Add(new PdfMarkup(PdfMarkupKind.Ink, Ink: ink));
                        break;
                    case PdfiumNative.AnnotText when TryReadNote(pageNumber, index) is { } note:
                        markup.Add(new PdfMarkup(PdfMarkupKind.Note, Note: note));
                        break;
                    case PdfiumNative.AnnotFreeText when TryReadFreeText(pageNumber, index) is { } freeText:
                        markup.Add(new PdfMarkup(PdfMarkupKind.FreeText, FreeText: freeText));
                        break;
                }
            }
        }
        finally
        {
            PdfiumNative.ClosePage(page);
        }

        return markup;
    }
    /// <summary>Removes the ink annotation whose strokes match, so an addition can be taken back.</summary>
    public bool RemoveInkMatching(int pageNumber, PdfInk ink)
    {
        ThrowIfDisposed();

        var page = LoadPageOrThrow(pageNumber);
        try
        {
            for (var index = PdfiumNative.PageGetAnnotCount(page) - 1; index >= 0; index--)
            {
                var existing = TryReadInk(pageNumber, index);
                if (existing is null || !SameStrokes(existing.Strokes, ink.Strokes))
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

    /// <summary>Removes the free-text annotation whose bounds and text match.</summary>
    public bool RemoveFreeTextMatching(int pageNumber, PdfFreeText freeText)
    {
        ThrowIfDisposed();

        var page = LoadPageOrThrow(pageNumber);
        try
        {
            for (var index = PdfiumNative.PageGetAnnotCount(page) - 1; index >= 0; index--)
            {
                var existing = TryReadFreeText(pageNumber, index);
                if (existing is null || !SameFreeText(existing, freeText))
                {
                    continue;
                }

                if (PdfiumNative.PageRemoveAnnot(page, index) == 0)
                {
                    return false;
                }

                RemoveTextObjects(page, existing.Bounds, existing.Text);
                return true;
            }

            return false;
        }
        finally
        {
            PdfiumNative.ClosePage(page);
        }
    }

    /// <summary>Removes the note whose rectangle and text match.</summary>
    public bool RemoveNoteMatching(int pageNumber, PdfNote note)
    {
        ThrowIfDisposed();

        var page = LoadPageOrThrow(pageNumber);
        try
        {
            for (var index = PdfiumNative.PageGetAnnotCount(page) - 1; index >= 0; index--)
            {
                var existing = TryReadNote(pageNumber, index);
                if (existing is null ||
                    !string.Equals(existing.Text, note.Text, StringComparison.Ordinal) ||
                    Math.Abs(existing.Position.X - note.Position.X) > 0.5 ||
                    Math.Abs(existing.Position.Y - note.Position.Y) > 0.5)
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

    /// <summary>
    /// Reads an annotation's colour. The private key is checked first because PDFium removes
    /// the standard colour entries when it generates an appearance for an annotation we made;
    /// annotations from elsewhere still answer through <c>/C</c> or <c>/IC</c>.
    /// </summary>
    private static (byte Red, byte Green, byte Blue) ReadColour(IntPtr annot)
    {
        if (TryReadStoredColour(annot, out var storedRed, out var storedGreen, out var storedBlue))
        {
            return (storedRed, storedGreen, storedBlue);
        }

        foreach (var slot in new[] { PdfiumNative.AnnotColorStroke, PdfiumNative.AnnotColorFill })
        {
            if (PdfiumNative.AnnotGetColor(annot, slot, out var r, out var g, out var b, out _) == 0)
            {
                continue;
            }

            if (r == 0 && g == 0 && b == 0 && slot == PdfiumNative.AnnotColorStroke)
            {
                continue;
            }

            return ((byte)Math.Min(r, 255), (byte)Math.Min(g, 255), (byte)Math.Min(b, 255));
        }

        return (0, 0, 0);
    }

    /// <summary>Reads the colour this application recorded when it created the annotation.</summary>
    private static bool TryReadStoredColour(IntPtr annot, out byte red, out byte green, out byte blue)
    {
        red = 0;
        green = 0;
        blue = 0;

        var bytes = (int)PdfiumNative.AnnotGetStringValue(annot, ColourKey, null, 0);
        if (bytes <= 0)
        {
            return false;
        }

        var buffer = new byte[bytes];
        var written = (int)PdfiumNative.AnnotGetStringValue(annot, ColourKey, buffer, (uint)bytes);
        if (written <= 0)
        {
            return false;
        }

        var text = System.Text.Encoding.Unicode
            .GetString(buffer, 0, Math.Min(written, buffer.Length))
            .TrimEnd('\0');

        var parts = text.Split(',');
        if (parts.Length != 3 ||
            !byte.TryParse(parts[0], out red) ||
            !byte.TryParse(parts[1], out green) ||
            !byte.TryParse(parts[2], out blue))
        {
            red = 0;
            green = 0;
            blue = 0;
            return false;
        }

        return true;
    }

    private static string FormatColour(byte red, byte green, byte blue) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{red},{green},{blue}");

    private static string FormatBounds(PdfQuad bounds) =>
        string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{bounds.Left:0.###},{bounds.Top:0.###},{bounds.Right:0.###},{bounds.Bottom:0.###}");

    private static bool TryReadBounds(string text, out PdfQuad bounds)
    {
        bounds = default;
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        var parts = text.Split(',');
        if (parts.Length != 4)
        {
            return false;
        }

        var culture = System.Globalization.CultureInfo.InvariantCulture;
        const System.Globalization.NumberStyles style = System.Globalization.NumberStyles.Float;

        if (!double.TryParse(parts[0], style, culture, out var left) ||
            !double.TryParse(parts[1], style, culture, out var top) ||
            !double.TryParse(parts[2], style, culture, out var right) ||
            !double.TryParse(parts[3], style, culture, out var bottom))
        {
            return false;
        }

        bounds = new PdfQuad(left, top, right, bottom);
        return bounds.Width > 0 && bounds.Height > 0;
    }
    private static bool SameStrokes(IReadOnlyList<PdfInkStroke> left, IReadOnlyList<PdfInkStroke> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (var stroke = 0; stroke < left.Count; stroke++)
        {
            if (left[stroke].Points.Count != right[stroke].Points.Count)
            {
                return false;
            }

            for (var point = 0; point < left[stroke].Points.Count; point++)
            {
                if (Math.Abs(left[stroke].Points[point].X - right[stroke].Points[point].X) > 0.01 ||
                    Math.Abs(left[stroke].Points[point].Y - right[stroke].Points[point].Y) > 0.01)
                {
                    return false;
                }
            }
        }

        return true;
    }
    private static PdfQuad InkBounds(IReadOnlyList<PdfInkStroke> strokes)
    {
        double left = double.MaxValue, right = double.MinValue, top = double.MinValue, bottom = double.MaxValue;

        foreach (var point in strokes.SelectMany(stroke => stroke.Points))
        {
            left = Math.Min(left, point.X);
            right = Math.Max(right, point.X);
            top = Math.Max(top, point.Y);
            bottom = Math.Min(bottom, point.Y);
        }

        // Give a flat stroke a little height, or the rectangle is degenerate.
        if (top - bottom < 1) { top += 1; bottom -= 1; }
        if (right - left < 1) { right += 1; left -= 1; }

        return new PdfQuad(left, top, right, bottom);
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

                var (red, green, blue) = ReadColour(annot);
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

    /// <summary>Reads an underline, strikeout, or squiggly annotation back out.</summary>
    public PdfTextMarkup? TryReadTextMarkup(int pageNumber, int index)
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
                var kind = KindForSubtype(PdfiumNative.AnnotGetSubtype(annot));
                if (kind is null)
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

                if (quads.Count == 0 &&
                    PdfiumNative.AnnotGetRect(annot, out var rect) != 0)
                {
                    quads.Add(new PdfQuad(rect.Left, rect.Top, rect.Right, rect.Bottom));
                }

                var (red, green, blue) = ReadColour(annot);
                return new PdfTextMarkup(
                    pageNumber,
                    kind.Value,
                    quads,
                    red,
                    green,
                    blue);
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

    /// <summary>Removes the text markup matching kind and geometry, for undo.</summary>
    public bool RemoveTextMarkupMatching(int pageNumber, PdfTextMarkup markup)
    {
        ThrowIfDisposed();

        var page = LoadPageOrThrow(pageNumber);
        try
        {
            for (var index = PdfiumNative.PageGetAnnotCount(page) - 1; index >= 0; index--)
            {
                var existing = TryReadTextMarkup(pageNumber, index);
                if (existing is null ||
                    existing.Kind != markup.Kind ||
                    !SameGeometry(existing.Quads, markup.Quads))
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
    public void AddHighlight(int pageNumber, IReadOnlyList<PdfQuad> quads, byte red, byte green, byte blue) =>
        AddAttachmentMarkup(
            pageNumber,
            PdfiumNative.AnnotHighlight,
            quads,
            red,
            green,
            blue);

    /// <summary>Adds an underline, strikeout, or squiggly annotation over the selected quads.</summary>
    public void AddTextMarkup(
        int pageNumber,
        PdfTextMarkupKind kind,
        IReadOnlyList<PdfQuad> quads,
        byte red,
        byte green,
        byte blue) =>
        AddAttachmentMarkup(pageNumber, SubtypeFor(kind), quads, red, green, blue);

    private void AddAttachmentMarkup(
        int pageNumber,
        int subtype,
        IReadOnlyList<PdfQuad> quads,
        byte red,
        byte green,
        byte blue)
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
            annot = PdfiumNative.PageCreateAnnot(page, subtype);
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
            PdfiumNative.AnnotSetStringValue(annot, ColourKey, FormatColour(red, green, blue));
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

    private static int SubtypeFor(PdfTextMarkupKind kind) => kind switch
    {
        PdfTextMarkupKind.Underline => PdfiumNative.AnnotUnderline,
        PdfTextMarkupKind.StrikeOut => PdfiumNative.AnnotStrikeOut,
        _ => PdfiumNative.AnnotSquiggly
    };

    private static PdfTextMarkupKind? KindForSubtype(int subtype) => subtype switch
    {
        PdfiumNative.AnnotUnderline => PdfTextMarkupKind.Underline,
        PdfiumNative.AnnotStrikeOut => PdfTextMarkupKind.StrikeOut,
        PdfiumNative.AnnotSquiggly => PdfTextMarkupKind.Squiggly,
        _ => null
    };

    private static PdfMarkupKind MarkupKindFor(PdfTextMarkupKind kind) => kind switch
    {
        PdfTextMarkupKind.Underline => PdfMarkupKind.Underline,
        PdfTextMarkupKind.StrikeOut => PdfMarkupKind.StrikeOut,
        _ => PdfMarkupKind.Squiggly
    };

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
