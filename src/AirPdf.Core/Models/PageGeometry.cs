namespace AirPdf.Core.Models;

/// <summary>
/// Converts rectangles from a PDF page's own coordinate space into the normalized display
/// space the reader paints in.
/// </summary>
/// <remarks>
/// Two coordinate systems meet here:
/// <list type="bullet">
/// <item>PDF page space: origin bottom-left, Y increasing upwards, before <c>/Rotate</c>.
/// PDFium reports link annotation rectangles in it.</item>
/// <item>Display space: origin top-left, Y increasing downwards, normalized to 0..1, after
/// <c>/Rotate</c> has been applied and with width and height swapped for quarter turns.</item>
/// </list>
/// PdfPig does not use this: it already applies the page rotation to its letters, which is
/// why text on rotated pages needs an ordering fix rather than a geometric one.
/// </remarks>
public static class PageGeometry
{
    /// <summary>
    /// Maps a page-space rectangle onto the normalized display rectangle.
    /// </summary>
    /// <param name="left">Page-space left edge.</param>
    /// <param name="top">Page-space top edge (the larger Y).</param>
    /// <param name="right">Page-space right edge.</param>
    /// <param name="bottom">Page-space bottom edge (the smaller Y).</param>
    /// <param name="width">Unrotated page width in points.</param>
    /// <param name="height">Unrotated page height in points.</param>
    /// <param name="rotation">Page <c>/Rotate</c> in degrees; any multiple of 90 is accepted.</param>
    public static PdfSearchRect ToDisplayRect(
        double left,
        double top,
        double right,
        double bottom,
        double width,
        double height,
        int rotation)
    {
        if (width <= 0 || height <= 0)
        {
            return default;
        }

        var corners = new[]
        {
            (Left: Math.Min(left, right), Right: Math.Max(left, right)),
            (Left: Math.Min(top, bottom), Right: Math.Max(top, bottom))
        };

        // Page space is Y-up; flip into a top-left origin before rotating.
        var u0 = corners[0].Left;
        var u1 = corners[0].Right;
        var v0 = height - corners[1].Right;
        var v1 = height - corners[1].Left;

        var quarters = NormalizeRotation(rotation);
        var (d0, d1) = MapCorner(u0, v0, width, height, quarters);
        var (d2, d3) = MapCorner(u1, v1, width, height, quarters);

        var displayWidth = quarters is 90 or 270 ? height : width;
        var displayHeight = quarters is 90 or 270 ? width : height;

        var x = Math.Clamp(Math.Min(d0, d2) / displayWidth, 0, 1);
        var y = Math.Clamp(Math.Min(d1, d3) / displayHeight, 0, 1);
        var w = Math.Clamp(Math.Abs(d2 - d0) / displayWidth, 0, 1 - x);
        var h = Math.Clamp(Math.Abs(d3 - d1) / displayHeight, 0, 1 - y);

        return new PdfSearchRect(x, y, w, h);
    }

    /// <summary>
    /// The inverse of <see cref="ToDisplayRect"/>: turns a normalized display rectangle back into
    /// page space, which is where annotations are written.
    /// </summary>
    /// <param name="display">Normalized rectangle with the origin at the top-left.</param>
    /// <param name="width">Unrotated page width in points.</param>
    /// <param name="height">Unrotated page height in points.</param>
    /// <param name="rotation">Page <c>/Rotate</c> in degrees.</param>
    public static PdfQuad ToPageQuad(
        PdfSearchRect display,
        double width,
        double height,
        int rotation)
    {
        if (width <= 0 || height <= 0)
        {
            return default;
        }

        var quarters = NormalizeRotation(rotation);
        var displayWidth = quarters is 90 or 270 ? height : width;
        var displayHeight = quarters is 90 or 270 ? width : height;

        var x0 = display.X * displayWidth;
        var y0 = display.Y * displayHeight;
        var x1 = (display.X + display.Width) * displayWidth;
        var y1 = (display.Y + display.Height) * displayHeight;

        var (u0, v0) = UnmapCorner(x0, y0, width, height, quarters);
        var (u1, v1) = UnmapCorner(x1, y1, width, height, quarters);

        // Page space is Y-up, and the display coordinate carries the top edge.
        var top = height - Math.Min(v0, v1);
        var bottom = height - Math.Max(v0, v1);

        return new PdfQuad(
            Math.Min(u0, u1),
            top,
            Math.Max(u0, u1),
            bottom);
    }
    /// <summary>Reduces a <c>/Rotate</c> value to 0, 90, 180, or 270.</summary>
    public static int NormalizeRotation(int rotation)
    {
        var value = ((rotation % 360) + 360) % 360;
        return value switch
        {
            90 or 180 or 270 => value,
            _ => 0
        };
    }

    /// <summary>
    /// Rotates one point clockwise inside the page. The display box is
    /// (height × width) for quarter turns, which is why the mapped coordinates come back in
    /// that box rather than in the caller's.
    /// </summary>
    /// <summary>Inverts <see cref="MapCorner"/>.</summary>
    private static (double U, double V) UnmapCorner(
        double x,
        double y,
        double width,
        double height,
        int quarters) => quarters switch
    {
        90 => (y, height - x),
        180 => (width - x, height - y),
        270 => (width - y, x),
        _ => (x, y)
    };
    private static (double X, double Y) MapCorner(
        double u,
        double v,
        double width,
        double height,
        int quarters) => quarters switch
    {
        90 => (height - v, u),
        180 => (width - u, height - v),
        270 => (v, width - u),
        _ => (u, v)
    };
}
