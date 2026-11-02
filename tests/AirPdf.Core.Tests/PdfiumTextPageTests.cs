using System.Runtime.InteropServices;
using AirPdf.Core.Models;
using AirPdf.Core.Pdfium;
using SkiaSharp;
using Xunit;

namespace AirPdf.Core.Tests;

/// <summary>
/// PDFium's text layer is the replacement for PdfPig's: characters arrive in reading order
/// with boxes in unrotated page space, so rotated pages keep their text and the geometry is
/// the only thing that needs mapping.
/// </summary>
public sealed class PdfiumTextPageTests
{
    private static readonly string SamplePath =
        Path.Combine(AppContext.BaseDirectory, "Assets", "sample.pdf");

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void RotatedPages_KeepTheTextInReadingOrder(int rotation)
    {
        var path = WriteFixture(rotation);
        try
        {
            using var document = PdfiumDocument.Open(path, password: null);

            var page = document.GetTextPage(1);

            var text = string.Concat(page.Words.Select(word => word.Text));
            Assert.Equal(RotatedPdfFixture.Word, text);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public async Task RotatedPages_PutTheWordBoxOnTopOfTheInk(int rotation)
    {
        var path = WriteFixture(rotation);
        try
        {
            using var document = PdfiumDocument.Open(path, password: null);
            var page = document.GetTextPage(1);
            var word = Assert.Single(page.Words);

            using var bitmap = document.RenderPage(1, 400);

            var ink = DarkFraction(bitmap, word.Bounds);
            Assert.True(ink > 0.05, $"quarter turns {rotation / 90}: word box covered {ink:P1} dark pixels");
        }
        finally
        {
            await Task.CompletedTask;
        }
    }

    [Fact]
    public void UprightText_StillExtractsWords()
    {
        using var document = PdfiumDocument.Open(SamplePath, password: null);

        // Page 1 is the cover, whose letters are separated by real spaces; page 2 is body text.
        var page = document.GetTextPage(2);

        Assert.NotEmpty(page.Words);
        var text = string.Join(" ", page.Words.Select(word => word.Text));
        Assert.Contains("Reading", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ChineseText_KeepsItsCharactersWithoutInventingSpaces()
    {
        using var document = PdfiumDocument.Open(SamplePath, password: null);

        // Page 7 of the sample is the Simplified Chinese page.
        var page = document.GetTextPage(7);

        Assert.NotEmpty(page.Words);
        var text = string.Concat(page.Words.Select(word => word.Text));
        Assert.Contains("轻量", text, StringComparison.Ordinal);
    }

    [Fact]
    public void WordBoxesStayInsideThePage()
    {
        using var document = PdfiumDocument.Open(SamplePath, password: null);

        for (var pageNumber = 1; pageNumber <= 3; pageNumber++)
        {
            foreach (var word in document.GetTextPage(pageNumber).Words)
            {
                Assert.InRange(word.Bounds.X, 0, 1);
                Assert.InRange(word.Bounds.Y, 0, 1);
                Assert.InRange(word.Bounds.X + word.Bounds.Width, 0, 1.0001);
                Assert.InRange(word.Bounds.Y + word.Bounds.Height, 0, 1.0001);
            }
        }
    }

    private static string WriteFixture(int rotation)
    {
        var path = Path.Combine(Path.GetTempPath(), $"airpdf-text-{rotation}-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, RotatedPdfFixture.Create(rotation));
        return path;
    }

    private static double DarkFraction(SKBitmap bitmap, PdfSearchRect bounds)
    {
        var width = bitmap.Width;
        var height = bitmap.Height;
        var rowBytes = bitmap.RowBytes;
        var row = new byte[rowBytes];

        var left = Math.Clamp((int)Math.Floor(bounds.X * width), 0, width - 1);
        var right = Math.Clamp((int)Math.Ceiling((bounds.X + bounds.Width) * width), left + 1, width);
        var top = Math.Clamp((int)Math.Floor(bounds.Y * height), 0, height - 1);
        var bottom = Math.Clamp((int)Math.Ceiling((bounds.Y + bounds.Height) * height), top + 1, height);

        long dark = 0;
        long total = 0;

        for (var y = top; y < bottom; y++)
        {
            Marshal.Copy(bitmap.GetPixels() + (y * rowBytes), row, 0, rowBytes);

            for (var x = left; x < right; x++)
            {
                var b = row[x * 4];
                var g = row[(x * 4) + 1];
                var r = row[(x * 4) + 2];
                if ((r + g + b) / 3 < 128)
                {
                    dark++;
                }

                total++;
            }
        }

        return total == 0 ? 0 : dark / (double)total;
    }
}
