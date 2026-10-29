using System.Runtime.InteropServices;

namespace AirPdf.Core.Pdfium;

/// <summary>
/// Minimal PDFium bindings. PDFtoImage's own wrapper is <c>internal</c>, so keeping a
/// document handle open — which is what removes the per-page document reparse — means
/// talking to PDFium directly.
/// </summary>
/// <remarks>
/// The native library is the one PDFtoImage already ships for every target:
/// <c>pdfium.dll</c> (win-x86/x64/arm64), <c>libpdfium.so</c> (linux x86/x64/arm/arm64,
/// musl x86/x64/arm64) and <c>libpdfium.dylib</c> (osx-x64/arm64). The P/Invoke
/// declarations below are plain C ABI with no platform-specific code.
/// </remarks>
internal static class PdfiumNative
{
    private const string Library = "pdfium";

    /// <summary><c>FPDF_ANNOT</c>: render annotations.</summary>
    internal const int RenderAnnot = 0x01;

    /// <summary>
    /// Flags used for every page render. Anti-aliasing for text, images and paths stays on
    /// (none of the <c>RENDER_NO_SMOOTH*</c> bits are set), matching PDFtoImage's
    /// <c>PdfAntiAliasing.All</c>. <c>FPDF_LCD_TEXT</c> is deliberately not set: sub-pixel
    /// text is not what the reader has shipped so far, and it is device dependent.
    /// </summary>
    internal const int RenderFlags = RenderAnnot;

    /// <summary><c>FPDFBitmap_BGRA</c>, matching <c>SKColorType.Bgra8888</c>.</summary>
    internal const int BitmapBgra = 4;

    private const uint ErrorSuccess = 0;
    private const uint ErrorFile = 2;
    private const uint ErrorFormat = 3;
    private const uint ErrorPassword = 4;
    private const uint ErrorSecurity = 5;

    private static int _initialized;

    /// <summary>
    /// Initializes the PDFium library once per process. PDFium does not reference-count
    /// this call and <c>FPDF_DestroyLibrary</c> tears down every open document, so nothing
    /// in this app may call destroy while a handle is alive.
    /// </summary>
    internal static void EnsureLibrary()
    {
        if (Interlocked.Exchange(ref _initialized, 1) == 0)
        {
            InitLibrary();
        }
    }

    internal static uint LastError => GetLastError();

    internal static bool IsPasswordError(uint error) => error == ErrorPassword;

    internal static bool IsFormatError(uint error) => error is ErrorFormat or ErrorFile;

    internal static bool IsSecurityError(uint error) => error == ErrorSecurity;

    internal static bool IsSuccess(uint error) => error == ErrorSuccess;

    /// <summary>
    /// <c>unsigned long</c> is 32-bit on Windows and 64-bit on Unix, so it must be
    /// marshalled as <see cref="CULong"/>; using <c>uint</c> would corrupt the struct
    /// layout on Linux and macOS.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct FileAccess
    {
        public CULong FileLength;

        /// <summary>Function pointer produced from <see cref="GetBlockCallback"/>.</summary>
        public IntPtr GetBlock;

        public IntPtr Param;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Size
    {
        public float Width;
        public float Height;
    }

    /// <summary>
    /// <c>FS_RECTF</c>: PDFium reports annotation bounds as left/top/right/bottom floats in
    /// page space. Which pair is numerically larger depends on the coordinate system, so
    /// callers min/max them rather than assume an order.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct RectF
    {
        public float Left;
        public float Top;
        public float Right;
        public float Bottom;
    }

    /// <summary>
    /// <c>int (*m_GetBlock)(void* param, unsigned long position, unsigned char* pBuf,
    /// unsigned long size)</c>. Returns 1 when the whole range was read.
    /// </summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int GetBlockCallback(IntPtr param, CULong position, IntPtr buffer, CULong size);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "FPDF_InitLibrary")]
    private static extern void InitLibrary();

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "FPDF_LoadCustomDocument")]
    internal static extern IntPtr LoadCustomDocument(ref FileAccess fileAccess, IntPtr passwordUtf8);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "FPDF_CloseDocument")]
    internal static extern void CloseDocument(IntPtr document);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "FPDF_GetLastError")]
    private static extern uint GetLastError();

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "FPDF_GetPageCount")]
    internal static extern int GetPageCount(IntPtr document);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "FPDF_GetPageSizeByIndexF")]
    internal static extern int GetPageSizeByIndexF(IntPtr document, int pageIndex, out Size size);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "FPDF_LoadPage")]
    internal static extern IntPtr LoadPage(IntPtr document, int pageIndex);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "FPDF_ClosePage")]
    internal static extern void ClosePage(IntPtr page);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "FPDFBitmap_CreateEx")]
    internal static extern IntPtr BitmapCreateEx(int width, int height, int format, IntPtr firstScan, int stride);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "FPDFBitmap_Destroy")]
    internal static extern void BitmapDestroy(IntPtr bitmap);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "FPDFBitmap_FillRect")]
    internal static extern void BitmapFillRect(IntPtr bitmap, int left, int top, int width, int height, uint color);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "FPDF_RenderPageBitmap")]
    internal static extern void RenderPageBitmap(
        IntPtr bitmap,
        IntPtr page,
        int startX,
        int startY,
        int sizeX,
        int sizeY,
        int rotate,
        int flags);

    // ---- links -----------------------------------------------------------------

    /// <summary><c>PDFACTION_GOTO</c>: jump to a destination inside this document.</summary>
    internal const uint ActionGoTo = 1;

    /// <summary><c>PDFACTION_URI</c>: open an external URI.</summary>
    internal const uint ActionUri = 3;

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "FPDF_GetPageWidthF")]
    internal static extern float GetPageWidthF(IntPtr page);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "FPDF_GetPageHeightF")]
    internal static extern float GetPageHeightF(IntPtr page);

    /// <summary>Returns the page's <c>/Rotate</c> in quarter turns (0-3).</summary>
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "FPDFPage_GetRotation")]
    internal static extern int GetPageRotation(IntPtr page);

    /// <summary>Walks the page's links. Returns non-zero when a link was written to
    /// <paramref name="link"/> and <paramref name="startPos"/> was advanced.</summary>
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "FPDFLink_Enumerate")]
    internal static extern int LinkEnumerate(IntPtr page, ref int startPos, out IntPtr link);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "FPDFLink_GetAnnotRect")]
    internal static extern int LinkGetAnnotRect(IntPtr link, out RectF rect);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "FPDFLink_GetAction")]
    internal static extern IntPtr LinkGetAction(IntPtr link);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "FPDFLink_GetDest")]
    internal static extern IntPtr LinkGetDest(IntPtr document, IntPtr link);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "FPDFAction_GetType")]
    internal static extern CULong ActionGetType(IntPtr action);

    /// <summary>Writes the URI as a NUL-terminated byte string and returns its length in
    /// bytes, including the terminator. Pass a null buffer to measure first.</summary>
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "FPDFAction_GetURIPath")]
    internal static extern CULong ActionGetUriPath(IntPtr document, IntPtr action, IntPtr buffer, CULong length);

    /// <summary>Zero-based destination page index, or -1 when the destination is not a
    /// page destination.</summary>
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "FPDFDest_GetDestPageIndex")]
    internal static extern int DestGetPageIndex(IntPtr document, IntPtr destination);
}
