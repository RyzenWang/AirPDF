using AirPdf.Core.Pdfium;
using Xunit;

namespace AirPdf.Core.Tests;

/// <summary>
/// A fact that only runs where a font covering Chinese can be embedded. Text annotations draw
/// their own appearance with such a font; on a machine without one they fall back to PDFium's
/// Latin-only generated appearance, which cannot draw Chinese at all, so there is nothing to
/// assert there.
/// </summary>
/// <remarks>
/// The check looks for a single-file font on purpose: the candidate list also holds collection
/// fonts (".ttc") because PDFium may accept them, but this attribute cannot know whether it does,
/// and a skipped test is better than a red one for a platform this feature cannot reach yet.
/// </remarks>
public sealed class CjkFontFactAttribute : FactAttribute
{
    public CjkFontFactAttribute()
    {
        var available = PdfiumDocument.TextFontCandidates(cjkOnly: true)
            .Any(path =>
                (path.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase) ||
                 path.EndsWith(".otf", StringComparison.OrdinalIgnoreCase)) &&
                File.Exists(path));

        if (!available)
        {
            Skip = "No font covering Chinese is installed on this machine.";
        }
    }
}
