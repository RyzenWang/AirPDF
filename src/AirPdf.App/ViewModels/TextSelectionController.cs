using System.Text;
using AirPdf.App.Services;
using AirPdf.Core.Models;

namespace AirPdf.App.ViewModels;

/// <summary>
/// Word-level selection state shared by the view model and the pointer input
/// layer. Selection geometry stays in normalized page coordinates so it can be
/// painted by an overlay at any zoom level.
/// </summary>
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

        var wordIndex = page.HitTestWord(x, y);
        if (wordIndex < 0)
        {
            return false;
        }

        Reset();

        if (clickCount >= 3)
        {
            _anchor = new TextPosition(pageNumber, 0);
            _focus = new TextPosition(pageNumber, page.Words.Count - 1);
            Apply();
            return true;
        }

        _anchor = new TextPosition(pageNumber, wordIndex);
        _focus = clickCount >= 2 ? _anchor : null;
        if (_focus is not null)
        {
            Apply();
        }

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
        var wordIndex = page.HitTestWord(x, y);
        if (wordIndex < 0)
        {
            wordIndex = page.NearestWord(x, y);
        }

        if (wordIndex < 0)
        {
            return false;
        }

        _focus = new TextPosition(pageNumber, wordIndex);
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
        _anchor = new TextPosition(first.Number, 0);
        _focus = new TextPosition(last.Number, last.Words.Count - 1);
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
        var start = anchor;
        var end = focus;
        if (start.PageNumber > end.PageNumber ||
            (start.PageNumber == end.PageNumber && start.WordIndex > end.WordIndex))
        {
            (start, end) = (end, start);
        }

        var selectedPages = new HashSet<int>();
        foreach (var page in pages)
        {
            var inRange = page.Number >= start.PageNumber && page.Number <= end.PageNumber;
            if (!inRange)
            {
                if (_selectedPages.Contains(page.Number))
                {
                    page.ClearSelectionRects();
                }

                continue;
            }

            if (page.Words.Count == 0)
            {
                if (_selectedPages.Contains(page.Number))
                {
                    page.ClearSelectionRects();
                }

                continue;
            }

            var from = page.Number == start.PageNumber ? start.WordIndex : 0;
            var to = page.Number == end.PageNumber ? end.WordIndex : page.Words.Count - 1;
            from = Math.Clamp(from, 0, page.Words.Count - 1);
            to = Math.Clamp(to, 0, page.Words.Count - 1);
            if (from > to)
            {
                (from, to) = (to, from);
            }

            var rects = new List<PdfSearchRect>(to - from + 1);
            for (var i = from; i <= to; i++)
            {
                rects.Add(page.Words[i].Bounds);
            }

            page.SetSelectionRects(rects);
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
        PdfTextWord? previous = null;
        var previousPage = 0;

        foreach (var page in pages)
        {
            if (page.Number < start.PageNumber ||
                page.Number > end.PageNumber ||
                page.Words.Count == 0)
            {
                continue;
            }

            var from = page.Number == start.PageNumber ? start.WordIndex : 0;
            var to = page.Number == end.PageNumber ? end.WordIndex : page.Words.Count - 1;
            from = Math.Clamp(from, 0, page.Words.Count - 1);
            to = Math.Clamp(to, 0, page.Words.Count - 1);
            if (from > to)
            {
                (from, to) = (to, from);
            }

            for (var i = from; i <= to; i++)
            {
                var word = page.Words[i];
                if (builder.Length > 0)
                {
                    if (page.Number != previousPage || IsNewLine(previous, word))
                    {
                        builder.Append('\n');
                    }
                    else if (NeedsSpace(previous?.Text, word.Text))
                    {
                        builder.Append(' ');
                    }
                }

                builder.Append(word.Text);
                previous = word;
                previousPage = page.Number;
            }
        }

        return builder.ToString();
    }

    private static bool IsNewLine(PdfTextWord? previous, PdfTextWord current)
    {
        if (previous is null)
        {
            return false;
        }

        var previousCenter = previous.Bounds.Y + (previous.Bounds.Height / 2);
        var currentCenter = current.Bounds.Y + (current.Bounds.Height / 2);
        var threshold = Math.Max(previous.Bounds.Height, current.Bounds.Height) * 0.65;
        return Math.Abs(previousCenter - currentCenter) > threshold ||
               current.Bounds.X + current.Bounds.Width < previous.Bounds.X + 0.002;
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

    private readonly record struct TextPosition(int PageNumber, int WordIndex);

    private readonly record struct PendingSelection(int PageNumber, double X, double Y);
}
