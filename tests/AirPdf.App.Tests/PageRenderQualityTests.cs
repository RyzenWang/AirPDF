using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using AirPdf.App.Rendering;
using AirPdf.App.Services;
using AirPdf.App.ViewModels;
using AirPdf.Core.Models;
using AirPdf.Core.Services;
using Xunit;

namespace AirPdf.App.Tests;

public sealed class PageRenderQualityTests
{
    [AvaloniaFact]
    public async Task Page_RendersSmallerWhileInteractingAndRefinesAfterwards()
    {
        var sample = Path.Combine(AppContext.BaseDirectory, "Assets", "sample.pdf");
        using var documents = new PdfDocumentService();
        await documents.OpenAsync(sample);

        var localization = new LocalizationService();
        var renderer = new PdfPageRenderer(documents, sample, null);
        using var interaction = new RenderInteractionState();
        using var page = new PageViewModel(
            renderer,
            new PdfPageInfo(1, 612, 792),
            localization,
            interaction);

        interaction.BeginInteraction();
        page.Activate(1.0);

        await WaitUntilAsync(() => page.Image is not null, "the reduced render");
        var reducedWidth = page.Image!.PixelSize.Width;
        Assert.Equal(BitmapInterpolationMode.MediumQuality, page.BitmapInterpolationMode);

        await WaitUntilAsync(
            () => page.Image is not null && page.Image.PixelSize.Width > reducedWidth,
            "the refined render");

        Assert.Equal(BitmapInterpolationMode.HighQuality, page.BitmapInterpolationMode);
        Assert.True(page.Image!.PixelSize.Width > reducedWidth);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string description)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10);
        }

        Assert.Fail($"Timed out waiting for {description}.");
    }
}
