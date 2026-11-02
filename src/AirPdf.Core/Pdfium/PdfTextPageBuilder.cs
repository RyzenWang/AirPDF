using System.Text;
using AirPdf.Core.Models;
using AirPdf.Core.Pdfium;

namespace AirPdf.Core.Pdfium;

/// <summary>
/// Turns PDFium's per-character text into the word list the reader selects and searches on.
/// </summary>
/// <remarks>
/// PDFium reports characters in reading order with boxes in the page's own unrotated space,
/// which is what makes this rotation-proof: only the boxes need mapping. Characters are
/// grouped into words on whitespace, on a noticeable gap, and at CJK boundaries — CJK is
/// written without spaces, so each character stays its own selectable unit.
/// </remarks>
public static class PdfTextPageBuilder
{
    /// <summary>Gaps wider than this fraction of the font size split a word.</summary>
    private const double GapRatio = 0.3;

    public static PdfTextPage Build(
        int pageNumber,
        IReadOnlyList<PdfTextChar> chars,
        double pageWidth,
        double pageHeight,
        int rotation)
    {
        var words = new List<PdfTextWord>();
        var current = new StringBuilder();
        double left = 0, bottom = 0, right = 0, top = 0;
        PdfTextChar? previous = null;

        void Flush()
        {
            if (current.Length == 0)
            {
                return;
            }

            var bounds = PageGeometry.ToDisplayRect(left, top, right, bottom, pageWidth, pageHeight, rotation);
            if (bounds.Width > 0.0001 && bounds.Height > 0.0001)
            {
                words.Add(new PdfTextWord
                {
                    Index = words.Count,
                    Text = current.ToString(),
                    Bounds = bounds
                });
            }

            current.Clear();
        }

        foreach (var item in chars)
        {
            if (item.IsWhitespace)
            {
                Flush();
                previous = item;
                continue;
            }

            if (previous is { } last && StartsNewWord(last, item))
            {
                Flush();
            }

            if (current.Length == 0)
            {
                left = item.Left;
                bottom = item.Bottom;
                right = item.Right;
                top = item.Top;
            }
            else
            {
                left = Math.Min(left, item.Left);
                bottom = Math.Min(bottom, item.Bottom);
                right = Math.Max(right, item.Right);
                top = Math.Max(top, item.Top);
            }

            current.Append(item.Text);
            previous = item;
        }

        Flush();

        return new PdfTextPage
        {
            PageNumber = pageNumber,
            Words = words
        };
    }

    private static bool StartsNewWord(PdfTextChar previous, PdfTextChar current)
    {
        // CJK has no word spaces; treating each character as a unit keeps selection sane.
        if (previous.IsCjk || current.IsCjk)
        {
            return true;
        }

        var horizontal = Math.Max(0, Math.Max(previous.Left, current.Left) - Math.Min(previous.Right, current.Right));
        var vertical = Math.Max(0, Math.Max(previous.Bottom, current.Bottom) - Math.Min(previous.Top, current.Top));
        var gap = Math.Sqrt((horizontal * horizontal) + (vertical * vertical));

        var scale = Math.Max(previous.FontSize, current.FontSize);
        if (scale <= 0)
        {
            scale = Math.Max(Math.Max(previous.Height, current.Height), 1);
        }

        return gap > scale * GapRatio;
    }
}
