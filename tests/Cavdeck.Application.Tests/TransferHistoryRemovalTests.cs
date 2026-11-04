using Cavdeck.Application.Transfers;
using Cavdeck.Domain.Transfers;
using Xunit;

namespace Cavdeck.Application.Tests;

public sealed class TransferHistoryRemovalTests
{
    private static TransferJob Job(TransferState state) =>
        TransferJob.Create(TransferKind.Upload, Guid.NewGuid(), "bucket", "file.txt",
            @"C:\source\file.txt", 12, TransferConflictPolicy.Skip) with
        {
            State = state,
            Failure = state == TransferState.Failed
                ? new TransferFailure(TransferFailureKind.AccessDenied, false, "AccessDenied", "denied")
                : null
        };

    [Fact]
    public async Task Clearing_failed_removes_only_terminal_records_without_touching_resumable_work()
    {
        var journal = new MemoryTransferJournal();
        var failed = Job(TransferState.Failed);
        var running = Job(TransferState.Running);
        var pendingMultipart = Job(TransferState.Failed) with { UploadId = "pending" };
        var completed = Job(TransferState.Completed);
        foreach (var job in new[] { failed, running, pendingMultipart, completed })
            await journal.UpsertAsync(job, CancellationToken.None);
        await journal.UpsertPartAsync(failed.Id,
            new TransferPart(1, 0, 12, "etag"), CancellationToken.None);
        var removed = await journal.RemoveTerminalAsync(TransferHistoryScope.Failed, null,
            CancellationToken.None);
        Assert.Equal(new[] { failed.Id }, removed.JobIds);
        Assert.Empty(await journal.GetPartsAsync(failed.Id, CancellationToken.None));
        var remaining = await journal.LoadRecentAsync(10, CancellationToken.None);
        Assert.DoesNotContain(remaining, job => job.Id == failed.Id);
        Assert.Contains(remaining, job => job.Id == running.Id);
        Assert.Contains(remaining, job => job.Id == pendingMultipart.Id);
        Assert.Contains(remaining, job => job.Id == completed.Id);
    }

    [Fact]
    public async Task Single_record_removal_does_not_cancel_a_running_job()
    {
        var journal = new MemoryTransferJournal();
        var running = Job(TransferState.Running);
        var cancelled = Job(TransferState.Cancelled);
        await journal.UpsertAsync(running, CancellationToken.None);
        await journal.UpsertAsync(cancelled, CancellationToken.None);
        Assert.Empty((await journal.RemoveTerminalAsync(
            TransferHistoryScope.Single, running.Id, CancellationToken.None)).JobIds);
        Assert.Equal(new[] { cancelled.Id }, (await journal.RemoveTerminalAsync(
            TransferHistoryScope.Single, cancelled.Id, CancellationToken.None)).JobIds);
        Assert.Single(await journal.LoadRecentAsync(10, CancellationToken.None));
    }

    [Fact]
    public async Task Active_cross_copy_scan_protects_mapping_and_batch_until_finished()
    {
        var journal = new MemoryTransferJournal();
        var source = Guid.NewGuid();
        var destination = Guid.NewGuid();
        var batch = new CrossTransferBatch(Guid.NewGuid(), source, destination,
            "source", "target", "", [], TransferConflictPolicy.Skip, 0, null, null, 1,
            CrossBatchState.Scanning, null, DateTimeOffset.UtcNow);
        var job = TransferJob.Create(TransferKind.Copy, destination, "target", "file.txt",
            "", 12, TransferConflictPolicy.Skip, batch.Id) with
        {
            State = TransferState.Failed,
            CrossTransfer = new CrossTransferSpec(
                new StorageObjectRef(source, "source", "file.txt"),
                new StorageObjectRef(destination, "target", "file.txt"),
                TransferConflictPolicy.Skip)
        };
        await journal.UpsertBatchAsync(batch, CancellationToken.None);
        Assert.True(await journal.TryCreateCrossJobAsync(job, CancellationToken.None));
        Assert.Empty((await journal.RemoveTerminalAsync(
            TransferHistoryScope.Failed, null, CancellationToken.None)).JobIds);
        Assert.Equal(1, (await journal.GetBatchProgressAsync(batch.Id, CancellationToken.None)).Failed);
        await journal.UpsertBatchAsync(batch with { State = CrossBatchState.Completed }, CancellationToken.None);
        var removed = await journal.RemoveTerminalAsync(TransferHistoryScope.Failed, null,
            CancellationToken.None);
        Assert.Contains(job.Id, removed.JobIds);
        Assert.Contains(batch.Id, removed.BatchIds);
        Assert.Empty(await journal.LoadRecentBatchesAsync(10, CancellationToken.None));
    }
}
