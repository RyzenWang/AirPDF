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
        var service = new PdfDocumentService();

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
        var service = new PdfDocumentService();

        using var bitmap = await service.RenderPageAsync(SamplePath, 1, 600);

        Assert.Equal(600, bitmap.Width);
        Assert.True(bitmap.Height > 800);
        var pixel = bitmap.GetPixel(30, 30);
        Assert.Equal(255, pixel.Alpha);
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
    public async Task OpenAsync_MissingFile_ThrowsFriendlyFailure()
    {
        var service = new PdfDocumentService();

        var error = await Assert.ThrowsAsync<PdfDocumentException>(
            () => service.OpenAsync(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".pdf")));

        Assert.Equal(PdfFailure.NotFound, error.Failure);
    }
}
