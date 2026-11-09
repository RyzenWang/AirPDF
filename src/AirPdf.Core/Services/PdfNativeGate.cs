namespace AirPdf.Core.Services;

/// <summary>
/// Priority of a PDFium native call. PDFium is not thread-safe, so every call still goes
/// through one gate; the priority decides which waiter gets the gate next.
/// </summary>
internal enum PdfGatePriority
{
    /// <summary>A page render that the reader can currently see.</summary>
    Interactive = 0,

    /// <summary>Text extraction, links, metadata, opening and other background work.</summary>
    Background = 1
}

/// <summary>
/// An async mutex with priority handoff. The current owner cannot be preempted, but when it
/// releases the gate the highest-priority waiter runs next. That is enough for search or
/// link extraction to yield between pages instead of making a visible render wait for the
/// whole document scan.
/// </summary>
internal sealed class PdfNativeGate
{
    private readonly object _sync = new();
    private readonly PriorityQueue<Waiter, (PdfGatePriority Priority, long Sequence)> _waiters = new();
    private bool _held;
    private long _sequence;

    public Task WaitAsync(CancellationToken cancellationToken = default) =>
        WaitAsync(PdfGatePriority.Background, cancellationToken);

    public Task WaitAsync(PdfGatePriority priority, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled(cancellationToken);
        }

        lock (_sync)
        {
            if (!_held)
            {
                _held = true;
                return Task.CompletedTask;
            }

            var waiter = new Waiter();
            _waiters.Enqueue(waiter, (priority, _sequence++));

            if (cancellationToken.CanBeCanceled)
            {
                waiter.Registration = cancellationToken.Register(
                    static state => ((Waiter)state!).Cancel(),
                    waiter);
            }

            return waiter.Task;
        }
    }

    public void Wait(CancellationToken cancellationToken = default) =>
        WaitAsync(cancellationToken).GetAwaiter().GetResult();

    public void Release()
    {
        Waiter? next = null;

        lock (_sync)
        {
            while (_waiters.TryDequeue(out var waiter, out _))
            {
                if (waiter.TryTake())
                {
                    next = waiter;
                    break;
                }

                // The waiter was cancelled while it was queued. Release its registration
                // here rather than inside the cancellation callback.
                waiter.DisposeRegistration();
            }

            if (next is null)
            {
                _held = false;
                return;
            }
        }

        next.Signal();
    }

    private sealed class Waiter
    {
        private readonly TaskCompletionSource _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private int _state;

        public CancellationTokenRegistration Registration { get; set; }

        public Task Task => _completion.Task;

        public bool TryTake() =>
            Interlocked.CompareExchange(ref _state, 1, 0) == 0;

        public void Signal()
        {
            Registration.Unregister();
            _completion.TrySetResult();
        }

        public void Cancel()
        {
            if (Interlocked.CompareExchange(ref _state, 2, 0) == 0)
            {
                _completion.TrySetCanceled();
            }
        }

        public void DisposeRegistration() => Registration.Unregister();
    }
}
