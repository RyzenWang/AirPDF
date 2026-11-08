using System.Text;
using AirPdf.App.Services;
using AirPdf.Core.Models;

namespace AirPdf.App.ViewModels;

/// <summary>
/// Character-level selection state shared by the view model and the pointer input layer.
/// Selection geometry stays in normalized page coordinates so it can be painted by an overlay
/// at any zoom level.
/// </summary>
/// <remarks>
/// Selection used to anchor on whole words, because the text layer only exposed word boxes:
/// dragging across a line selected "the first word you touched up to the last word you touched",
/// so copying a phrase always brought its neighbours along. PDFium already reports characters,
/// and <see cref="PdfTextPageBuilder"/> now keeps their boxes, so a selection is stored as
/// (word, character) positions and painted from the character boxes they cover.
///
/// Words still matter: they carry the reading order, and the rule that decides whether two
/// selected words are separated by a space or a newline when the text is copied.
/// </remarks>
public sealed class TextSelectionController
{
    private readonly Func<IReadOnlyList<PageViewModel>> _pages;
    private readonly Func<TextLayerLoader?> _textLoader;
    private readonly HashSet<int> _selectedPages = [];
    private TextPosition? _anchor;
    private TextPosition? _focus;
    private PendingSelection? _pending;
    private string _selectedText = string.Empty;

    public TextSelectionController(
        Func<IReadOnlyList<PageViewModel>> pages,
        Func<TextLayerLoader?> textLoader)
    {
        _pages = pages;
        _textLoader = textLoader;
    }

    public event EventHandler? SelectionChanged;

    public bool HasSelection => _selectedText.Length > 0;

    /// <summary>True when this page is part of the current selection.</summary>
    public bool IsPageSelected(int pageNumber) => _selectedPages.Contains(pageNumber);

    public string SelectedText => _selectedText;

    public async Task<bool> BeginAsync(int pageNumber, double x, double y, int clickCount)
    {
        var page = FindPage(pageNumber);
        if (page is null)
        {
            return false;
        }

        if (!page.HasTextLayer)
        {
            var loader = _textLoader();
            if (loader is null)
            {
                return false;
            }

            await loader.EnsurePageAsync(pageNumber).ConfigureAwait(true);
            if (!ReferenceEquals(_textLoader(), loader))
            {
                return false;
            }

            page = FindPage(pageNumber);
            if (page is null || !page.HasTextLayer)
            {
                return false;
            }
        }

        var hit = page.HitTestChar(x, y);
        if (!hit.IsHit)
        {
            return false;
        }

        Reset();

        if (clickCount >= 3)
        {
            _anchor = new TextPosition(pageNumber, 0, 0);
            _focus = LastPosition(page);
            Apply();
            return true;
        }

        if (clickCount >= 2)
        {
            // A double click is still a word: that is the one gesture whose meaning the reader
            // expects to stay coarse.
            _anchor = new TextPosition(pageNumber, hit.WordIndex, 0);
            _focus = new TextPosition(pageNumber, hit.WordIndex, LastChar(page.Words[hit.WordIndex]));
            Apply();
            return true;
        }

        _anchor = new TextPosition(pageNumber, hit.WordIndex, hit.CharIndex);
        _focus = null;

        return true;
    }

    public bool Update(int pageNumber, double x, double y)
    {
        if (_anchor is null)
        {
            return false;
        }

        var page = FindPage(pageNumber);
        if (page is null)
        {
            return false;
        }

        if (!page.HasTextLayer)
        {
            _pending = new PendingSelection(pageNumber, x, y);
            _textLoader()?.Request(pageNumber);
            return false;
        }

        _pending = null;
        var hit = page.HitTestChar(x, y);
        if (!hit.IsHit)
        {
            hit = page.NearestChar(x, y);
        }

        if (!hit.IsHit)
        {
            return false;
        }

        _focus = new TextPosition(pageNumber, hit.WordIndex, hit.CharIndex);
        Apply();
        return true;
    }

    public void Complete()
    {
        if (_focus is null)
        {
            Clear();
        }
    }

    public void Clear()
    {
        _anchor = null;
        _focus = null;
        _pending = null;
        ResetVisuals();
    }

    public async Task SelectAllAsync()
    {
        var pages = _pages();
        if (pages.Count == 0)
        {
            return;
        }

        Clear();
        var loader = _textLoader();
        if (loader is not null)
        {
            await loader.EnsureAllAsync().ConfigureAwait(true);
            if (!ReferenceEquals(_textLoader(), loader))
            {
                return;
            }
        }

        pages = _pages();
        var first = pages.FirstOrDefault(page => page.HasTextLayer);
        var last = pages.LastOrDefault(page => page.HasTextLayer);
        if (first is null || last is null)
        {
            Clear();
            return;
        }

        _pending = null;
        _anchor = new TextPosition(first.Number, 0, 0);
        _focus = LastPosition(last);
        Apply();
    }

    /// <summary>
    /// Called after a lazily loaded page becomes available. If the pointer last
    /// hovered over that page while it was still loading, retry the hit test.
    /// </summary>
    public void OnTextLayerLoaded(int pageNumber)
    {
        if (_pending is { } pending &&
            pending.PageNumber == pageNumber &&
            _anchor is not null)
        {
            _pending = null;
            Update(pending.PageNumber, pending.X, pending.Y);
            return;
        }

        if (_anchor is not null)
        {
            Apply();
        }
    }

    private PageViewModel? FindPage(int pageNumber)
    {
        var pages = _pages();
        if (pageNumber >= 1 && pageNumber <= pages.Count)
        {
            var page = pages[pageNumber - 1];
            if (page.Number == pageNumber)
            {
                return page;
            }
        }

        foreach (var page in pages)
        {
            if (page.Number == pageNumber)
            {
                return page;
            }
        }

        return null;
    }

    private void Apply()
    {
        if (_anchor is not { } anchor || _focus is not { } focus)
        {
            return;
        }

        var pages = _pages();
        var (start, end) = Order(anchor, focus);

        var selectedPages = new HashSet<int>();
        foreach (var page in pages)
        {
            var inRange = page.Number >= start.PageNumber && page.Number <= end.PageNumber;
            if (!inRange || page.Words.Count == 0)
            {
                if (_selectedPages.Contains(page.Number))
                {
                    page.ClearSelectionRects();
                }

                continue;
            }

            var from = Clamp(page, page.Number == start.PageNumber ? start : StartPosition(page));
            var to = Clamp(page, page.Number == end.PageNumber ? end : LastPosition(page));
            if (Compare(from, to) > 0)
            {
                (from, to) = (to, from);
            }

            page.SetSelectionRects(BuildRects(page, from, to));
            selectedPages.Add(page.Number);
        }

        _selectedPages.Clear();
        foreach (var pageNumber in selectedPages)
        {
            _selectedPages.Add(pageNumber);
        }

        _selectedText = BuildSelectedText(start, end, pages);
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Paints a character range as one rectangle per run of characters instead of one per
    /// character: a rectangle per letter outlines every glyph, and a saved highlight stores one
    /// quad per rectangle, so the run is also what keeps a highlight from exploding in size.
    /// </summary>
    private static List<PdfSearchRect> BuildRects(
        PageViewModel page,
        TextPosition from,
        TextPosition to)
    {
        var rects = new List<PdfSearchRect>(to.WordIndex - from.WordIndex + 1);
        for (var index = from.WordIndex; index <= to.WordIndex; index++)
        {
            var word = page.Words[index];
            AppendWordRects(
                rects,
                word,
                index == from.WordIndex ? from.CharIndex : 0,
                index == to.WordIndex ? to.CharIndex : word.CharCount - 1);
        }

        return rects;
    }

    private static void AppendWordRects(
        List<PdfSearchRect> rects,
        PdfTextWord word,
        int from,
        int to)
    {
        if (word.CharCount == 0)
        {
            rects.Add(word.Bounds);
            return;
        }

        from = Math.Clamp(from, 0, word.CharCount - 1);
        to = Math.Clamp(to, 0, word.CharCount - 1);
        if (from > to)
        {
            (from, to) = (to, from);
        }

        var run = word.Chars[from].Bounds;
        for (var index = from + 1; index <= to; index++)
        {
            var next = word.Chars[index].Bounds;
            if (Joins(run, next))
            {
                run = Union(run, next);
            }
            else
            {
                rects.Add(run);
                run = next;
            }
        }

        rects.Add(run);
    }

    /// <summary>
    /// True when two character boxes belong to the same run of text: they sit on the same line
    /// and the gap between them is kerning rather than a break.
    /// </summary>
    private static bool Joins(PdfSearchRect run, PdfSearchRect next)
    {
        var verticalOverlap =
            Math.Min(run.Y + run.Height, next.Y + next.Height) - Math.Max(run.Y, next.Y);
        if (verticalOverlap < Math.Min(run.Height, next.Height) * 0.5)
        {
            return false;
        }

        var gap = next.X - (run.X + run.Width);
        return gap <= Math.Max(0.004, run.Height * 0.35) &&
               gap >= -Math.Max(0.004, next.Width);
    }

    private static PdfSearchRect Union(PdfSearchRect left, PdfSearchRect right)
    {
        var x = Math.Min(left.X, right.X);
        var y = Math.Min(left.Y, right.Y);
        var rightEdge = Math.Max(left.X + left.Width, right.X + right.Width);
        var bottomEdge = Math.Max(left.Y + left.Height, right.Y + right.Height);
        return new PdfSearchRect(x, y, rightEdge - x, bottomEdge - y);
    }

    private void Reset()
    {
        _pending = null;
        ResetVisuals();
    }

    private void ResetVisuals()
    {
        foreach (var page in _pages())
        {
            page.ClearSelectionRects();
        }

        _selectedPages.Clear();
        _selectedText = string.Empty;
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private static string BuildSelectedText(
        TextPosition start,
        TextPosition end,
        IReadOnlyList<PageViewModel> pages)
    {
        var builder = new StringBuilder();
        string? previousText = null;
        PdfSearchRect previousBounds = default;
        var previousPage = 0;

        foreach (var page in pages)
        {
            if (page.Number < start.PageNumber ||
                page.Number > end.PageNumber ||
                page.Words.Count == 0)
            {
                continue;
            }

            var from = Clamp(page, page.Number == start.PageNumber ? start : StartPosition(page));
            var to = Clamp(page, page.Number == end.PageNumber ? end : LastPosition(page));
            if (Compare(from, to) > 0)
            {
                (from, to) = (to, from);
            }

            for (var index = from.WordIndex; index <= to.WordIndex; index++)
            {
                var word = page.Words[index];
                var text = word.GetText(
                    index == from.WordIndex ? from.CharIndex : 0,
                    index == to.WordIndex ? to.CharIndex : word.CharCount - 1);
                if (text.Length == 0)
                {
                    continue;
                }

                if (builder.Length > 0)
                {
                    if (page.Number != previousPage || IsNewLine(previousBounds, word.Bounds))
                    {
                        builder.Append('\n');
                    }
                    else if (NeedsSpace(previousText, text))
                    {
                        builder.Append(' ');
                    }
                }

                builder.Append(text);
                previousText = text;
                previousBounds = word.Bounds;
                previousPage = page.Number;
            }
        }

        return builder.ToString();
    }

    private static bool IsNewLine(PdfSearchRect previous, PdfSearchRect current)
    {
        var previousCenter = previous.Y + (previous.Height / 2);
        var currentCenter = current.Y + (current.Height / 2);
        var threshold = Math.Max(previous.Height, current.Height) * 0.65;
        return Math.Abs(previousCenter - currentCenter) > threshold ||
               current.X + current.Width < previous.X + 0.002;
    }

    private static bool NeedsSpace(string? previous, string current)
    {
        if (string.IsNullOrEmpty(previous) || string.IsNullOrEmpty(current))
        {
            return false;
        }

        var left = previous[^1];
        var right = current[0];
        return !(IsCjk(left) && IsCjk(right));
    }

    private static bool IsCjk(char value) =>
        value is >= '\u2E80' and <= '\u9FFF' or >= '\uF900' and <= '\uFAFF';

    private static TextPosition StartPosition(PageViewModel page) =>
        new(page.Number, 0, 0);

    private static TextPosition LastPosition(PageViewModel page) => new(
        page.Number,
        page.Words.Count - 1,
        LastChar(page.Words[^1]));

    private static int LastChar(PdfTextWord word) => Math.Max(0, word.CharCount - 1);

    /// <summary>Keeps a stored position inside a page whose text layer was reloaded.</summary>
    private static TextPosition Clamp(PageViewModel page, TextPosition position)
    {
        var word = Math.Clamp(position.WordIndex, 0, page.Words.Count - 1);
        var count = page.Words[word].CharCount;
        var character = count == 0 ? 0 : Math.Clamp(position.CharIndex, 0, count - 1);
        return new TextPosition(page.Number, word, character);
    }

    private static (TextPosition Start, TextPosition End) Order(TextPosition anchor, TextPosition focus) =>
        Compare(anchor, focus) <= 0 ? (anchor, focus) : (focus, anchor);

    private static int Compare(TextPosition left, TextPosition right)
    {
        if (left.PageNumber != right.PageNumber)
        {
            return left.PageNumber.CompareTo(right.PageNumber);
        }

        if (left.WordIndex != right.WordIndex)
        {
            return left.WordIndex.CompareTo(right.WordIndex);
        }

        return left.CharIndex.CompareTo(right.CharIndex);
    }

    private readonly record struct TextPosition(int PageNumber, int WordIndex, int CharIndex);

    private readonly record struct PendingSelection(int PageNumber, double X, double Y);
}
