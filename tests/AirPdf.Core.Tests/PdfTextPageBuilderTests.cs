using AirPdf.Core.Models;
using AirPdf.Core.Pdfium;
using Xunit;

namespace AirPdf.Core.Tests;

/// <summary>
/// The builder turns PDFium's per-character stream into words. Selection is character level,
/// so every word has to keep the box of each character it was built from: without them a drag
/// can only snap out to word edges.
/// </summary>
public sealed class PdfTextPageBuilderTests
{
    private const double PageWidth = 100;
    private const double PageHeight = 100;
    private const double GlyphWidth = 10;
    private const double Bottom = 20;
    private const double Top = 30;
    private const double FontSize = 10;

    [Fact]
    public void Characters_AreGroupedIntoWordsAndKeepTheirOrder()
    {
        var page = Build(
            Glyph("a", 10),
            Glyph("b", 20),
            new PdfTextChar(" ", 30, Bottom, 34, Top, FontSize),
            Glyph("c", 40),
            Glyph("d", 50));

        Assert.Equal(2, page.Words.Count);
        Assert.Equal("ab", page.Words[0].Text);
        Assert.Equal("cd", page.Words[1].Text);
        Assert.Equal(["a", "b"], page.Words[0].Chars.Select(character => character.Text));
        Assert.Equal(["c", "d"], page.Words[1].Chars.Select(character => character.Text));
        Assert.Equal(2, page.Words[0].CharCount);
    }

    [Fact]
    public void CharacterBoxes_CarryTheMappedGeometry()
    {
        var word = Build(Glyph("a", 10), Glyph("b", 20)).Words[0];

        // Page space y 20..30 becomes display y 0.7..0.8 after the Y flip.
        AssertRect(new PdfSearchRect(0.1, 0.7, 0.1, 0.1), word.Chars[0].Bounds);
        AssertRect(new PdfSearchRect(0.2, 0.7, 0.1, 0.1), word.Chars[1].Bounds);

        // The word box stays the union of its characters.
        AssertRect(new PdfSearchRect(0.1, 0.7, 0.2, 0.1), word.Bounds);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void CharacterBoxes_StayInsideTheWordBoxOnEveryRotation(int rotation)
    {
        var word = BuildRotated(rotation, Glyph("a", 10), Glyph("b", 20)).Words[0];

        Assert.Equal("ab", string.Concat(word.Chars.Select(character => character.Text)));

        var union = word.Chars
            .Select(character => character.Bounds)
            .Aggregate(Union);

        AssertRect(word.Bounds, union);
    }

    [Fact]
    public void CharacterRange_ReadsTheWordText()
    {
        var word = Build(Glyph("a", 10), Glyph("b", 20), Glyph("c", 30)).Words[0];

        Assert.Equal("b", word.GetText(1, 1));
        Assert.Equal("bc", word.GetText(1, 2));
        Assert.Equal("abc", word.GetText(0, 2));

        // A stale range is clamped instead of throwing.
        Assert.Equal("abc", word.GetText(-4, 99));
    }

    [Fact]
    public void AWordWithoutCharacterBoxes_FallsBackToItsText()
    {
        var word = new PdfTextWord
        {
            Index = 0,
            Text = "plain",
            Bounds = new PdfSearchRect(0, 0, 0.1, 0.1)
        };

        Assert.Equal(0, word.CharCount);
        Assert.Equal("plain", word.GetText(0, 0));
        Assert.Equal("plain", word.GetText(2, 3));
    }

    [Fact]
    public void WhitespaceOnlyText_ProducesNoWords()
    {
        var page = Build(
            new PdfTextChar(" ", 10, Bottom, 14, Top, FontSize),
            new PdfTextChar("\n", 20, Bottom, 24, Top, FontSize));

        Assert.Empty(page.Words);
    }

    private static PdfTextPage Build(params PdfTextChar[] chars) => BuildRotated(0, chars);

    private static PdfTextPage BuildRotated(int rotation, params PdfTextChar[] chars) =>
        PdfTextPageBuilder.Build(1, chars, PageWidth, PageHeight, rotation);

    private static PdfTextChar Glyph(string text, double left) =>
        new(text, left, Bottom, left + GlyphWidth, Top, FontSize);

    private static PdfSearchRect Union(PdfSearchRect left, PdfSearchRect right)
    {
        var x = Math.Min(left.X, right.X);
        var y = Math.Min(left.Y, right.Y);
        var rightEdge = Math.Max(left.X + left.Width, right.X + right.Width);
        var bottomEdge = Math.Max(left.Y + left.Height, right.Y + right.Height);
        return new PdfSearchRect(x, y, rightEdge - x, bottomEdge - y);
    }

    private static void AssertRect(PdfSearchRect expected, PdfSearchRect actual)
    {
        Assert.Equal(expected.X, actual.X, 6);
        Assert.Equal(expected.Y, actual.Y, 6);
        Assert.Equal(expected.Width, actual.Width, 6);
        Assert.Equal(expected.Height, actual.Height, 6);
    }
}
