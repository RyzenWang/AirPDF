using AirPdf.Core.Models;
using AirPdf.Core.Services;
using Xunit;

namespace AirPdf.Core.Tests;

public sealed class PdfDocumentServiceTests
{
    private static readonly string SamplePath =
        Path.Combine(AppContext.BaseDirectory, "Assets", "sample.pdf");

    [Fact]
    public async Task OpenAsync_ReadsPagesAndDocumentInfo()
    {
        using var service = new PdfDocumentService();

        var document = await service.OpenAsync(SamplePath);

        Assert.Equal(8, document.PageCount);
        Assert.Equal(8, document.Pages.Count);
        Assert.Equal("sample.pdf", document.FileName);
        Assert.True(document.FileSizeBytes > 1000);
        Assert.All(document.Pages, page =>
        {
            Assert.True(page.Width > 590 && page.Width < 600);
            Assert.True(page.Height > 835 && page.Height < 850);
        });
    }

    [Fact]
    public async Task RenderPageAsync_ReturnsRenderablePixels()
    {
        using var service = new PdfDocumentService();

        using var bitmap = await service.RenderPageAsync(SamplePath, 1, 600);

        Assert.Equal(600, bitmap.Width);
        Assert.True(bitmap.Height > 800);
        var pixel = bitmap.GetPixel(30, 30);
        Assert.Equal(255, pixel.Alpha);
    }

    [Fact]
    public async Task RenderPageAsync_KeepsThePageAspectRatio()
    {
        using var service = new PdfDocumentService();
        var document = await service.OpenAsync(SamplePath);
        var page = document.Pages[0];

        using var bitmap = await service.RenderPageAsync(SamplePath, 1, 600);

        var expectedHeight = 600 * (page.Height / page.Width);
        Assert.InRange(bitmap.Height, expectedHeight - 1, expectedHeight + 1);
    }

    /// <summary>
    /// The document handle now stays open between renders, so rendering a page far away and
    /// then coming back must produce exactly the same pixels as the first render.
    /// </summary>
    [Fact]
    public async Task RenderPageAsync_ReusesTheOpenDocumentWithoutDrift()
    {
        using var service = new PdfDocumentService();
        await service.OpenAsync(SamplePath);

        var first = await RenderPixelsAsync(service, pageNumber: 1);
        await RenderPixelsAsync(service, pageNumber: 8);
        var again = await RenderPixelsAsync(service, pageNumber: 1);

        Assert.Equal(first, again);
    }

    [Fact]
    public async Task RenderPageAsync_AfterCloseDocument_FallsBackToATransientHandle()
    {
        using var service = new PdfDocumentService();
        await service.OpenAsync(SamplePath);
        service.CloseDocument();

        var pixels = await RenderPixelsAsync(service, pageNumber: 3);

        Assert.NotEmpty(pixels);
        Assert.Contains(pixels, value => value != 255);
    }

    private static async Task<byte[]> RenderPixelsAsync(PdfDocumentService service, int pageNumber)
    {
        using var bitmap = await service.RenderPageAsync(SamplePath, pageNumber, 400);

        var rowBytes = bitmap.Info.Width * 4;
        var pixels = new byte[rowBytes * bitmap.Info.Height];
        var source = bitmap.GetPixels();
        for (var y = 0; y < bitmap.Info.Height; y++)
        {
            System.Runtime.InteropServices.Marshal.Copy(
                source + (y * bitmap.RowBytes),
                pixels,
                y * rowBytes,
                rowBytes);
        }

        return pixels;
    }

    [Fact]
    public async Task SearchAsync_FindsTextWithHighlightGeometry()
    {
        var service = new PdfSearchService();

        var hits = await service.SearchAsync(SamplePath, "AirPDF");

        Assert.NotEmpty(hits);
        Assert.Contains(hits, hit => hit.PageNumber == 1);
        Assert.All(hits, hit => Assert.False(string.IsNullOrWhiteSpace(hit.Snippet)));
        Assert.Contains(hits, hit => hit.Rects.Count > 0);
    }

    [Fact]
    public async Task SearchAsync_HandlesNonAsciiText()
    {
        var service = new PdfSearchService();

        var hits = await service.SearchAsync(SamplePath, "轻量");

        Assert.NotEmpty(hits);
        Assert.Contains(hits, hit => hit.PageNumber == 7);
    }

    [Fact]
    public async Task ExtractTextAsync_ReturnsWordGeometry()
    {
        var service = new PdfTextService();
        var pages = new List<PdfTextPage>();
        var progress = new InlineProgress<PdfTextPage>(pages.Add);

        await service.ExtractAsync(SamplePath, null, progress);

        Assert.Equal(8, pages.Count);
        Assert.Contains(pages, page => page.Words.Any(word =>
            word.Text.Contains("quiet", StringComparison.OrdinalIgnoreCase)));
        Assert.All(
            pages.SelectMany(page => page.Words),
            word =>
            {
                Assert.InRange(word.Bounds.X, 0, 1);
                Assert.InRange(word.Bounds.Y, 0, 1);
                Assert.InRange(word.Bounds.Width, 0, 1);
                Assert.InRange(word.Bounds.Height, 0, 1);
            });
    }

    [Fact]
    public async Task ExtractPagesAsync_LoadsOnlyRequestedPages()
    {
        var service = new PdfTextService();

        var pages = await service.ExtractPagesAsync(SamplePath, new[] { 7 }, password: null);

        var page = Assert.Single(pages);
        Assert.Equal(7, page.PageNumber);
        Assert.NotEmpty(page.Words);
    }

    [Fact]
    public void SettingsService_RoundTripsReadingPosition()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"airpdf-settings-{Guid.NewGuid()}");
        try
        {
            var service = new SettingsService(directory);
            var settings = new AppSettings();
            settings.ReadingPositions.Add(new ReadingPositionEntry
            {
                FilePath = SamplePath,
                Page = 3,
                PageOffset = 0.42,
                FileSizeBytes = 1234,
                FileLastWriteUtcTicks = 5678
            });

            service.Save(settings);
            var loaded = service.Load();

            var position = Assert.Single(loaded.ReadingPositions);
            Assert.Equal(SamplePath, position.FilePath);
            Assert.Equal(3, position.Page);
            Assert.Equal(0.42, position.PageOffset, 3);
            Assert.Equal(1234, position.FileSizeBytes);
            Assert.Equal(5678, position.FileLastWriteUtcTicks);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task OpenAsync_MissingFile_ThrowsFriendlyFailure()
    {
        using var service = new PdfDocumentService();

        var error = await Assert.ThrowsAsync<PdfDocumentException>(
            () => service.OpenAsync(Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.pdf")));

        Assert.Equal(PdfFailure.NotFound, error.Failure);
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
