using System.Text;
using System.Text.RegularExpressions;
using AirPdf.Core.Models;
using AirPdf.Core.Pdfium;

namespace AirPdf.Core.Services;

/// <summary>
/// Full-document text search. It walks the same PDFium text layer the reader selects from, so
/// search and selection agree, and rotated pages are searchable like any other.
/// </summary>
public sealed partial class PdfSearchService
{
    private const int MaxResults = 250;

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    private readonly PdfDocumentService _documents;

    public PdfSearchService(PdfDocumentService documents) => _documents = documents;

    public async Task<IReadOnlyList<PdfSearchHit>> SearchAsync(
        string filePath,
        string query,
        string? password = null,
        CancellationToken cancellationToken = default)
    {
        var normalizedQuery = NormalizeWhitespace(query);
        if (normalizedQuery.Length == 0)
        {
            return [];
        }

        var hits = new List<PdfSearchHit>();
        var pageCount = _documents.OpenPageCount;

        for (var pageNumber = 1; pageNumber <= pageCount && hits.Count < MaxResults; pageNumber++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            PdfTextPage page;
            try
            {
                page = await _documents.GetTextPageAsync(pageNumber, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // One unreadable page must not abort the search.
                continue;
            }

            hits.AddRange(SearchPage(page, normalizedQuery, MaxResults - hits.Count));
        }

        return hits;
    }

    private static IReadOnlyList<PdfSearchHit> SearchPage(PdfTextPage page, string query, int remaining)
    {
        if (remaining <= 0 || page.Words.Count == 0)
        {
            return [];
        }

        var (text, words) = BuildPageText(page);
        if (text.Length == 0)
        {
            return [];
        }

        var hits = new List<PdfSearchHit>();
        var offset = 0;
        var matchIndex = 0;

        while (offset < text.Length && hits.Count < remaining)
        {
            var index = text.IndexOf(query, offset, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                break;
            }

            var end = index + query.Length;
            var rects = GetRects(words, index, end);

            hits.Add(new PdfSearchHit
            {
                PageNumber = page.PageNumber,
                Snippet = BuildSnippet(text, index, query.Length),
                Rects = rects,
                MatchIndexOnPage = matchIndex
            });

            matchIndex++;
            offset = Math.Max(index + 1, end);
        }

        return hits;
    }

    /// <summary>
    /// Joins the page's words into one searchable string. A space is inserted only where the
    /// document really has a word break: CJK runs get no separator, or a Chinese query would
    /// never match.
    /// </summary>
    private static (string Text, List<SearchWord> Words) BuildPageText(PdfTextPage page)
    {
        var builder = new StringBuilder();
        var words = new List<SearchWord>(page.Words.Count);

        foreach (var word in page.Words)
        {
            var text = NormalizeWhitespace(word.Text);
            if (text.Length == 0)
            {
                continue;
            }

            if (builder.Length > 0 && NeedsSpace(builder[^1], text[0]))
            {
                builder.Append(' ');
            }

            var start = builder.Length;
            builder.Append(text);
            words.Add(new SearchWord(start, builder.Length, word.Bounds));
        }

        return (builder.ToString(), words);
    }

    private static IReadOnlyList<PdfSearchRect> GetRects(IReadOnlyList<SearchWord> words, int start, int end)
    {
        var rects = new List<PdfSearchRect>();
        foreach (var word in words)
        {
            if (word.End <= start || word.Start >= end)
            {
                continue;
            }

            if (word.Bounds.Width > 0.0001 && word.Bounds.Height > 0.0001)
            {
                rects.Add(word.Bounds);
            }
        }

        return rects;
    }

    private static bool NeedsSpace(char previous, char current) =>
        !(PdfTextChar.IsCjkChar(previous) && PdfTextChar.IsCjkChar(current));

    private static string BuildSnippet(string text, int index, int length)
    {
        const int context = 54;
        var start = Math.Max(0, index - context);
        var end = Math.Min(text.Length, index + length + context);

        if (start > 0)
        {
            var nextSpace = text.IndexOf(' ', start, index - start);
            if (nextSpace >= 0)
            {
                start = nextSpace + 1;
            }
        }

        if (end < text.Length && end > index + length)
        {
            var previousSpace = text.LastIndexOf(' ', end, end - (index + length));
            if (previousSpace >= 0)
            {
                end = previousSpace;
            }
        }

        var snippet = text[start..end].Trim();
        return (start > 0 ? "\u2026" : string.Empty) + snippet + (end < text.Length ? "\u2026" : string.Empty);
    }

    private static string NormalizeWhitespace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : Whitespace().Replace(value, " ").Trim();

    private readonly record struct SearchWord(int Start, int End, PdfSearchRect Bounds);
}
