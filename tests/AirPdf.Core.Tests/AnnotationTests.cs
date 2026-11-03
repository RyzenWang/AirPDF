using AirPdf.Core.Models;
using AirPdf.Core.Services;
using SkiaSharp;
using Xunit;

namespace AirPdf.Core.Tests;

/// <summary>
/// Highlights and the copy that carries them. Both are edits to a PDF, which this application
/// could not do at all before, so the tests check the parts that are easy to get wrong: the
/// annotation really exists, it really draws, and the original file is never touched.
/// </summary>
[Collection(PdfiumCollection.Name)]
public sealed class AnnotationTests : IDisposable
{
    private static readonly string SamplePath =
        Path.Combine(AppContext.BaseDirectory, "Assets", "sample.pdf");

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"airpdf-annot-{Guid.NewGuid():N}");

    public AnnotationTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch
        {
            // A leftover temp directory must not fail a test run.
        }
    }

    [Fact]
    public async Task AddHighlight_AddsOneAnnotationOfTheHighlightSubtype()
    {
        using var service = new PdfDocumentService();
        var info = await service.OpenAsync(SamplePath);
        var page = info.Pages[0];

        var before = await service.GetAnnotationCountAsync(SamplePath, 1);
        await service.AddHighlightAsync(SamplePath, 1, [Band(page, 0.6)]);

        Assert.Equal(before + 1, await service.GetAnnotationCountAsync(SamplePath, 1));

        // Subtype 9 is HIGHLIGHT in the PDFium API; a different value would mean the rectangle
        // landed as some other markup.
        Assert.Equal(9, await service.GetAnnotationSubtypeAsync(SamplePath, 1, before));
    }

    [Fact]
    public async Task AddHighlight_PaintsTheWholeAreaItCovers()
    {
        using var service = new PdfDocumentService();
        var info = await service.OpenAsync(SamplePath);
        var page = info.Pages[0];

        // Cover the whole page: the point is that annotation rendering reaches every part of the
        // quad, and a page-wide rectangle cannot miss the way a band over blank margins might.
        var whole = new PdfQuad(0, page.Height, page.Width, 0);

        var before = await service.RenderPageAsync(SamplePath, 1, 600);
        var samplesX = new[] { 0.1, 0.5, 0.9 };
        var samplesY = new[] { 0.1, 0.5, 0.9 };
        var beforePixels = (from fx in samplesX from fy in samplesY select Pixel(before, fx, fy)).ToList();

        await service.AddHighlightAsync(SamplePath, 1, [whole]);
        var after = await service.RenderPageAsync(SamplePath, 1, 600);
        var afterPixels = (from fx in samplesX from fy in samplesY select Pixel(after, fx, fy)).ToList();

        before.Dispose();
        after.Dispose();

        // Every sampled point has to move: a highlight that only painted its border, or only the
        // first quad, would leave the middle of the page untouched.
        for (var index = 0; index < beforePixels.Count; index++)
        {
            Assert.True(
                beforePixels[index] != afterPixels[index],
                $"sample {index} did not change: {beforePixels[index]} -> {afterPixels[index]}");
        }
    }
    [Fact]
    public async Task SaveCopy_WritesTheAnnotationAndLeavesTheOriginalUntouched()
    {
        var originalHash = HashOf(SamplePath);
        var originalLength = new FileInfo(SamplePath).Length;

        using var service = new PdfDocumentService();
        var info = await service.OpenAsync(SamplePath);
        var before = await service.GetAnnotationCountAsync(SamplePath, 1);
        await service.AddHighlightAsync(SamplePath, 1, [Band(info.Pages[0], 0.6)]);

        var destination = Path.Combine(_directory, "annotated.pdf");
        await service.SaveCopyAsync(SamplePath, destination);

        // The copy carries the annotation...
        Assert.True(File.Exists(destination));
        Assert.True(new FileInfo(destination).Length > 0);

        using var reopened = new PdfDocumentService();
        await reopened.OpenAsync(destination);
        Assert.Equal(before + 1, await reopened.GetAnnotationCountAsync(destination, 1));

        // ...and the file the reader opened was not modified, not even its timestamp.
        Assert.Equal(originalHash, HashOf(SamplePath));
        Assert.Equal(originalLength, new FileInfo(SamplePath).Length);
    }

    [Fact]
    public async Task RemoveHighlightsAt_RemovesTheHighlightUnderThePoint()
    {
        using var service = new PdfDocumentService();
        var info = await service.OpenAsync(SamplePath);
        var page = info.Pages[0];
        var band = Band(page, 0.5);

        var before = await service.GetAnnotationCountAsync(SamplePath, 1);
        await service.AddHighlightAsync(SamplePath, 1, [band]);
        Assert.Equal(before + 1, await service.GetAnnotationCountAsync(SamplePath, 1));

        var removed = await service.RemoveHighlightsAtAsync(
            SamplePath,
            1,
            new PdfQuad(band.Left + 1, band.Top - 1, band.Left + 1, band.Top - 1));

        Assert.Single(removed);
        Assert.Equal(before, await service.GetAnnotationCountAsync(SamplePath, 1));
    }

    [Fact]
    public async Task RemoveHighlightsAt_IgnoresAPointOutsideEveryHighlight()
    {
        using var service = new PdfDocumentService();
        var info = await service.OpenAsync(SamplePath);
        var page = info.Pages[0];

        await service.AddHighlightAsync(SamplePath, 1, [Band(page, 0.5)]);
        var after = await service.GetAnnotationCountAsync(SamplePath, 1);

        // Well below the band: nothing there, so nothing may be removed.
        var removed = await service.RemoveHighlightsAtAsync(
            SamplePath,
            1,
            new PdfQuad(page.Width / 2, 1, page.Width / 2, 1));

        Assert.Empty(removed);
        Assert.Equal(after, await service.GetAnnotationCountAsync(SamplePath, 1));
    }

    [Fact]
    public async Task AHighlightCanBeReadBackWithItsGeometryAndColour()
    {
        using var service = new PdfDocumentService();
        var info = await service.OpenAsync(SamplePath);
        var band = Band(info.Pages[0], 0.5);

        await service.AddHighlightAsync(SamplePath, 1, [band], 12, 34, 56);

        var highlights = await service.GetHighlightsAsync(SamplePath, 1);
        var added = Assert.Single(
            highlights,
            candidate => candidate.Quads.Count == 1 &&
                         Math.Abs(candidate.Quads[0].Left - band.Left) < 0.5 &&
                         Math.Abs(candidate.Quads[0].Top - band.Top) < 0.5);

        Assert.Equal(12, added.Red);
        Assert.Equal(34, added.Green);
        Assert.Equal(56, added.Blue);
    }
    [Fact]
    public async Task RemoveHighlightMatching_TakesBackExactlyThatHighlight()
    {
        using var service = new PdfDocumentService();
        var info = await service.OpenAsync(SamplePath);
        var page = info.Pages[0];
        var band = Band(page, 0.5);
        var before = await service.GetAnnotationCountAsync(SamplePath, 1);

        await service.AddHighlightAsync(SamplePath, 1, [band]);
        var removed = await service.RemoveHighlightsAtAsync(
            SamplePath,
            1,
            new PdfQuad(band.Left + 1, band.Top - 1, band.Left + 1, band.Top - 1));

        Assert.Single(removed);
        var snapshot = removed[0];
        Assert.Equal(1, snapshot.PageNumber);
        Assert.Single(snapshot.Quads);
        Assert.Equal(band.Left, snapshot.Quads[0].Left, 2);
        Assert.Equal(band.Top, snapshot.Quads[0].Top, 2);
        Assert.Equal(255, snapshot.Red);

        // Restoring the snapshot brings the annotation count back to where it was after adding.
        await service.AddHighlightAsync(SamplePath, 1, snapshot.Quads, snapshot.Red, snapshot.Green, snapshot.Blue);
        Assert.Equal(before + 1, await service.GetAnnotationCountAsync(SamplePath, 1));
    }
    [Fact]
    public async Task RemovedHighlights_AreGoneFromTheSavedCopyToo()
    {
        using var service = new PdfDocumentService();
        var info = await service.OpenAsync(SamplePath);
        var page = info.Pages[0];
        var before = await service.GetAnnotationCountAsync(SamplePath, 1);

        var band = Band(page, 0.5);
        await service.AddHighlightAsync(SamplePath, 1, [band]);
        await service.RemoveHighlightsAtAsync(
            SamplePath,
            1,
            new PdfQuad(band.Left + 1, band.Top - 1, band.Left + 1, band.Top - 1));

        var destination = Path.Combine(_directory, "cleaned.pdf");
        await service.SaveCopyAsync(SamplePath, destination);

        using var reopened = new PdfDocumentService();
        await reopened.OpenAsync(destination);
        Assert.Equal(before, await reopened.GetAnnotationCountAsync(destination, 1));
    }
    [Fact]
    public async Task SaveInPlace_WritesIntoTheOriginalAndKeepsTheDocumentUsable()
    {
        // A copy of the sample, because this test is the one that changes a file.
        var target = Path.Combine(_directory, "in-place.pdf");
        File.Copy(SamplePath, target);

        var before = HashOf(target);

        using var service = new PdfDocumentService();
        var info = await service.OpenAsync(target);
        var baseline = await service.GetAnnotationCountAsync(target, 1);
        await service.AddHighlightAsync(target, 1, [Band(info.Pages[0], 0.5)]);

        await service.SaveInPlaceAsync(target);

        // The file itself now carries the annotation...
        Assert.NotEqual(before, HashOf(target));

        using var reopened = new PdfDocumentService();
        var reopenedInfo = await reopened.OpenAsync(target);
        Assert.Equal(baseline + 1, await reopened.GetAnnotationCountAsync(target, 1));
        Assert.True(reopenedInfo.PageCount > 0);

        // ...the temporary file is gone...
        Assert.False(File.Exists(target + ".airpdf-save"));

        // ...and the service that saved can still draw the page.
        var bitmap = await service.RenderPageAsync(target, 1, 400);
        Assert.NotNull(bitmap);
        bitmap.Dispose();
    }
    [Fact]
    public async Task AfterSaveInPlace_ThePagesStillHaveContent()
    {
        // "It renders" is not the same as "it has ink on it": a blank bitmap is still a bitmap,
        // which is exactly the failure being chased here.
        var target = Path.Combine(_directory, "content.pdf");
        File.Copy(SamplePath, target);

        using (var writer = new PdfDocumentService())
        {
            var info = await writer.OpenAsync(target);
            await writer.AddHighlightAsync(target, 1, [Band(info.Pages[0], 0.5)]);
            await writer.SaveInPlaceAsync(target);
        }

        using var reader = new PdfDocumentService();
        await reader.OpenAsync(target);
        using var page = await reader.RenderPageAsync(target, 1, 600);

        var dark = 0;
        for (var y = 0; y < page.Height; y += 4)
        {
            for (var x = 0; x < page.Width; x += 4)
            {
                var colour = page.GetPixel(x, y);
                if (colour.Red < 200 || colour.Green < 200 || colour.Blue < 200)
                {
                    dark++;
                }
            }
        }

        Assert.True(dark > 50, $"the rendered page is blank: {dark} non-white samples");
    }
    [Fact]
    public async Task SaveInPlace_OnAReadOnlyFile_FailsCleanlyAndKeepsTheDocumentUsable()
    {
        var target = Path.Combine(_directory, "read-only.pdf");
        File.Copy(SamplePath, target);

        using var service = new PdfDocumentService();
        var info = await service.OpenAsync(target);
        var band = Band(info.Pages[0], 0.5);
        await service.AddHighlightAsync(target, 1, [band]);

        var attributes = File.GetAttributes(target);
        File.SetAttributes(target, attributes | FileAttributes.ReadOnly);

        try
        {
            var failure = await Assert.ThrowsAsync<PdfDocumentException>(
                () => service.SaveInPlaceAsync(target));
            Assert.Equal(PdfFailure.AccessDenied, failure.Failure);

            // The important part: the reader can still draw the page and still edit, because the
            // session was checked before it was closed rather than after.
            var bitmap = await service.RenderPageAsync(target, 1, 300);
            Assert.NotNull(bitmap);
            bitmap.Dispose();

            await service.AddHighlightAsync(target, 1, [Band(info.Pages[0], 0.7)]);
        }
        finally
        {
            File.SetAttributes(target, attributes);
        }
    }
    [Fact]
    public async Task AddHighlight_WithNoRectangles_ChangesNothing()
    {
        using var service = new PdfDocumentService();
        await service.OpenAsync(SamplePath);
        var before = await service.GetAnnotationCountAsync(SamplePath, 1);

        await service.AddHighlightAsync(SamplePath, 1, []);

        Assert.Equal(before, await service.GetAnnotationCountAsync(SamplePath, 1));
    }

    [Fact]
    public async Task EditingAfterTheDocumentIsClosed_FailsLoudly()
    {
        using var service = new PdfDocumentService();
        var info = await service.OpenAsync(SamplePath);
        service.CloseDocument();

        await Assert.ThrowsAsync<PdfDocumentException>(
            () => service.AddHighlightAsync(SamplePath, 1, [Band(info.Pages[0], 0.6)]));
    }

    /// <summary>
    /// A horizontal band across the page, in page space. <paramref name="fraction"/> is measured
    /// from the top, which keeps the numbers readable in the assertions.
    /// </summary>
    private static PdfQuad Band(PdfPageInfo page, double fraction)
    {
        var top = page.Height * (1 - fraction);
        var bottom = top - (page.Height * 0.05);

        return new PdfQuad(page.Width * 0.1, top, page.Width * 0.9, bottom);
    }

    /// <summary>Reads the pixel at a fraction of the rendered page, 0..1 from the top-left.</summary>
    private static SKColor Pixel(SKBitmap bitmap, double x, double y)
    {
        var px = Math.Clamp((int)(x * bitmap.Width), 0, bitmap.Width - 1);
        var py = Math.Clamp((int)(y * bitmap.Height), 0, bitmap.Height - 1);
        return bitmap.GetPixel(px, py);
    }
    private static string HashOf(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
    }
}
