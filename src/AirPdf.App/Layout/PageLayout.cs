namespace AirPdf.App.Layout;

/// <summary>
/// Precomputed vertical layout for the continuous page canvas. Page tops are
/// built once per zoom/document change so scroll handling can use a binary
/// search instead of walking every page.
/// </summary>
public sealed class PageLayout
{
    public const double TopPadding = 24.0;
    public const double PageGap = 28.0;

    private readonly double[] _tops;
    private readonly double[] _heights;
    private readonly double[] _bottoms;

    public PageLayout(IReadOnlyList<double> pageHeights)
    {
        ArgumentNullException.ThrowIfNull(pageHeights);

        var count = pageHeights.Count;
        _tops = new double[count];
        _heights = new double[count];
        _bottoms = new double[count];

        var top = TopPadding;
        for (var i = 0; i < count; i++)
        {
            var height = Math.Max(1, pageHeights[i]);
            _tops[i] = top;
            _heights[i] = height;
            _bottoms[i] = top + height;
            top = _bottoms[i] + PageGap;
        }
    }

    public static PageLayout Empty { get; } = new(Array.Empty<double>());

    public int Count => _tops.Length;

    public double GetTop(int pageNumber) => _tops[ToIndex(pageNumber)];

    public double GetHeight(int pageNumber) => _heights[ToIndex(pageNumber)];

    public double GetBottom(int pageNumber) => _bottoms[ToIndex(pageNumber)];

    /// <summary>
    /// Returns the page with the greatest overlap with the viewport. Binary
    /// search bounds the scan to the pages intersecting the viewport, plus one
    /// neighbour on each side for gap and tie handling.
    /// </summary>
    public int GetPageAtViewportTop(double offset, double viewportHeight)
    {
        if (Count == 0)
        {
            return 1;
        }

        if (viewportHeight <= 0)
        {
            return 1;
        }

        var viewportBottom = offset + viewportHeight;
        var index = FindLastPageStartingAtOrBefore(offset);
        var bottomIndex = FindLastPageStartingAtOrBefore(viewportBottom);
        var first = Math.Max(0, index - 1);
        var last = Math.Min(Count - 1, bottomIndex + 1);
        var bestPage = first;
        var bestOverlap = double.NegativeInfinity;

        for (var i = first; i <= last; i++)
        {
            var overlap = Math.Min(_bottoms[i], viewportBottom) - Math.Max(_tops[i], offset);
            if (overlap > bestOverlap)
            {
                bestOverlap = overlap;
                bestPage = i;
            }
        }

        return bestPage + 1;
    }

    /// <summary>Normalized offset within a page, clamped to 0..1.</summary>
    public double GetPageOffset(int pageNumber, double scrollOffset)
    {
        var index = ToIndex(pageNumber);
        return Math.Clamp((scrollOffset - _tops[index]) / Math.Max(1, _heights[index]), 0, 1);
    }

    private int FindLastPageStartingAtOrBefore(double offset)
    {
        var low = 0;
        var high = Count - 1;
        var result = 0;

        while (low <= high)
        {
            var middle = low + ((high - low) / 2);
            if (_tops[middle] <= offset)
            {
                result = middle;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        return result;
    }

    private int ToIndex(int pageNumber)
    {
        if (pageNumber < 1 || pageNumber > Count)
        {
            throw new ArgumentOutOfRangeException(nameof(pageNumber));
        }

        return pageNumber - 1;
    }
}
