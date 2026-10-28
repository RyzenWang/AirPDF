using System.Text.RegularExpressions;
using AirPdf.Core.Models;
using UglyToad.PdfPig;

namespace AirPdf.Core.Services;

/// <summary>
/// Extracts word-level text geometry for selection. This is intentionally
/// separate from search: search can stop at a result cap, while selection
/// needs a stable, ordered word index for every page.
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

                PdfTextPage textPage;
                try
                {
                    textPage = ExtractPage(page);
                }
                catch
                {
                    textPage = new PdfTextPage
                    {
                        PageNumber = page.Number,
                        Words = []
                    };
                }

                progress?.Report(textPage);
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

    private static PdfTextPage ExtractPage(UglyToad.PdfPig.Content.Page page)
    {
        var pageWidth = page.Width;
        var pageHeight = page.Height;

        if (pageWidth <= 0 || pageHeight <= 0 || page.Rotation.Value % 360 != 0)
        {
            return new PdfTextPage
            {
                PageNumber = page.Number,
                Words = []
            };
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
