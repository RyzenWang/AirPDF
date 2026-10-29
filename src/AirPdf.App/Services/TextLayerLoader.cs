using AirPdf.Core.Models;
using AirPdf.Core.Services;

namespace AirPdf.App.Services;

/// <summary>
/// Loads PdfPig text layers on demand. Opening a document no longer parses the
/// whole text layer; pages are requested as they become visible or are needed
/// for a selection, and pending requests are batched into one PdfPig open.
/// </summary>
public sealed class TextLayerLoader : IDisposable
{
    private readonly PdfTextService _service;
    private readonly string _filePath;
    private readonly string? _password;
    private readonly int _pageCount;
    private readonly Func<PdfTextPage, Task> _onPageLoaded;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly object _gate = new();
    private readonly HashSet<int> _loaded = [];
    private readonly HashSet<int> _queued = [];
    private readonly List<int> _pending = [];
    private readonly Dictionary<int, TaskCompletionSource<bool>> _waiters = [];
    private bool _running;
    private bool _disposed;

    public TextLayerLoader(
        PdfTextService service,
        string filePath,
        string? password,
        int pageCount,
        Func<PdfTextPage, Task> onPageLoaded)
    {
        _service = service;
        _filePath = filePath;
        _password = password;
        _pageCount = pageCount;
        _onPageLoaded = onPageLoaded;
    }

    public bool IsLoaded(int pageNumber)
    {
        lock (_gate)
        {
            return _loaded.Contains(pageNumber);
        }
    }

    public int LoadedCount
    {
        get
        {
            lock (_gate)
            {
                return _loaded.Count;
            }
        }
    }

    /// <summary>Queues a page and its neighbours without waiting.</summary>
    public void Request(int pageNumber, int radius = 1)
    {
        if (_disposed || _pageCount <= 0)
        {
            return;
        }

        var start = false;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            var first = Math.Max(1, pageNumber - radius);
            var last = Math.Min(_pageCount, pageNumber + radius);
            for (var page = first; page <= last; page++)
            {
                QueueLocked(page);
            }

            start = TryStartLocked();
        }

        if (start)
        {
            _ = RunAsync();
        }
    }

    /// <summary>
    /// Ensures a page has been parsed. The caller can await this before
    /// hit-testing a click that may arrive before the page is visible.
    /// </summary>
    public Task<bool> EnsurePageAsync(int pageNumber)
    {
        TaskCompletionSource<bool> waiter;
        var start = false;

        lock (_gate)
        {
            if (_disposed || pageNumber < 1 || pageNumber > _pageCount)
            {
                return Task.FromResult(false);
            }

            if (_loaded.Contains(pageNumber))
            {
                return Task.FromResult(true);
            }

            if (!_waiters.TryGetValue(pageNumber, out var existing))
            {
                existing = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _waiters[pageNumber] = existing;
            }

            waiter = existing!;

            QueueLocked(pageNumber);
            start = TryStartLocked();
        }

        if (start)
        {
            _ = RunAsync();
        }

        return waiter.Task;
    }

    public async Task<bool> EnsureAllAsync()
    {
        if (_pageCount <= 0)
        {
            return false;
        }

        var tasks = new Task<bool>[_pageCount];
        for (var page = 1; page <= _pageCount; page++)
        {
            tasks[page - 1] = EnsurePageAsync(page);
        }

        var results = await Task.WhenAll(tasks).ConfigureAwait(true);
        return results.All(result => result);
    }

    public void Dispose()
    {
        TaskCompletionSource<bool>[] waiters;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _pending.Clear();
            waiters = _waiters.Values.ToArray();
            _waiters.Clear();
        }

        try
        {
            _cancellation.Cancel();
        }
        catch
        {
            // The loader has already shut down, or a cancellation callback failed.
        }

        foreach (var waiter in waiters)
        {
            waiter.TrySetResult(false);
        }
    }

    private async Task RunAsync()
    {
        try
        {
            while (true)
            {
                int[] batch;
                lock (_gate)
                {
                    if (_disposed || _pending.Count == 0)
                    {
                        _running = false;
                        return;
                    }

                    batch = _pending.ToArray();
                    _pending.Clear();
                }

                IReadOnlyList<PdfTextPage> pages;
                try
                {
                    pages = await _service
                        .ExtractPagesAsync(_filePath, batch, _password, _cancellation.Token)
                        .ConfigureAwait(true);
                }
                catch (OperationCanceledException)
                {
                    CompleteWaiters(batch, false);
                    return;
                }
                catch
                {
                    CompleteWaiters(batch, false);
                    continue;
                }

                if (_disposed)
                {
                    return;
                }

                var returned = new HashSet<int>();
                foreach (var page in pages)
                {
                    returned.Add(page.PageNumber);
                    TaskCompletionSource<bool>? waiter = null;
                    lock (_gate)
                    {
                        _loaded.Add(page.PageNumber);
                        _queued.Remove(page.PageNumber);
                        _waiters.Remove(page.PageNumber, out waiter);
                    }

                    try
                    {
                        await _onPageLoaded(page).ConfigureAwait(true);
                    }
                    catch
                    {
                        // A page callback must not stop the remaining batch.
                    }

                    // Complete waiters after the page view model has the text,
                    // so an awaited EnsurePageAsync can safely hit-test.
                    waiter?.TrySetResult(true);
                }

                foreach (var pageNumber in batch)
                {
                    if (!returned.Contains(pageNumber))
                    {
                        CompleteWaiters(new[] { pageNumber }, false);
                    }
                }
            }
        }
        finally
        {
            lock (_gate)
            {
                _running = false;
            }
        }
    }

    private void QueueLocked(int pageNumber)
    {
        if (pageNumber < 1 || pageNumber > _pageCount ||
            _loaded.Contains(pageNumber) ||
            !_queued.Add(pageNumber))
        {
            return;
        }

        _pending.Add(pageNumber);
    }

    private bool TryStartLocked()
    {
        if (_running || _disposed || _pending.Count == 0)
        {
            return false;
        }

        _running = true;
        return true;
    }

    private void CompleteWaiters(IEnumerable<int> pageNumbers, bool result)
    {
        lock (_gate)
        {
            foreach (var pageNumber in pageNumbers)
            {
                _queued.Remove(pageNumber);
                if (_waiters.Remove(pageNumber, out var waiter))
                {
                    waiter?.TrySetResult(result);
                }
            }
        }
    }
}
