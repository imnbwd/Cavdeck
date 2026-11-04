using Cavdeck.Domain.Transfers;
using Xunit;

namespace Cavdeck.Domain.Tests;

public sealed class TransferJobTests
{
    [Fact]
    public void Transfer_job_is_initialized_as_expected()
    {
        var job = TransferJob.Create(
            TransferKind.Upload, Guid.NewGuid(), "bucket", "local.zip", "local.zip",
            1024, TransferConflictPolicy.Skip);

        Assert.Equal(TransferState.Queued, job.State);
        Assert.Equal(1024, job.TotalBytes);
        Assert.Equal(ResumeMode.Reconcile, job.Resume);
    }
}
