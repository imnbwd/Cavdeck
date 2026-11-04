using Cavdeck.Application.Transfers;
using Cavdeck.Domain.Transfers;
using Xunit;

namespace Cavdeck.Application.Tests;

public sealed class TransferBadgeTests
{
    [Fact]
    public void Zero_pending_hides_the_count()
    {
        var badge = TransferBadges.From([]);
        Assert.Equal(0, badge.PendingCount);
        Assert.False(badge.ShowCount);
        Assert.False(badge.HasFailures);
    }

    [Fact]
    public void Open_states_count_and_paused_is_included()
    {
        var badge = TransferBadges.From([
            TransferState.Running,
            TransferState.Queued,
            TransferState.WaitingRetry,
            TransferState.Paused,
            TransferState.Completed,
            TransferState.Cancelled
        ]);

        Assert.Equal(4, badge.PendingCount);
        Assert.True(badge.ShowCount);
        Assert.False(badge.HasFailures);
    }

    [Fact]
    public void Failure_marks_without_adding_to_the_count()
    {
        var badge = TransferBadges.From([TransferState.Failed, TransferState.Completed]);
        Assert.Equal(0, badge.PendingCount);
        Assert.False(badge.ShowCount);
        Assert.True(badge.HasFailures);
    }

    [Fact]
    public void Pending_and_failure_can_both_be_true()
    {
        var badge = TransferBadges.From([TransferState.Running, TransferState.Failed]);
        Assert.Equal(1, badge.PendingCount);
        Assert.True(badge.ShowCount);
        Assert.True(badge.HasFailures);
    }
}
