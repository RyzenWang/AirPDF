using AirPdf.Core.Services;
using SkiaSharp;
using Xunit;

namespace AirPdf.Core.Tests;

public sealed class RenderPriorityQueueTests
{
    [Fact]
    public async Task NewerRequestForTheSameKeySupersedesQueuedWork()
    {
        var queue = new RenderPriorityQueue();
        var blockerStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseBlocker = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var blocker = queue.Enqueue(
            PdfRenderPriority.Visible,
            new RenderRequestKey(new object(), "blocker.pdf", 0, false),
            default,
            async _ =>
            {
                blockerStarted.SetResult(true);
                await releaseBlocker.Task;
                return new SKBitmap(1, 1);
            });
        await blockerStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var key = new RenderRequestKey(new object(), "document.pdf", 1, false);
        var calls = 0;

        var old = queue.Enqueue(
            PdfRenderPriority.Visible,
            key,
            default,
            _ =>
            {
                Interlocked.Increment(ref calls);
                return Task.FromResult(new SKBitmap(1, 1));
            });
        var newest = queue.Enqueue(
            PdfRenderPriority.Visible,
            key,
            default,
            _ =>
            {
                Interlocked.Increment(ref calls);
                return Task.FromResult(new SKBitmap(1, 1));
            });

        releaseBlocker.SetResult(true);

        using var result = await newest.WaitAsync(TimeSpan.FromSeconds(2));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await old);

        Assert.Equal(1, calls);

        using var blockerBitmap = await blocker.WaitAsync(TimeSpan.FromSeconds(2));
    }
}
