using System.Text;
using Cavdeck.Application.Explorer;
using Cavdeck.Application.Transfers;
using Cavdeck.Domain.Storage;
using Cavdeck.Domain.Transfers;
using Xunit;

namespace Cavdeck.Application.Tests;

public sealed class IsolatedManagementTests
{
    private static readonly StorageConnection Source = new(Guid.NewGuid(), "source", StorageProviderKind.AwsS3, "", "us-east-1", "fictional");
    private static readonly StorageConnection Destination = new(Guid.NewGuid(), "destination", StorageProviderKind.Minio,
        "https://storage.example.test", "us-east-1", "fictional-destination");

    [Fact]
    public async Task FolderMarkerIsAtomicAndDoesNotOverwriteChildren()
    {
        var store = new FakeRemote();
        store.Put("assets", "folder/child", "existing");
        var explorer = new ExplorerService(null!, remote: store);
        var contenders = Enumerable.Range(0, 5).Select(async _ =>
        {
            try
            {
                await explorer.CreateFolderMarkerAsync(Source, "assets", "folder/", CancellationToken.None);
                return true;
            }
            catch (StorageException exception) when (exception.Failure.Kind == StorageFailureKind.PreconditionFailed)
            {
                return false;
            }
        });
        Assert.Single(await Task.WhenAll(contenders), success => success);
        Assert.Equal("existing", store.Text("assets", "folder/child"));
        Assert.Equal("", store.Text("assets", "folder/"));
        await Assert.ThrowsAsync<StorageException>(() => explorer.CreateFolderMarkerAsync(
            Source with { IsReadOnly = true }, "assets", "blocked/", CancellationToken.None));
        await Assert.ThrowsAsync<StorageException>(() => explorer.CreateFolderMarkerAsync(
            Source with { RestrictedBucket = "other" }, "assets", "blocked/", CancellationToken.None));
        await Assert.ThrowsAsync<StorageException>(() => explorer.CreateFolderMarkerAsync(
            Source with { KeyPrefix = "scope/" }, "assets", "blocked/", CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => explorer.CreateFolderMarkerAsync(Source, "assets", "file", CancellationToken.None));
        var unsupported = new ExplorerService(null!, remote: new FakeRemote { RejectVerifiedPut = true });
        await Assert.ThrowsAsync<StorageException>(() => unsupported.CreateFolderMarkerAsync(Source, "assets", "blocked/", CancellationToken.None));
    }

    [Theory]
    [InlineData(TransferConflictPolicy.Fail, true, false, "chosen.txt", "existing")]
    [InlineData(TransferConflictPolicy.Skip, false, true, "chosen.txt", "existing")]
    [InlineData(TransferConflictPolicy.Overwrite, false, false, "chosen.txt", "payload")]
    [InlineData(TransferConflictPolicy.Rename, false, false, "chosen (1).txt", "payload")]
    public async Task ExactCrossCopyHonorsDestinationNameAndPolicy(TransferConflictPolicy policy, bool failed,
        bool skipped, string finalName, string finalData)
    {
        var store = new FakeRemote { RejectConditionalMultipart = true };
        store.Put("source", "original.txt", "payload");
        store.Put("target", "chosen.txt", "existing");
        var journal = new MemoryTransferJournal();
        await using var engine = new TransferEngine(store, journal, new CrossConnections(Source, Destination), Options());
        await engine.StartAsync();
        var jobs = await engine.EnqueueCrossCopiesAsync(Source, Destination, "source",
            [new DownloadItem("original.txt", false, 7, "chosen.txt")], "target", "", policy, exactTargets: true);
        var job = await WaitAsync(engine, Assert.Single(jobs).Id);
        Assert.Equal(failed ? TransferState.Failed : TransferState.Completed, job.State);
        Assert.Equal(skipped, job.Skipped);
        Assert.Equal(finalData, store.Text("target", finalName));
        Assert.Equal("payload", store.Text("source", "original.txt"));
        Assert.False(store.Contains("target", "original.txt"));
    }

    [Theory]
    [InlineData(TransferState.Queued, false, true)]
    [InlineData(TransferState.Paused, false, true)]
    [InlineData(TransferState.Running, false, true)]
    [InlineData(TransferState.WaitingRetry, false, true)]
    [InlineData(TransferState.Failed, true, true)]
    [InlineData(TransferState.Failed, false, false)]
    [InlineData(TransferState.Completed, false, false)]
    [InlineData(TransferState.Cancelled, false, false)]
    public async Task OnlyEligibleJobsResumeWithTheirOriginalId(TransferState state, bool retryable, bool resumable)
    {
        var journal = new MemoryTransferJournal();
        var store = new FakeRemote();
        using var temp = new TemporaryFile("");
        var original = TransferJob.Create(TransferKind.Upload, Source.Id, "assets", "marker/", temp.Path, 0,
            TransferConflictPolicy.Fail, folderMarker: true) with
        {
            State = state,
            Failure = state == TransferState.Failed ? new TransferFailure(TransferFailureKind.Network, retryable, "Mock", null) : null,
            ConnectionConfiguration = CrossTransferPlanner.ConfigurationIdentity(Source)
        };
        await journal.UpsertAsync(original, CancellationToken.None);
        await using var engine = new TransferEngine(store, journal, new CrossConnections(Source), Options());
        var rejected = new List<ResumeRejection>();
        var started = false;
        await engine.ResumeIsolatedRunAsync([original], [], rejected, () => started = true, CancellationToken.None);
        Assert.Equal(resumable, started);
        if (resumable)
        {
            var job = await WaitAsync(engine, original.Id);
            Assert.Equal(TransferState.Completed, job.State);
            Assert.Equal(original.BatchId, job.BatchId);
            Assert.Empty(rejected);
        }
        else
        {
            Assert.Single(rejected);
            Assert.Empty(engine.Jobs);
            Assert.False(store.Contains("assets", "marker/"));
        }
        Assert.Equal(original.Id, Assert.Single(await journal.LoadRecentAsync(10, CancellationToken.None)).Id);
    }

    [Fact]
    public async Task RetryableMultipartRetainsItsUploadIdAndPartsAcrossRuns()
    {
        using var temp = new TemporaryFile("abcdefghijklmno");
        var remote = new FakeRemote { FailPartNumber = 2 };
        var journal = new MemoryTransferJournal();
        var options = Options();
        TransferJob checkpoint;
        await using (var first = new TransferEngine(remote, journal, new CrossConnections(Source), options))
        {
            await first.StartAsync();
            await first.EnqueueExactUploadsAsync(Source, "assets", "multipart", temp.Path, false,
                TransferConflictPolicy.Fail, CancellationToken.None);
            var failed = await WaitAsync(first, Assert.Single(first.Jobs).Id);
            Assert.True(failed.Failure?.Retryable);
            checkpoint = Assert.Single(await first.FinishIsolatedRunAsync(preserveRetryableState: true));
        }
        Assert.NotNull(checkpoint.UploadId);
        Assert.NotEmpty(await journal.GetPartsAsync(checkpoint.Id, CancellationToken.None));
        Assert.Equal(1, remote.PendingUploads);
        await using var second = new TransferEngine(remote, journal, new CrossConnections(Source), options);
        await second.ResumeIsolatedRunAsync([checkpoint], [], new List<ResumeRejection>(), () => { }, CancellationToken.None);
        var result = await WaitAsync(second, checkpoint.Id);
        Assert.Equal(TransferState.Completed, result.State);
        Assert.Equal("abcdefghijklmno", remote.Text("assets", "multipart"));
        Assert.Equal(0, remote.PendingUploads);
    }

    [Fact]
    public async Task InterruptedCrossMoveDoesNotDuplicateJobsOrDeleteTwice()
    {
        var remote = new FakeRemote { DeleteThenFailOnce = true };
        remote.Put("source", "folder/a", "payload");
        remote.Put("source", "folder/b", "second");
        var journal = new MemoryTransferJournal();
        var options = Options();
        TransferJob checkpoint;
        CrossTransferBatch originalBatch;
        await using (var first = new TransferEngine(remote, journal, new CrossConnections(Source, Destination), options))
        {
            await first.StartAsync();
            var jobs = await first.EnqueueCrossCopiesAsync(Source, Destination, "source",
                [new DownloadItem("folder/a", false, 7, "copy/a")], "target", "", TransferConflictPolicy.Fail,
                move: true, exactTargets: true);
            var failed = await WaitAsync(first, Assert.Single(jobs).Id);
            Assert.True(failed.Failure?.Retryable);
            Assert.Equal(CrossTransferStage.Removing, failed.CrossTransfer?.Stage);
            checkpoint = Assert.Single(await first.FinishIsolatedRunAsync(preserveRetryableState: true));
            originalBatch = Assert.Single(first.Batches);
        }
        var interruptedBatch = originalBatch with
        {
            State = CrossBatchState.Scanning, ScanIndex = 0,
            Items = [originalBatch.Items[0], new DownloadItem("folder/b", false, 6, "copy/b")]
        };
        await journal.UpsertBatchAsync(interruptedBatch, CancellationToken.None);
        await using var second = new TransferEngine(remote, journal, new CrossConnections(Source, Destination), options);
        await second.ResumeIsolatedRunAsync([checkpoint], [interruptedBatch], new List<ResumeRejection>(), () => { }, CancellationToken.None);
        await WaitAsync(second, checkpoint.Id);
        await WaitAsync(second, second.Jobs.Single(job => job.Key == "copy/b").Id);
        Assert.All(second.Jobs, job => Assert.Equal(TransferState.Completed, job.State));
        Assert.Equal(2, remote.ConditionalDeletes);
        Assert.Equal("payload", remote.Text("target", "copy/a"));
        Assert.Equal("second", remote.Text("target", "copy/b"));
        Assert.Equal(2, (await journal.LoadRecentAsync(10, CancellationToken.None)).Count);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task DownloadCheckpointResumesOnlyIfBothEndpointsMatch(bool remoteChanged, bool localChanged)
    {
        using var temp = new TemporaryFile("");
        File.Delete(temp.Path);
        var store = new FakeRemote();
        store.Put("assets", "report", "payload");
        await using var lease = await store.OpenAsync(Source, write: false, CancellationToken.None);
        var identity = await lease.HeadAsync("assets", "report", CancellationToken.None);
        var original = TransferJob.Create(TransferKind.Download, Source.Id, "assets", "report", temp.Path,
            identity!.Size, TransferConflictPolicy.Overwrite) with
        {
            State = TransferState.Paused, Remote = identity, Resume = ResumeMode.Range, BytesTransferred = 3,
            ConnectionConfiguration = CrossTransferPlanner.ConfigurationIdentity(Source),
            LocalDestinationBaselineCaptured = true
        };
        var partial = TransferPaths.Partial(temp.Path, original.Id, isolated: true);
        await File.WriteAllTextAsync(partial, "pay");
        try
        {
            if (remoteChanged)
                store.Put("assets", "report", "changed");
            if (localChanged)
                await File.WriteAllTextAsync(temp.Path, "local racing file");
            var journal = new MemoryTransferJournal();
            await journal.UpsertAsync(original, CancellationToken.None);
            await using var engine = new TransferEngine(store, journal, new CrossConnections(Source), Options());
            await engine.ResumeIsolatedRunAsync([original], [], new List<ResumeRejection>(), () => { }, CancellationToken.None);
            var job = await WaitAsync(engine, original.Id);
            if (remoteChanged || localChanged)
            {
                Assert.Equal(TransferState.Failed, job.State);
                Assert.Equal(remoteChanged ? "SourceChanged" : "LocalDestinationChanged", job.Failure?.Code);
                Assert.Equal(localChanged, File.Exists(temp.Path));
                if (localChanged)
                    Assert.Equal("local racing file", await File.ReadAllTextAsync(temp.Path));
            }
            else
            {
                Assert.Equal(TransferState.Completed, job.State);
                Assert.True(store.RangeWasRequested);
                Assert.Equal("payload", await File.ReadAllTextAsync(temp.Path));
            }
        }
        finally { File.Delete(partial); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResumeRejectsDeletedAndRetargetedConnections(bool retargeted)
    {
        var store = new FakeRemote();
        var journal = new MemoryTransferJournal();
        var job = TransferJob.Create(TransferKind.Upload, Source.Id, "assets", "file", "fictional", 1,
            TransferConflictPolicy.Fail) with { ConnectionConfiguration = CrossTransferPlanner.ConfigurationIdentity(Source) };
        await journal.UpsertAsync(job, CancellationToken.None);
        var current = retargeted ? Source with { Region = "eu-west-1" } : Destination;
        await using var engine = new TransferEngine(store, journal, new CrossConnections(current), Options());
        var rejected = new List<ResumeRejection>();
        await engine.ResumeIsolatedRunAsync([job], [], rejected, () => throw new InvalidOperationException(), CancellationToken.None);
        Assert.Equal(retargeted ? "ConnectionConfigurationChanged" : "ConnectionMissing", Assert.Single(rejected).Reason);
        Assert.Empty(engine.Jobs);
    }

    private static TransferEngineOptions Options() => new()
    {
        IsolatedRun = true, MaxConcurrentTransfers = 1, MaxConcurrentParts = 1, MaxAttempts = 1,
        CrossCopyPartSizeBytes = 4, MultipartThresholdBytes = 4, PartSizeBytes = 4, MinimumPartBytes = 1
    };

    private static async Task<TransferJob> WaitAsync(TransferEngine engine, Guid id)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (engine.TryCurrent(id) is not { IsOpen: false })
            await Task.Delay(15, timeout.Token);
        return engine.TryCurrent(id)!;
    }

    private sealed class TemporaryFile : IDisposable
    {
        public TemporaryFile(string content)
        {
            Path = System.IO.Path.GetTempFileName();
            File.WriteAllText(Path, content, new UTF8Encoding(false));
        }
        public string Path { get; }
        public void Dispose() => File.Delete(Path);
    }
}
