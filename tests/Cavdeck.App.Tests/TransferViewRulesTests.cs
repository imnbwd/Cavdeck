using Cavdeck.App.Features.Transfers;
using Cavdeck.Domain.Transfers;
using Xunit;

namespace Cavdeck.App.Tests;

public sealed class TransferViewRulesTests
{
    [Theory]
    [InlineData(TransferState.Queued, TransferStatusFilter.Queued, true)]
    [InlineData(TransferState.WaitingRetry, TransferStatusFilter.Queued, true)]
    [InlineData(TransferState.Running, TransferStatusFilter.Running, true)]
    [InlineData(TransferState.Paused, TransferStatusFilter.Paused, true)]
    [InlineData(TransferState.Failed, TransferStatusFilter.Failed, true)]
    [InlineData(TransferState.Cancelled, TransferStatusFilter.All, false)]
    [InlineData(TransferState.Completed, TransferStatusFilter.All, false)]
    [InlineData(TransferState.Failed, TransferStatusFilter.Running, false)]
    [InlineData(TransferState.Queued, TransferStatusFilter.Paused, false)]
    public void Current_filter_matches_only_expected_states(
        TransferState state, TransferStatusFilter filter, bool expected) =>
        Assert.Equal(expected, TransferViewRules.Matches(state, history: false, filter));

    [Theory]
    [InlineData(TransferState.Completed, TransferStatusFilter.All, true)]
    [InlineData(TransferState.Cancelled, TransferStatusFilter.All, true)]
    [InlineData(TransferState.Completed, TransferStatusFilter.Completed, true)]
    [InlineData(TransferState.Cancelled, TransferStatusFilter.Cancelled, true)]
    [InlineData(TransferState.Completed, TransferStatusFilter.Cancelled, false)]
    [InlineData(TransferState.Cancelled, TransferStatusFilter.Completed, false)]
    [InlineData(TransferState.Running, TransferStatusFilter.All, false)]
    [InlineData(TransferState.Failed, TransferStatusFilter.All, false)]
    public void History_filter_does_not_leak_unfinished_jobs(
        TransferState state, TransferStatusFilter filter, bool expected) =>
        Assert.Equal(expected, TransferViewRules.Matches(state, history: true, filter));

    [Fact]
    public void Notice_buffer_aggregates_completion_and_failure_without_cross_contamination()
    {
        var buffer = new TransferNoticeBuffer();
        buffer.AddCompleted("one.bin");
        buffer.AddCompleted("two.bin");
        buffer.AddFailed("broken.bin");
        buffer.AddSkipped();

        var first = buffer.Drain();
        Assert.Equal(2, first.Completed);
        Assert.Equal(1, first.Skipped);
        Assert.Equal(1, first.Failed);
        Assert.Null(first.CompletedName);
        Assert.Equal("broken.bin", first.FailedName);

        var cleared = buffer.Drain();
        Assert.Equal(0, cleared.Completed);
        Assert.Equal(0, cleared.Skipped);
        Assert.Equal(0, cleared.Failed);

        buffer.AddCompleted("next.bin");
        var next = buffer.Drain();
        Assert.Equal(1, next.Completed);
        Assert.Equal(0, next.Skipped);
        Assert.Equal("next.bin", next.CompletedName);
    }
}
