namespace AirPdf.Core.Services;

/// <summary>Ordering hint for PDFium page renders. Lower values run first.</summary>
public enum PdfRenderPriority
{
    /// <summary>The page currently on screen.</summary>
    Visible = 0,

    /// <summary>Another page in the layout, including overscan neighbours.</summary>
    Page = 1,

    /// <summary>A decorative thumbnail; it must never delay a visible page.</summary>
    Thumbnail = 2
}
