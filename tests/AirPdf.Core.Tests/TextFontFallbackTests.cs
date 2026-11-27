using AirPdf.Core.Models;
using AirPdf.Core.Pdfium;
using AirPdf.Core.Services;
using Xunit;

namespace AirPdf.Core.Tests;

/// <summary>
/// On a machine without a font that can be embedded, text annotations fall back to PDFium's
/// generated appearance. That appearance still draws Latin text, and it is what keeps the words
/// visible on platforms whose font list this code does not know. CI's Linux and macOS jobs are
/// the ones that exercise it for real, so this pins the fallback down on every platform.
/// </summary>
[Collection("TextFontOverride")]
public sealed class TextFontFallbackTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"airpdf-font-fallback-{Guid.NewGuid():N}");

    public TextFontFallbackTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        PdfiumDocument.TextFontPathOverride = null;
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A failed cleanup must not hide the assertion that failed first.
        }
    }

    [Fact]
    public async Task AddFreeText_WithoutAUsableFont_StillRendersLatinText()
    {
        PdfiumDocument.TextFontPathOverride = string.Empty;

        var blank = Path.Combine(_directory, "fallback.pdf");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Assets", "outline.pdf"), blank);

        using var service = new PdfDocumentService();
        await service.OpenAsync(blank);

        var quad = new PdfQuad(90, 700, 430, 660);
        await service.AddFreeTextAsync(blank, 1, quad, "Hello text", 24, 20, 20, 20);

        using var rendered = await service.RenderPageAsync(blank, 1, 900);

        var scale = rendered.Width / 612.0;
        var ink = 0;
        for (var y = (int)((792 - 700) * scale); y < (int)((792 - 660) * scale); y++)
        {
            for (var x = (int)(90 * scale); x < (int)(430 * scale); x++)
            {
                var colour = rendered.GetPixel(x, y);
                if (colour.Red < 160 && colour.Green < 160 && colour.Blue < 160)
                {
                    ink++;
                }
            }
        }

        Assert.True(ink > 100, $"the fallback appearance rendered {ink} samples");

        // The words are still the annotation's, whichever appearance drew them.
        var stored = Assert.Single(await service.GetFreeTextsAsync(blank, 1));
        Assert.Equal("Hello text", stored.Text);
        Assert.Equal(24, stored.FontSize);
    }
}

/// <summary>Serialises the tests that swap the process-wide font path.</summary>
[CollectionDefinition("TextFontOverride", DisableParallelization = true)]
public sealed class TextFontOverrideCollection
{
}
