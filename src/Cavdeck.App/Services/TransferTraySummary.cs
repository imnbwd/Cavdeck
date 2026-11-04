using Cavdeck.Domain.Transfers;

namespace Cavdeck.App.Services;

public readonly record struct TransferTraySummary(int Running, int Waiting, int Paused, int Failed)
{
    public int Unfinished => Running + Waiting + Paused;

    public static TransferTraySummary FromJobs(IEnumerable<TransferJob> jobs)
    {
        var running = 0;
        var waiting = 0;
        var paused = 0;
        var failed = 0;
        foreach (var job in jobs)
        {
            switch (job.State)
            {
                case TransferState.Running: running++; break;
                case TransferState.Queued or TransferState.WaitingRetry: waiting++; break;
                case TransferState.Paused: paused++; break;
                case TransferState.Failed: failed++; break;
            }
        }
        return new(running, waiting, paused, failed);
    }
}
