namespace AirPdf.Core.Models;

/// <summary>
/// A rectangle in unrotated page space: points, origin at the bottom-left, <see cref="Top"/> is
/// the larger Y. This is the space PDF annotations use, and it is what a page's selection has to
/// be converted back to before it can be written into the file.
/// </summary>
public readonly record struct PdfQuad(double Left, double Top, double Right, double Bottom)
{
    public double Width => Right - Left;

    public double Height => Top - Bottom;
}
