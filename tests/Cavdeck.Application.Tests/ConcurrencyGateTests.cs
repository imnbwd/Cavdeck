using Cavdeck.Application.Transfers;
using Xunit;

namespace Cavdeck.Application.Tests;

public sealed class ConcurrencyGateTests
{
    [Fact]
    public async Task Already_cancelled_token_does_not_acquire_a_permit()
    {
        var gate = new ConcurrencyGate(1);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => gate.AcquireAsync(cancellation.Token));

        await gate.AcquireAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
        gate.Release();
    }

    [Fact]
    public async Task Cancelling_a_waiter_does_not_block_the_next_waiter()
    {
        var gate = new ConcurrencyGate(1);
        await gate.AcquireAsync(CancellationToken.None);
        using var cancelled = new CancellationTokenSource();

        var cancelledWaiter = gate.AcquireAsync(cancelled.Token);
        var nextWaiter = gate.AcquireAsync(CancellationToken.None);
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelledWaiter);
        gate.Release();

        await nextWaiter.WaitAsync(TimeSpan.FromSeconds(2));
        gate.Release();

        await gate.AcquireAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
        gate.Release();
    }

    [Fact]
    public async Task Cancellation_racing_with_release_never_leaks_a_permit()
    {
        const int iterations = 200;
        const int waiterCount = 16;

        for (var iteration = 0; iteration < iterations; iteration++)
        {
            var gate = new ConcurrencyGate(1);
            await gate.AcquireAsync(CancellationToken.None);
            var cancellations = new CancellationTokenSource[waiterCount];
            var waiters = new Task[waiterCount];

            for (var index = 0; index < waiterCount; index++)
            {
                cancellations[index] = new CancellationTokenSource();
                waiters[index] = gate.AcquireAsync(cancellations[index].Token);
            }

            var release = Task.Run(gate.Release);
            var cancel = Task.Run(() => Parallel.ForEach(cancellations, source => source.Cancel()));
            await Task.WhenAll(release, cancel);

            var granted = 0;
            foreach (var waiter in waiters)
            {
                try
                {
                    await waiter.WaitAsync(TimeSpan.FromSeconds(2));
                    granted++;
                }
                catch (OperationCanceledException)
                {
                    // Cancellation won while the waiter was still queued.
                }
            }

            for (var index = 0; index < granted; index++)
                gate.Release();

            // If cancellation stole a permit between the old implementation's
            // accounting increment and TrySetResult, this acquisition would hang.
            await gate.AcquireAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
            gate.Release();

            foreach (var cancellation in cancellations)
                cancellation.Dispose();
        }
    }

    [Fact]
    public async Task Releasing_without_an_owned_permit_is_rejected()
    {
        var gate = new ConcurrencyGate(1);

        Assert.Throws<InvalidOperationException>(gate.Release);

        await gate.AcquireAsync(CancellationToken.None);
        gate.Release();
    }
}
