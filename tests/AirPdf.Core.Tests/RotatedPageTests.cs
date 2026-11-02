using System.Runtime.InteropServices;
using AirPdf.Core.Models;
using AirPdf.Core.Services;
using SkiaSharp;
using Xunit;

namespace AirPdf.Core.Tests;

/// <summary>
/// Rotated pages are verified against pixels: a computed rectangle is only correct if the
/// rendered page actually has content under it. A transform off by one quarter turn lands on
/// blank paper and fails these assertions.
/// </summary>
public sealed class RotatedPageTests
{
    [Theory]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public async Task LinkBounds_CoverTheBlockThatWasDrawn(int rotation)
    {
        var path = WriteFixture(rotation);
        try
        {
            using var service = new PdfDocumentService();
            await service.OpenAsync(path);

            var link = Assert.Single(await service.GetLinksAsync(path, 1));
            Assert.Equal(PdfLinkKind.Uri, link.Kind);

            using var page = await service.RenderPageAsync(path, 1, 400);

            var dark = DarkFraction(page, link.Bounds);
            Assert.True(
                dark > 0.5,
                $"quarter turns {rotation / 90}: link rectangle covered {dark:P0} dark pixels");
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
    public async Task LinkBounds_StayInsideTheDisplayedPage(int rotation)
    {
        var path = WriteFixture(rotation);
        try
        {
            using var service = new PdfDocumentService();
            await service.OpenAsync(path);

            var link = Assert.Single(await service.GetLinksAsync(path, 1));

            Assert.InRange(link.Bounds.X, 0, 1);
            Assert.InRange(link.Bounds.Y, 0, 1);
            Assert.InRange(link.Bounds.X + link.Bounds.Width, 0, 1.0001);
            Assert.InRange(link.Bounds.Y + link.Bounds.Height, 0, 1.0001);

            // The block is 100 x 30 of the page's 200 x 300 points, so the reported area is
            // 0.05 whatever the rotation.
            var area = link.Bounds.Width * link.Bounds.Height;
            Assert.InRange(area, 0.045, 0.055);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task UprightPages_StillBehaveAsBefore()
    {
        var path = WriteFixture(0);
        try
        {
            using var service = new PdfDocumentService();
            await service.OpenAsync(path);

            var link = Assert.Single(await service.GetLinksAsync(path, 1));
            using var bitmap = await service.RenderPageAsync(path, 1, 400);

            Assert.True(DarkFraction(bitmap, link.Bounds) > 0.5);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// Text on rotated pages is selectable: the PDFium text layer reports characters in
    /// reading order with boxes in unrotated page space, so both the order and the geometry
    /// survive a quarter or half turn.
    /// </summary>
    [Theory]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public async Task TextLayer_IsAvailableOnRotatedPages(int rotation)
    {
        var path = WriteFixture(rotation);
        try
        {
            using var documents = new PdfDocumentService();
            await documents.OpenAsync(path);
            var text = new PdfTextService(documents);

            var page = Assert.Single(await text.ExtractPagesAsync(path, new[] { 1 }, password: null));

            Assert.Equal(RotatedPdfFixture.Word, string.Concat(page.Words.Select(word => word.Text)));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string WriteFixture(int rotation)
    {
        var path = Path.Combine(Path.GetTempPath(), $"airpdf-rot-{rotation}-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, RotatedPdfFixture.Create(rotation));
        return path;
    }

    /// <summary>Fraction of the pixels inside <paramref name="bounds"/> that are dark.</summary>
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
