using AirPdf.Core.Services;
using Xunit;

namespace AirPdf.Core.Tests;

public sealed class PdfNativeGateTests
{
    [Fact]
    public async Task InteractiveWaiter_RunsBeforeAnEarlierBackgroundWaiter()
    {
        var gate = new PdfNativeGate();

        await gate.WaitAsync();
        var background = gate.WaitAsync();
        var interactive = gate.WaitAsync(PdfGatePriority.Interactive, default);

        Assert.False(background.IsCompleted);
        Assert.False(interactive.IsCompleted);

        gate.Release();
        await interactive.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(background.IsCompleted);

        gate.Release();
        await background.WaitAsync(TimeSpan.FromSeconds(2));
        gate.Release();
    }

    [Fact]
    public async Task CancelledWaiter_DoesNotHoldTheGate()
    {
        var gate = new PdfNativeGate();
        await gate.WaitAsync();

        using var cancellation = new CancellationTokenSource();
        var waiting = gate.WaitAsync(cancellation.Token);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await waiting);

        gate.Release();

        var next = gate.WaitAsync();
        await next.WaitAsync(TimeSpan.FromSeconds(2));
        gate.Release();
    }
}
