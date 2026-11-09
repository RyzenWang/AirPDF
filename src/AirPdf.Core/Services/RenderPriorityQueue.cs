using SkiaSharp;

namespace AirPdf.Core.Services;

/// <summary>Identifies a render that a newer request can supersede.</summary>
internal readonly record struct RenderRequestKey(
    object Owner,
    string FilePath,
    int PageNumber,
    bool Thumbnail);

/// <summary>
/// A process-wide, priority-ordered render lane. Every PDFium page render still takes the
/// static native gate, but requests wait here first so a visible page can jump ahead of
/// thumbnails that were queued earlier. One worker keeps the native calls serialized.
/// </summary>
internal sealed class RenderPriorityQueue
{
    private readonly PriorityQueue<WorkItem, (int Priority, long Sequence)> _queue = new();
    private readonly Dictionary<RenderRequestKey, WorkItem> _latest = [];
    private readonly SemaphoreSlim _signal = new(0);
    private readonly object _gate = new();
    private long _sequence;
    private Task? _worker;

    public Task<SKBitmap> Enqueue(
        PdfRenderPriority priority,
        RenderRequestKey key,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task<SKBitmap>> work)
    {
        var completion = new TaskCompletionSource<SKBitmap>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        if (cancellationToken.IsCancellationRequested)
        {
            completion.SetCanceled(cancellationToken);
            return completion.Task;
        }

        lock (_gate)
        {
            var sequence = _sequence++;
            var item = new WorkItem(priority, key, cancellationToken, work, completion);

            // A newer request for the same page/kind replaces the older queued request. The
            // old entry stays in the priority queue but is skipped when the worker reaches
            // it, so fast scrolling cannot make stale pages render first.
            _latest[key] = item;
            _queue.Enqueue(item, ((int)priority, sequence));
            _worker ??= Task.Run(WorkerLoop);
        }

        _signal.Release();
        return completion.Task;
    }

    private async Task WorkerLoop()
    {
        while (true)
        {
            await _signal.WaitAsync().ConfigureAwait(false);

            WorkItem? item;
            var superseded = false;
            lock (_gate)
            {
                if (!_queue.TryDequeue(out item, out _) || item is null)
                {
                    continue;
                }

                if (_latest.TryGetValue(item.Key, out var latest))
                {
                    superseded = !ReferenceEquals(latest, item);
                    if (!superseded)
                    {
                        _latest.Remove(item.Key);
                    }
                }
            }

            if (superseded || item.CancellationToken.IsCancellationRequested)
            {
                item.Completion.TrySetCanceled(item.CancellationToken);
                continue;
            }

            try
            {
                var bitmap = await item.Work(item.CancellationToken).ConfigureAwait(false);
                item.Completion.TrySetResult(bitmap);
            }
            catch (OperationCanceledException canceled) when (canceled.CancellationToken == item.CancellationToken)
            {
                item.Completion.TrySetCanceled(item.CancellationToken);
            }
            catch (Exception unexpected)
            {
                item.Completion.TrySetException(unexpected);
            }
        }
    }

    private sealed record WorkItem(
        PdfRenderPriority Priority,
        RenderRequestKey Key,
        CancellationToken CancellationToken,
        Func<CancellationToken, Task<SKBitmap>> Work,
        TaskCompletionSource<SKBitmap> Completion);
}
