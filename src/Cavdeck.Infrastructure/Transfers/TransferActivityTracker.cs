using Cavdeck.Application.Abstractions;

namespace Cavdeck.Infrastructure.Transfers;

// TransferEngine must acquire/release this tracker for EVERY active operation.
// This starter intentionally does not initiate real S3 transfers yet.
public sealed class TransferActivityTracker : ITransferActivity
{
    private int _active;
    public bool HasActiveTransfers => Volatile.Read(ref _active) > 0;

    public IDisposable Begin()
    {
        Interlocked.Increment(ref _active);
        return new Lease(this);
    }

    private sealed class Lease(TransferActivityTracker owner) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
                Interlocked.Decrement(ref owner._active);
        }
    }
}
