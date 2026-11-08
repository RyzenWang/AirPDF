using System.Text;
using AirPdf.Core.Models;
using AirPdf.Core.Pdfium;

namespace AirPdf.Core.Pdfium;

/// <summary>
/// Turns PDFium's per-character text into the word list the reader searches on, keeping each
/// character's box so selection can be character-precise.
/// </summary>
/// <remarks>
/// PDFium reports characters in reading order with boxes in the page's own unrotated space,
/// which is what makes this rotation-proof: only the boxes need mapping. Characters are
/// grouped into words on whitespace, on a noticeable gap, and at CJK boundaries — CJK is
/// written without spaces, so each character stays its own selectable unit. Every word keeps
/// the mapped box of each character, so the reader never has to walk the page twice to select
/// part of a word.
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
        var currentChars = new List<PdfTextCharBox>();
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
                    Bounds = bounds,
                    Chars = currentChars.ToArray()
                });
            }

            current.Clear();
            currentChars.Clear();
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

            // The reader selects characters, so the character box is mapped here even though
            // the word rectangle is only the union of the same boxes.
            currentChars.Add(new PdfTextCharBox(
                item.Text,
                PageGeometry.ToDisplayRect(
                    item.Left,
                    item.Top,
                    item.Right,
                    item.Bottom,
                    pageWidth,
                    pageHeight,
                    rotation)));

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
