using System.Text;
using System.Text.RegularExpressions;
using AirPdf.Core.Models;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace AirPdf.Core.Services;

/// <summary>
/// Full-document text search backed by PdfPig. Search results include normalized
/// character boxes so the reader can paint highlights over the rendered page.
/// </summary>
public sealed class PdfSearchService
{
    private const int MaxResults = 250;

    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    public Task<IReadOnlyList<PdfSearchHit>> SearchAsync(
        string filePath,
        string query,
        string? password = null,
        CancellationToken cancellationToken = default)
    {
        var normalizedQuery = NormalizeWhitespace(query);
        if (normalizedQuery.Length == 0)
        {
            return Task.FromResult<IReadOnlyList<PdfSearchHit>>([]);
        }

        return Task.Run(
            () => SearchCore(filePath, normalizedQuery, password, cancellationToken),
            cancellationToken);
    }

    private static IReadOnlyList<PdfSearchHit> SearchCore(
        string filePath,
        string query,
        string? password,
        CancellationToken cancellationToken)
    {
        var hits = new List<PdfSearchHit>();

        try
        {
            using var document = PdfDocument.Open(filePath, new ParsingOptions
            {
                Password = password!,
                UseLenientParsing = true,
                SkipMissingFonts = true
            });

            foreach (var page in document.GetPages())
            {
                cancellationToken.ThrowIfCancellationRequested();

                IReadOnlyList<PdfSearchHit> pageHits;
                try
                {
                    pageHits = SearchPage(page, query, MaxResults - hits.Count);
                }
                catch
                {
                    // A single malformed content stream should not abort the whole search.
                    continue;
                }

                hits.AddRange(pageHits);
                if (hits.Count >= MaxResults)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new PdfDocumentException(
                PdfFailure.Unknown,
                "Text search could not read this document.",
                ex);
        }

        return hits;
    }

    private static IReadOnlyList<PdfSearchHit> SearchPage(Page page, string query, int remaining)
    {
        if (remaining <= 0)
        {
            return [];
        }

        var words = new List<SearchWord>();
        var text = BuildPageText(page, words);
        if (text.Length == 0)
        {
            return [];
        }

        var hits = new List<PdfSearchHit>();
        var offset = 0;
        var matchIndex = 0;
        var canHighlight = page.Rotation.Value % 360 == 0 && page.Width > 0 && page.Height > 0;

        while (offset < text.Length && hits.Count < remaining)
        {
            var index = text.IndexOf(query, offset, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                break;
            }

            var end = index + query.Length;
            var rects = canHighlight
                ? GetRects(words, index, end, page.Width, page.Height)
                : [];

            hits.Add(new PdfSearchHit
            {
                PageNumber = page.Number,
                Snippet = BuildSnippet(text, index, query.Length),
                Rects = rects,
                MatchIndexOnPage = matchIndex
            });

            matchIndex++;
            offset = Math.Max(index + 1, end);
        }

        return hits;
    }

    private static string BuildPageText(Page page, List<SearchWord> words)
    {
        var builder = new StringBuilder();
        foreach (var word in page.GetWords())
        {
            var text = NormalizeWhitespace(word.Text);
            if (text.Length == 0)
            {
                continue;
            }

            if (builder.Length > 0)
            {
                builder.Append(' ');
            }

            var start = builder.Length;
            builder.Append(text);
            words.Add(new SearchWord(start, builder.Length, word.BoundingBox.Left, word.BoundingBox.Top,
                word.BoundingBox.Width, word.BoundingBox.Height));
        }

        return builder.ToString();
    }

    private static IReadOnlyList<PdfSearchRect> GetRects(
        IReadOnlyList<SearchWord> words,
        int start,
        int end,
        double pageWidth,
        double pageHeight)
    {
        var rects = new List<PdfSearchRect>();
        foreach (var word in words)
        {
            if (word.End <= start || word.Start >= end)
            {
                continue;
            }

            var x = word.Left / pageWidth;
            var y = (pageHeight - word.Top) / pageHeight;
            var width = word.Width / pageWidth;
            var height = word.Height / pageHeight;

            x = Math.Clamp(x, 0, 1);
            y = Math.Clamp(y, 0, 1);
            width = Math.Clamp(width, 0, 1 - x);
            height = Math.Clamp(height, 0, 1 - y);

            if (width > 0.0001 && height > 0.0001)
            {
                rects.Add(new PdfSearchRect(x, y, width, height));
            }
        }

        return rects;
    }

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

        if (end < text.Length)
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
        string.IsNullOrWhiteSpace(value) ? string.Empty : Whitespace.Replace(value, " ").Trim();

    private readonly record struct SearchWord(
        int Start,
        int End,
        double Left,
        double Top,
        double Width,
        double Height);
}
