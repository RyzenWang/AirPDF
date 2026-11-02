using System.Runtime.InteropServices;

namespace AirPdf.Core.Pdfium;

/// <summary>
/// One character from PDFium's text layer. Boxes are in the page's own coordinate space
/// (origin bottom-left, Y up, before <c>/Rotate</c>), which is what makes rotated pages
/// tractable: order is already correct, only the geometry has to be mapped.
/// </summary>
public readonly record struct PdfTextChar(
    string Text,
    double Left,
    double Bottom,
    double Right,
    double Top,
    double FontSize)
{
    public double Width => Right - Left;

    public double Height => Top - Bottom;

    public bool IsWhitespace => Text.Length == 1 && char.IsWhiteSpace(Text[0]);

    /// <summary>Han, Hiragana, Katakana, and Hangul are written without spaces, so each
    /// character is its own selectable unit rather than being glued into a long word.</summary>
    public bool IsCjk => Text.Length > 0 && IsCjkChar(Text[0]);

    /// <summary>True for Han, Kana, Hangul, and fullwidth forms.</summary>
    public static bool IsCjkChar(char value) =>
        value is >= '\u2E80' and <= '\u9FFF' or >= '\uAC00' and <= '\uD7AF' or >= '\uF900' and <= '\uFAFF'
            or >= '\u3040' and <= '\u30FF' or >= '\uFF00' and <= '\uFFEF';
}

/// <summary>Minimal <c>fpdf_text.h</c> bindings.</summary>
internal static class PdfiumText
{
    [DllImport(PdfiumNative.Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "FPDFText_LoadPage")]
    internal static extern IntPtr LoadPage(IntPtr page);

    [DllImport(PdfiumNative.Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "FPDFText_ClosePage")]
    internal static extern void ClosePage(IntPtr textPage);

    [DllImport(PdfiumNative.Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "FPDFText_CountChars")]
    internal static extern int CountChars(IntPtr textPage);

    [DllImport(PdfiumNative.Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "FPDFText_GetUnicode")]
    internal static extern uint GetUnicode(IntPtr textPage, int index);

    [DllImport(PdfiumNative.Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "FPDFText_GetCharBox")]
    internal static extern int GetCharBox(
        IntPtr textPage,
        int index,
        out double left,
        out double right,
        out double bottom,
        out double top);

    [DllImport(PdfiumNative.Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "FPDFText_GetFontSize")]
    internal static extern double GetFontSize(IntPtr textPage, int index);
}
