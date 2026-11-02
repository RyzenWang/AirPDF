using System.Text;

namespace AirPdf.Core.Tests;

/// <summary>
/// Builds a one-page PDF rotated by 90, 180, or 270 degrees, with a filled black block, some
/// extractable Helvetica text, and a link annotation over the block.
/// </summary>
/// <remarks>
/// The fixture is written by hand so the page geometry is exactly known: rendering it and
/// checking which pixels a computed rectangle covers is the only way to prove the
/// page-space to display-space transform lines up with what PDFium draws.
/// </remarks>
internal static class RotatedPdfFixture
{
    public const string LinkUri = "https://example.com/rotated";

    public const string Word = "ROTATED";

    /// <summary>Unrotated page box, in points.</summary>
    public const double PageWidth = 200;

    public const double PageHeight = 300;

    /// <summary>The black block, in unrotated page space (1/72 inch, origin bottom-left).</summary>
    public const double BlockLeft = 20;

    public const double BlockTop = 270;

    public const double BlockRight = 120;

    public const double BlockBottom = 240;

    public static byte[] Create(int rotation)
    {
        var content =
            "q 0 0 0 rg 20 240 100 30 re f Q\n" +
            "BT /F1 24 Tf 20 100 Td (ROTATED) Tj ET\n";

        var objects = new List<string>
        {
            // 1 catalog, 2 pages, 3 page, 4 font, 5 link annotation, 6 content stream
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 300] " +
                $"/Rotate {rotation} /Annots [5 0 R] /Contents 6 0 R " +
                "/Resources << /Font << /F1 4 0 R >> >> >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
            "<< /Type /Annot /Subtype /Link /Rect [20 240 120 270] /Border [0 0 0] " +
                $"/A << /S /URI /URI ({LinkUri}) >> >>",
            $"<< /Length {content.Length} >>\nstream\n{content}endstream"
        };

        var builder = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();

        for (var i = 0; i < objects.Count; i++)
        {
            offsets.Add(builder.Length);
            builder.Append(i + 1).Append(" 0 obj\n").Append(objects[i]).Append("\nendobj\n");
        }

        var xrefOffset = builder.Length;
        builder.Append("xref\n0 ").Append(objects.Count + 1).Append('\n');
        builder.Append("0000000000 65535 f \n");

        foreach (var offset in offsets)
        {
            builder.Append(offset.ToString("D10")).Append(" 00000 n \n");
        }

        builder
            .Append("trailer\n<< /Size ")
            .Append(objects.Count + 1)
            .Append(" /Root 1 0 R >>\nstartxref\n")
            .Append(xrefOffset)
            .Append("\n%%EOF\n");

        return Encoding.ASCII.GetBytes(builder.ToString());
    }
}
