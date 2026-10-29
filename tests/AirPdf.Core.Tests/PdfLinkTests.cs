using AirPdf.Core.Models;
using AirPdf.Core.Services;
using Xunit;

namespace AirPdf.Core.Tests;

/// <summary>
/// <c>links.pdf</c> is a two-page fixture: page 1 carries an external URI link, page 2 an
/// internal link back to page 1. See tests/Assets/README.md for how it is built.
/// </summary>
public sealed class PdfLinkTests
{
    private static readonly string LinksPath =
        Path.Combine(AppContext.BaseDirectory, "Assets", "links.pdf");

    private static readonly string SamplePath =
        Path.Combine(AppContext.BaseDirectory, "Assets", "sample.pdf");

    [Fact]
    public async Task GetLinksAsync_ReadsAnExternalUriLink()
    {
        using var service = new PdfDocumentService();
        await service.OpenAsync(LinksPath);

        var links = await service.GetLinksAsync(LinksPath, 1);

        var link = Assert.Single(links);
        Assert.Equal(PdfLinkKind.Uri, link.Kind);
        Assert.Equal("https://example.com/airpdf-links", link.Uri);

        // Rect [20 300 120 340] on a 300x400 page, normalized with a top-left origin.
        Assert.InRange(link.Bounds.X, 0.06, 0.07);
        Assert.InRange(link.Bounds.Y, 0.14, 0.16);
        Assert.InRange(link.Bounds.Width, 0.32, 0.34);
        Assert.InRange(link.Bounds.Height, 0.09, 0.11);
    }

    [Fact]
    public async Task GetLinksAsync_ResolvesAnInternalPageDestination()
    {
        using var service = new PdfDocumentService();
        await service.OpenAsync(LinksPath);

        var links = await service.GetLinksAsync(LinksPath, 2);

        var link = Assert.Single(links);
        Assert.Equal(PdfLinkKind.Page, link.Kind);
        Assert.Equal(1, link.PageNumber);
        Assert.Null(link.Uri);
    }

    [Fact]
    public async Task GetLinksAsync_PageWithoutLinks_ReturnsEmpty()
    {
        using var service = new PdfDocumentService();
        await service.OpenAsync(SamplePath);

        var links = await service.GetLinksAsync(SamplePath, 1);

        Assert.Empty(links);
    }

    [Fact]
    public async Task GetLinksAsync_AfterCloseDocument_ReturnsEmptyInsteadOfFailing()
    {
        using var service = new PdfDocumentService();
        await service.OpenAsync(LinksPath);
        service.CloseDocument();

        var links = await service.GetLinksAsync(LinksPath, 1);

        Assert.Empty(links);
    }

    [Fact]
    public async Task GetLinksAsync_KeepsLinksInsideThePage()
    {
        using var service = new PdfDocumentService();
        var document = await service.OpenAsync(LinksPath);
        Assert.Equal(2, document.PageCount);

        for (var page = 1; page <= document.PageCount; page++)
        {
            foreach (var link in await service.GetLinksAsync(LinksPath, page))
            {
                Assert.InRange(link.Bounds.X, 0, 1);
                Assert.InRange(link.Bounds.Y, 0, 1);
                Assert.InRange(link.Bounds.X + link.Bounds.Width, 0, 1.0001);
                Assert.InRange(link.Bounds.Y + link.Bounds.Height, 0, 1.0001);
            }
        }
    }
}
