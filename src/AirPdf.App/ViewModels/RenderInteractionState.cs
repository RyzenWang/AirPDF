using Avalonia.Threading;

namespace AirPdf.App.ViewModels;

/// <summary>
/// Tracks whether the reader is actively scrolling or zooming. Page renders use a smaller
/// target while this is true, and pages that were rendered at the reduced size re-render
/// once the interaction has been quiet for a moment.
/// </summary>
public sealed class RenderInteractionState : IDisposable
{
    private static readonly TimeSpan QuietDelay = TimeSpan.FromMilliseconds(220);

    private readonly DispatcherTimer _timer;
    private volatile bool _isInteracting;

    public RenderInteractionState()
    {
        _timer = new DispatcherTimer { Interval = QuietDelay };
        _timer.Tick += (_, _) => EndInteraction();
    }

    public bool IsInteracting => _isInteracting;

    public event EventHandler? Changed;

    public void BeginInteraction()
    {
        if (!_isInteracting)
        {
            _isInteracting = true;
            Changed?.Invoke(this, EventArgs.Empty);
        }

        _timer.Stop();
        _timer.Start();
    }

    private void EndInteraction()
    {
        _timer.Stop();
        if (!_isInteracting)
        {
            return;
        }

        _isInteracting = false;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose() => _timer.Stop();
}
