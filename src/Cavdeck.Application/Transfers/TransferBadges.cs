using Cavdeck.Domain.Transfers;

namespace Cavdeck.Application.Transfers;

public readonly record struct TransferBadge(int PendingCount, bool HasFailures)
{
    public bool ShowCount => PendingCount > 0;
}

public static class TransferBadges
{
    public static TransferBadge From(IEnumerable<TransferState> states)
    {
        ArgumentNullException.ThrowIfNull(states);
        var pending = 0;
        var failed = false;
        foreach (var state in states)
        {
            if (state is TransferState.Queued or TransferState.Running
                or TransferState.Paused or TransferState.WaitingRetry)
                pending++;
            else if (state == TransferState.Failed)
                failed = true;
        }

        return new TransferBadge(pending, failed);
    }
}
