using System.Text;

namespace AirPdf.Core.Models;

/// <summary>
/// A text layer for one page. Word rectangles are normalized to the page
/// (0..1, origin top-left), matching the rendered bitmap.
/// </summary>
public sealed class PdfTextPage
{
    public required int PageNumber { get; init; }

    public required IReadOnlyList<PdfTextWord> Words { get; init; }

    public bool HasText => Words.Count > 0;
}

/// <summary>
/// One word: the unit the text layer is searched on, and the unit that decides where a copied
/// line breaks. The reader selects <em>characters</em>, so each word also carries the boxes of
/// the characters it was built from.
/// </summary>
public sealed class PdfTextWord
{
    public required int Index { get; init; }

    public required string Text { get; init; }

    public required PdfSearchRect Bounds { get; init; }

    /// <summary>
    /// One entry per character, in the same order as <see cref="Text"/>. Empty only for a text
    /// layer built without character boxes; selection then falls back to the whole word.
    /// </summary>
    public IReadOnlyList<PdfTextCharBox> Chars { get; init; } = [];

    public int CharCount => Chars.Count;

    /// <summary>
    /// The text of characters <paramref name="from"/>..<paramref name="to"/>, inclusive.
    /// </summary>
    public string GetText(int from, int to)
    {
        if (Chars.Count == 0)
        {
            return Text;
        }

        from = Math.Clamp(from, 0, Chars.Count - 1);
        to = Math.Clamp(to, 0, Chars.Count - 1);
        if (from > to)
        {
            (from, to) = (to, from);
        }

        if (from == 0 && to == Chars.Count - 1)
        {
            return Text;
        }

        var builder = new StringBuilder(to - from + 1);
        for (var index = from; index <= to; index++)
        {
            builder.Append(Chars[index].Text);
        }

        return builder.ToString();
    }
}

/// <summary>
/// One character of a word: its text and its box in normalized display space.
/// </summary>
/// <remarks>
/// The text is kept per character rather than as a stride into the word, because PDFium reports
/// some characters as two UTF-16 units (anything outside the basic plane). A list of boxes keeps
/// the mapping exact: character <c>i</c> is always <c>Chars[i]</c>.
/// </remarks>
public readonly record struct PdfTextCharBox(string Text, PdfSearchRect Bounds);
