using Cavdeck.App.Services;
using Cavdeck.Domain.Transfers;
using Xunit;

namespace Cavdeck.App.Tests;

public sealed class TransferTraySummaryTests
{
    [Fact]
    public void Summary_distinguishes_open_terminal_and_failed_states()
    {
        var states = Enum.GetValues<TransferState>();
        var jobs = states.Select(state => TransferJob.Create(TransferKind.Download,
            Guid.NewGuid(), "bucket", "key", "file", null,
            TransferConflictPolicy.Skip) with { State = state });
        var result = TransferTraySummary.FromJobs(jobs);
        Assert.Equal(1, result.Running);
        Assert.Equal(2, result.Waiting);
        Assert.Equal(1, result.Paused);
        Assert.Equal(1, result.Failed);
        Assert.Equal(4, result.Unfinished);
    }
}
