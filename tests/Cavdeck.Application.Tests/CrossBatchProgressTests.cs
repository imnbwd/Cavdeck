using Cavdeck.Application.Transfers;
using Xunit;

namespace Cavdeck.Application.Tests;

public sealed class CrossBatchProgressTests
{
    [Fact]
    public void Percent_counts_successful_skipped_failed_and_cancelled_objects()
    {
        var progress = new CrossBatchProgress(10, 3, 2, 1, 1, 3);
        Assert.Equal(7, progress.Finished);
        Assert.Equal(70, progress.Percent);
    }

    [Fact]
    public void Empty_batch_has_no_nan_or_division_by_zero()
    {
        var progress = new CrossBatchProgress(0, 0, 0, 0, 0, 0);
        Assert.Equal(0, progress.Finished);
        Assert.Equal(0, progress.Percent);
    }

    [Fact]
    public void Progress_is_bounded_even_if_a_provider_reports_inconsistent_counts()
    {
        var progress = new CrossBatchProgress(2, 3, 2, 1, 0, 0);
        Assert.Equal(2, progress.Finished);
        Assert.Equal(100, progress.Percent);
    }
}
