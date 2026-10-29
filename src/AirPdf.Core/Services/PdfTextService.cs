using System.Text.RegularExpressions;
using AirPdf.Core.Models;
using UglyToad.PdfPig;

namespace AirPdf.Core.Services;

/// <summary>
/// Extracts word-level text geometry for selection. This is intentionally
/// separate from search: search can stop at a result cap, while selection
/// needs a stable, ordered word index for the pages being read.
/// </summary>
public sealed class PdfTextService
{
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    public Task ExtractAsync(
        string filePath,
        string? password,
        IProgress<PdfTextPage>? progress,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => ExtractCore(filePath, password, progress, cancellationToken), cancellationToken);

    /// <summary>
    /// Extracts only the requested pages, opening the document once. Used by
    /// the reader to load text lazily as pages become visible.
    /// </summary>
    public Task<IReadOnlyList<PdfTextPage>> ExtractPagesAsync(
        string filePath,
        IReadOnlyCollection<int> pageNumbers,
        string? password,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pageNumbers);

        var normalized = pageNumbers
            .Where(pageNumber => pageNumber > 0)
            .Distinct()
            .OrderBy(pageNumber => pageNumber)
            .ToArray();

        if (normalized.Length == 0)
        {
            return Task.FromResult<IReadOnlyList<PdfTextPage>>([]);
        }

        return Task.Run(
            () => ExtractPagesCore(filePath, normalized, password, cancellationToken),
            cancellationToken);
    }

    private static void ExtractCore(
        string filePath,
        string? password,
        IProgress<PdfTextPage>? progress,
        CancellationToken cancellationToken)
    {
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
                progress?.Report(ExtractPageSafe(page));
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
                "The text layer could not be read.",
                ex);
        }
    }

    private static IReadOnlyList<PdfTextPage> ExtractPagesCore(
        string filePath,
        IReadOnlyList<int> pageNumbers,
        string? password,
        CancellationToken cancellationToken)
    {
        var results = new List<PdfTextPage>(pageNumbers.Count);

        try
        {
            using var document = PdfDocument.Open(filePath, new ParsingOptions
            {
                Password = password!,
                UseLenientParsing = true,
                SkipMissingFonts = true
            });

            foreach (var pageNumber in pageNumbers)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (pageNumber > document.NumberOfPages)
                {
                    continue;
                }

                try
                {
                    results.Add(ExtractPageSafe(document.GetPage(pageNumber)));
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    results.Add(EmptyPage(pageNumber));
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
                "The text layer could not be read.",
                ex);
        }

        return results;
    }

    private static PdfTextPage ExtractPageSafe(UglyToad.PdfPig.Content.Page page)
    {
        try
        {
            return ExtractPage(page);
        }
        catch
        {
            return EmptyPage(page.Number);
        }
    }

    private static PdfTextPage EmptyPage(int pageNumber) => new()
    {
        PageNumber = pageNumber,
        Words = []
    };

    private static PdfTextPage ExtractPage(UglyToad.PdfPig.Content.Page page)
    {
        var pageWidth = page.Width;
        var pageHeight = page.Height;

        if (pageWidth <= 0 || pageHeight <= 0 || page.Rotation.Value % 360 != 0)
        {
            return EmptyPage(page.Number);
        }

        var words = new List<PdfTextWord>();
        foreach (var word in page.GetWords())
        {
            var text = Normalize(word.Text);
            if (text.Length == 0)
            {
                continue;
            }

            var box = word.BoundingBox;
            var x = Math.Clamp(box.Left / pageWidth, 0, 1);
            var y = Math.Clamp((pageHeight - box.Top) / pageHeight, 0, 1);
            var width = Math.Clamp(box.Width / pageWidth, 0, 1 - x);
            var height = Math.Clamp(box.Height / pageHeight, 0, 1 - y);

            if (width <= 0.0001 || height <= 0.0001)
            {
                continue;
            }

            words.Add(new PdfTextWord
            {
                Index = words.Count,
                Text = text,
                Bounds = new PdfSearchRect(x, y, width, height)
            });
        }

        return new PdfTextPage
        {
            PageNumber = page.Number,
            Words = words
        };
    }

    private static string Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : Whitespace.Replace(value, " ").Trim();
}
