using System.Runtime.InteropServices;

namespace AirPdf.Core.Pdfium;

/// <summary>
/// The annotation and save surface of PDFium. Split out of <c>PdfiumNative</c> because it is
/// the only part that mutates a document and writes files, which is worth reading on its own.
/// </summary>
internal static partial class PdfiumNative
{
    /// <summary>Highlight annotation, as numbered by the PDFium public API.</summary>
    internal const int AnnotHighlight = 9;

    /// <summary>
    /// Colour slots on an annotation. A highlight keeps its colour in <c>/C</c>, which is the
    /// stroke slot; the fill slot is set as well so viewers that prefer <c>/IC</c> agree.
    /// </summary>
    internal const int AnnotColorStroke = 0;

    internal const int AnnotColorFill = 1;

    /// <summary>Print flag, so a highlight survives printing like any other markup.</summary>
    internal const int AnnotFlagPrint = 4;

    /// <summary>Write a complete new file instead of appending to the original.</summary>
    internal const uint SaveNoIncremental = 2;

    [StructLayout(LayoutKind.Sequential)]
    internal struct FsRectF
    {
        public float Left;
        public float Top;
        public float Right;
        public float Bottom;
    }

    /// <summary>One quad: upper-left, upper-right, lower-left, lower-right, in page space.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct FsQuadPointsF
    {
        public float X1;
        public float Y1;
        public float X2;
        public float Y2;
        public float X3;
        public float Y3;
        public float X4;
        public float Y4;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct FpdfFileWrite
    {
        public int Version;
        public IntPtr WriteBlock;
    }

    /// <summary>
    /// PDFium's writer structure plus the managed stream behind it. <c>Base</c> must stay the
    /// first field: PDFium hands the same pointer back to the callback, and the stream handle is
    /// read from the offset just past the structure.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct FileWriteContext
    {
        public FpdfFileWrite Base;
        public IntPtr Stream;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int FileWriteDelegate(IntPtr context, IntPtr data, uint size);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "FPDFPage_CreateAnnot")]
    internal static extern IntPtr PageCreateAnnot(IntPtr page, int subtype);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "FPDFPage_GetAnnotCount")]
    internal static extern int PageGetAnnotCount(IntPtr page);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "FPDFPage_GetAnnot")]
    internal static extern IntPtr PageGetAnnot(IntPtr page, int index);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "FPDFPage_CloseAnnot")]
    internal static extern int PageCloseAnnot(IntPtr annot);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "FPDFAnnot_GetColor")]
    internal static extern int AnnotGetColor(
        IntPtr annot,
        int colorType,
        out uint red,
        out uint green,
        out uint blue,
        out uint alpha);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "FPDFAnnot_CountAttachmentPoints")]
    internal static extern UIntPtr AnnotCountAttachmentPoints(IntPtr annot);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "FPDFAnnot_GetAttachmentPoints")]
    internal static extern int AnnotGetAttachmentPoints(IntPtr annot, UIntPtr quadIndex, out FsQuadPointsF quadPoints);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "FPDFAnnot_GetRect")]
    internal static extern int AnnotGetRect(IntPtr annot, out FsRectF rect);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "FPDFPage_RemoveAnnot")]
    internal static extern int PageRemoveAnnot(IntPtr page, int index);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "FPDFAnnot_GetSubtype")]
    internal static extern int AnnotGetSubtype(IntPtr annot);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "FPDFAnnot_SetRect")]
    internal static extern int AnnotSetRect(IntPtr annot, ref FsRectF rect);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "FPDFAnnot_SetColor")]
    internal static extern int AnnotSetColor(IntPtr annot, int colorType, uint red, uint green, uint blue, uint alpha);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "FPDFAnnot_SetFlags")]
    internal static extern int AnnotSetFlags(IntPtr annot, int flags);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "FPDFAnnot_AppendAttachmentPoints")]
    internal static extern int AnnotAppendAttachmentPoints(IntPtr annot, ref FsQuadPointsF quadPoints);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "FPDF_SaveAsCopy")]
    internal static extern int SaveAsCopy(IntPtr document, ref FpdfFileWrite writer, uint flags);
}
