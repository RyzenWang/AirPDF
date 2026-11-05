namespace AirPdf.App.Controls;

/// <summary>
/// Maps a pointer position to a tab slot while a tab is being dragged.
/// </summary>
/// <remarks>
/// The slots are captured once, when the drag starts, as the right edge of every
/// document tab in content coordinates. Resolving against that fixed geometry is what
/// keeps a single pointer crossing equal to a single reorder.
///
/// The previous version re-measured the live layout on every pointer move. Moving a tab
/// to the right immediately shifted the other tabs left, which put another tab's midpoint
/// behind the pointer, so one drag could reorder the strip several times in a row.
/// </remarks>
public static class TabDragSlots
{
    /// <summary>
    /// Returns the slot index that contains <paramref name="contentX"/>, clamped to the
    /// available slots. A list with a single entry always resolves to slot zero.
    /// </summary>
    public static int Resolve(IReadOnlyList<double> rightEdges, double contentX)
    {
        if (rightEdges.Count == 0)
        {
            return 0;
        }

        for (var slot = 0; slot < rightEdges.Count - 1; slot++)
        {
            if (contentX < rightEdges[slot])
            {
                return slot;
            }
        }

        return rightEdges.Count - 1;
    }

    /// <summary>
    /// Builds the right edges of each slot from a left origin and per-slot widths.
    /// </summary>
    public static List<double> BuildEdges(double left, IReadOnlyList<double> widths)
    {
        var edges = new List<double>(widths.Count);
        var edge = left;
        foreach (var width in widths)
        {
            edge += width;
            edges.Add(edge);
        }

        return edges;
    }
}
