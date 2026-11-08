namespace AirPdf.App.ViewModels;

/// <summary>
/// A position in a page's text layer: which word, and which character inside that word.
/// </summary>
/// <remarks>
/// Selection anchors on characters, so a word alone is not precise enough: two clicks in the
/// same word have to be distinguishable.
/// </remarks>
public readonly record struct TextHit(int WordIndex, int CharIndex)
{
    /// <summary>A point that landed on no text.</summary>
    public static TextHit Miss => new(-1, -1);

    public bool IsHit => WordIndex >= 0 && CharIndex >= 0;
}
