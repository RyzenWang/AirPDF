using AirPdf.App.Controls;
using Xunit;

namespace AirPdf.App.Tests;

/// <summary>
/// Covers the geometry behind dragging a tab: one boundary crossing must equal one
/// reorder, which is what the previous live-measured implementation got wrong.
/// </summary>
public sealed class TabDragSlotsTests
{
    [Fact]
    public void Resolve_ReturnsTheSlotContainingThePointer()
    {
        // Three 100px tabs starting at 0: slot 0 is 0..100, slot 1 is 100..200, slot 2 is 200..300.
        var edges = TabDragSlots.BuildEdges(0, [100, 100, 100]);

        Assert.Equal(0, TabDragSlots.Resolve(edges, 0));
        Assert.Equal(0, TabDragSlots.Resolve(edges, 99));
        Assert.Equal(1, TabDragSlots.Resolve(edges, 100));
        Assert.Equal(1, TabDragSlots.Resolve(edges, 199));
        Assert.Equal(2, TabDragSlots.Resolve(edges, 200));
        Assert.Equal(2, TabDragSlots.Resolve(edges, 500));
    }

    [Fact]
    public void Resolve_ClampsBeforeTheFirstSlot()
    {
        var edges = TabDragSlots.BuildEdges(40, [100, 100]);

        Assert.Equal(0, TabDragSlots.Resolve(edges, 0));
        Assert.Equal(0, TabDragSlots.Resolve(edges, -50));
    }

    [Fact]
    public void Resolve_MovesOneSlotPerBoundaryCrossed()
    {
        var edges = TabDragSlots.BuildEdges(0, [120, 80, 100, 100]);
        var visited = new List<int>();

        // Sweep the pointer across the strip the way a drag does.
        for (var x = 0; x <= 400; x += 4)
        {
            var slot = TabDragSlots.Resolve(edges, x);
            if (visited.Count == 0 || visited[^1] != slot)
            {
                visited.Add(slot);
            }
        }

        Assert.Equal([0, 1, 2, 3], visited);
    }

    [Fact]
    public void Resolve_ReturnsZeroWithoutSlots()
    {
        Assert.Equal(0, TabDragSlots.Resolve([], 500));
    }

    [Fact]
    public void Resolve_NeverExceedsTheLastSlot()
    {
        var edges = TabDragSlots.BuildEdges(0, [50]);

        Assert.Equal(0, TabDragSlots.Resolve(edges, 10_000));
    }

    [Fact]
    public void BuildEdges_AccumulatesWidthsFromTheLeft()
    {
        var edges = TabDragSlots.BuildEdges(20, [10, 30, 5]);

        Assert.Equal([30, 60, 65], edges);
    }
}
