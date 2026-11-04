using Cavdeck.App.Features.Transfers;
using Xunit;

namespace Cavdeck.App.Tests;

public sealed class TransferNoticePreferenceTests
{
    [Fact]
    public void Disabled_success_and_skip_notices_still_allow_failed_notices()
    {
        var buffer = new TransferNoticeBuffer();
        buffer.AddCompleted("upload.zip", enabled: false);
        buffer.AddSkipped(enabled: false);
        buffer.AddFailed("failed.zip");
        var snapshot = buffer.Drain(enabled: false);
        Assert.Equal(0, snapshot.Completed);
        Assert.Equal(0, snapshot.Skipped);
        Assert.Equal(1, snapshot.Failed);
        Assert.Equal("failed.zip", snapshot.FailedName);
    }

    [Fact]
    public void Switching_off_before_notice_fires_discards_pending_completions()
    {
        var buffer = new TransferNoticeBuffer();
        buffer.AddCompleted("upload.zip", enabled: true);
        buffer.AddSkipped(enabled: true);
        buffer.AddFailed("failed.zip");
        var snapshot = buffer.Drain(enabled: false);
        Assert.Equal(0, snapshot.Completed);
        Assert.Equal(0, snapshot.Skipped);
        Assert.Null(snapshot.CompletedName);
        Assert.Equal(1, snapshot.Failed);
        Assert.Equal(0, buffer.Drain(enabled: true).Completed);
    }

    [Fact]
    public void Enabled_notices_still_aggregate_completions()
    {
        var buffer = new TransferNoticeBuffer();
        buffer.AddCompleted("one", enabled: true);
        buffer.AddCompleted("two", enabled: true);
        buffer.AddSkipped(enabled: true);
        var snapshot = buffer.Drain(enabled: true);
        Assert.Equal(2, snapshot.Completed);
        Assert.Equal(1, snapshot.Skipped);
        Assert.Null(snapshot.CompletedName);
    }
}
