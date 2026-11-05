using AirPdf.Core.Models;
using AirPdf.Core.Services;
using SkiaSharp;

namespace AirPdf.App.Rendering;

public sealed class PdfPageRenderer
{
    private readonly PdfDocumentService _service;
    private readonly string _filePath;
    private readonly string? _password;

    public PdfPageRenderer(PdfDocumentService service, string filePath, string? password)
    {
        _service = service;
        _filePath = filePath;
        _password = password;
    }

    public Task<SKBitmap> RenderAsync(
        int pageNumber,
        int pixelWidth,
        CancellationToken cancellationToken,
        PdfRenderPriority priority = PdfRenderPriority.Page) =>
        _service.RenderPageAsync(_filePath, pageNumber, pixelWidth, _password, cancellationToken, priority);

    public Task<IReadOnlyList<PdfLink>> GetLinksAsync(int pageNumber, CancellationToken cancellationToken) =>
        _service.GetLinksAsync(_filePath, pageNumber, cancellationToken);
}
