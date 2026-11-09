using System.Security.Cryptography;
using System.Text;
using Cavdeck.Application.Abstractions;
using Cavdeck.Application.Transfers;
using Cavdeck.Domain.Storage;
using Cavdeck.Domain.Transfers;
using Xunit;

namespace Cavdeck.Application.Tests;

public sealed class TransferEngineTests
{
    [Fact]
    public async Task Cross_copy_uses_read_only_source_and_verifies_destination_bytes()
    {
        var source = SampleConnection() with { Id = Guid.NewGuid(), IsReadOnly = true };
        var destination = SampleConnection() with { Id = Guid.NewGuid() };
        var remote = new FakeRemote();
        remote.Put("source", "folder/空 格.txt", "hello world from source");
        await using var engine = new TransferEngine(remote, new MemoryTransferJournal(),
            new CrossConnections(source, destination), new TransferEngineOptions
            {
                CrossCopyPartSizeBytes = 8,
                MinimumPartBytes = 1,
                CrossCopyMemoryBudgetBytes = 256 * 1024 * 1024
            });
        await engine.StartAsync();

        var jobs = await engine.EnqueueCrossCopiesAsync(source, destination, "source",
            [new DownloadItem("folder/空 格.txt", false, 23)], "target", "目的/",
            TransferConflictPolicy.Skip);
        var result = await Wait(engine, Assert.Single(jobs).Id,
            job => job.State is TransferState.Completed or TransferState.Failed);

        Assert.Equal(TransferState.Completed, result.State);
        Assert.Equal("SHA256", result.CrossTransfer?.Verification);
        Assert.Equal("hello world from source", remote.Text("source", "folder/空 格.txt"));
        Assert.Equal("hello world from source", remote.Text("target", "目的/空 格.txt"));
    }

    [Fact]
    public async Task Cross_move_verifies_target_before_conditional_source_delete()
    {
        var source = SampleConnection() with { Id = Guid.NewGuid() };
        var destination = SampleConnection() with { Id = Guid.NewGuid() };
        var remote = new FakeRemote();
        remote.Put("source", "file.txt", "important contents");
        await using var engine = new TransferEngine(remote, new MemoryTransferJournal(),
            new CrossConnections(source, destination), new TransferEngineOptions
            {
                CrossCopyPartSizeBytes = 8, MinimumPartBytes = 1
            });
        await engine.StartAsync();

        var jobs = await engine.EnqueueCrossCopiesAsync(source, destination, "source",
            [new DownloadItem("file.txt", false, 18)], "target", "",
            TransferConflictPolicy.Skip, move: true);
        var result = await Wait(engine, Assert.Single(jobs).Id,
            job => job.State is TransferState.Completed or TransferState.Failed);

        Assert.Equal(TransferState.Completed, result.State);
        Assert.True(result.CrossTransfer?.Move);
        Assert.Equal(1, remote.ConditionalDeletes);
        Assert.False(remote.Contains("source", "file.txt"));
        Assert.Equal("important contents", remote.Text("target", "file.txt"));
    }

    [Fact]
    public async Task Cross_move_recovers_when_source_deletion_succeeded_but_response_was_lost()
    {
        var source = SampleConnection() with { Id = Guid.NewGuid() };
        var destination = SampleConnection() with { Id = Guid.NewGuid() };
        var remote = new FakeRemote { DeleteThenFailOnce = true };
        remote.Put("source", "file.txt", "important contents");
        await using var engine = new TransferEngine(remote, new MemoryTransferJournal(),
            new CrossConnections(source, destination), new TransferEngineOptions
            { CrossCopyPartSizeBytes = 8, MinimumPartBytes = 1, MaxAttempts = 3 });
        await engine.StartAsync();
        var jobs = await engine.EnqueueCrossCopiesAsync(source, destination, "source",
            [new DownloadItem("file.txt", false, 18)], "target", "",
            TransferConflictPolicy.Skip, move: true);
        var result = await Wait(engine, Assert.Single(jobs).Id,
            job => job.State is TransferState.Completed or TransferState.Failed);
        Assert.Equal(TransferState.Completed, result.State);
        Assert.False(remote.Contains("source", "file.txt"));
        Assert.Equal("important contents", remote.Text("target", "file.txt"));
        Assert.Equal(1, remote.ConditionalDeletes);
    }

    [Fact]
    public async Task Cross_move_does_not_delete_source_when_copy_integrity_fails()
    {
        var source = SampleConnection() with { Id = Guid.NewGuid() };
        var destination = SampleConnection() with { Id = Guid.NewGuid() };
        var remote = new FakeRemote { CorruptAfterComplete = true };
        remote.Put("source", "file.txt", "important contents");
        await using var engine = new TransferEngine(remote, new MemoryTransferJournal(),
            new CrossConnections(source, destination), new TransferEngineOptions
            {
                CrossCopyPartSizeBytes = 8, MinimumPartBytes = 1
            });
        await engine.StartAsync();
        var jobs = await engine.EnqueueCrossCopiesAsync(source, destination, "source",
            [new DownloadItem("file.txt", false, 18)], "target", "",
            TransferConflictPolicy.Skip, move: true);
        var result = await Wait(engine, Assert.Single(jobs).Id,
            job => job.State is TransferState.Completed or TransferState.Failed);
        Assert.Equal(TransferState.Failed, result.State);
        Assert.Equal(0, remote.ConditionalDeletes);
        Assert.True(remote.Contains("source", "file.txt"));
    }

    [Fact]
    public async Task Cross_move_skip_does_not_delete_original_file()
    {
        var source = SampleConnection() with { Id = Guid.NewGuid() };
        var destination = SampleConnection() with { Id = Guid.NewGuid() };
        var remote = new FakeRemote();
        remote.Put("source", "item", "source data");
        remote.Put("target", "item", "existing data");
        await using var engine = new TransferEngine(remote, new MemoryTransferJournal(),
            new CrossConnections(source, destination));
        await engine.StartAsync();
        var jobs = await engine.EnqueueCrossCopiesAsync(source, destination, "source",
            [new DownloadItem("item", false, 11)], "target", "",
            TransferConflictPolicy.Skip, move: true);
        var result = await Wait(engine, Assert.Single(jobs).Id,
            job => job.State is TransferState.Completed or TransferState.Failed);
        Assert.True(result.Skipped);
        Assert.True(remote.Contains("source", "item"));
        Assert.Equal("existing data", remote.Text("target", "item"));
        Assert.Equal(0, remote.ConditionalDeletes);
    }

    [Fact]
    public async Task Cross_move_of_empty_file_creates_target_then_removes_source()
    {
        var source = SampleConnection() with { Id = Guid.NewGuid() };
        var destination = SampleConnection() with { Id = Guid.NewGuid() };
        var remote = new FakeRemote();
        remote.Put("source", "empty.txt", "");
        await using var engine = new TransferEngine(remote, new MemoryTransferJournal(),
            new CrossConnections(source, destination));
        await engine.StartAsync();
        var jobs = await engine.EnqueueCrossCopiesAsync(source, destination, "source",
            [new DownloadItem("empty.txt", false, 0)], "target", "",
            TransferConflictPolicy.Skip, move: true);
        var result = await Wait(engine, Assert.Single(jobs).Id,
            job => job.State is TransferState.Completed or TransferState.Failed);
        Assert.Equal(TransferState.Completed, result.State);
        Assert.False(remote.Contains("source", "empty.txt"));
        Assert.Equal("", remote.Text("target", "empty.txt"));
        Assert.Equal(1, remote.ConditionalDeletes);
    }

    [Fact]
    public async Task Cross_move_does_not_remove_changed_source_when_conditional_delete_races()
    {
        var source = SampleConnection() with { Id = Guid.NewGuid() };
        var destination = SampleConnection() with { Id = Guid.NewGuid() };
        var remote = new FakeRemote { MutateBeforeConditionalDelete = true };
        remote.Put("source", "file.txt", "original");
        await using var engine = new TransferEngine(remote, new MemoryTransferJournal(),
            new CrossConnections(source, destination), new TransferEngineOptions
            { CrossCopyPartSizeBytes = 8, MinimumPartBytes = 1 });
        await engine.StartAsync();
        var jobs = await engine.EnqueueCrossCopiesAsync(source, destination, "source",
            [new DownloadItem("file.txt", false, 8)], "target", "",
            TransferConflictPolicy.Skip, move: true);
        var result = await Wait(engine, Assert.Single(jobs).Id,
            job => job.State is TransferState.Completed or TransferState.Failed);
        Assert.Equal(TransferState.Failed, result.State);
        Assert.Equal("changed source", remote.Text("source", "file.txt"));
        Assert.Equal("original", remote.Text("target", "file.txt"));
        Assert.Equal(0, remote.ConditionalDeletes);
        Assert.Equal(CrossTransferStage.Removing, result.CrossTransfer?.Stage);
    }

    [Fact]
    public async Task Cross_copy_preserves_empty_folder_marker()
    {
        var source = SampleConnection() with { Id = Guid.NewGuid() };
        var destination = SampleConnection() with { Id = Guid.NewGuid() };
        var remote = new FakeRemote();
        remote.Put("source", "empty/", "");
        await using var engine = new TransferEngine(remote, new MemoryTransferJournal(),
            new CrossConnections(source, destination));
        await engine.StartAsync();
        await engine.EnqueueCrossCopiesAsync(source, destination, "source",
            [new DownloadItem("empty/", true, 0)], "target", "", TransferConflictPolicy.Skip);
        await WaitForAllCrossCopiesAsync(engine, 1);
        Assert.True(remote.Contains("target", "empty/"));
    }

    [Fact]
    public async Task Cross_move_rejects_disappeared_folders_before_queuing()
    {
        var source = SampleConnection() with { Id = Guid.NewGuid() };
        var destination = SampleConnection() with { Id = Guid.NewGuid() };
        await using var engine = new TransferEngine(new FakeRemote(), new MemoryTransferJournal(),
            new CrossConnections(source, destination));
        await engine.StartAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => engine.EnqueueCrossCopiesAsync(
            source, destination, "source", [new DownloadItem("folder/", true, null)],
            "target", "", TransferConflictPolicy.Skip, move: true));
        Assert.Empty(engine.Batches);
    }

    [Fact]
    public async Task Cross_move_folder_snapshots_all_descendants_and_preserves_structure()
    {
        var source = SampleConnection() with { Id = Guid.NewGuid() };
        var destination = SampleConnection() with { Id = Guid.NewGuid() };
        var remote = new FakeRemote();
        remote.Put("source", "photos/a.txt", "first");
        remote.Put("source", "photos/sub/b.txt", "second");
        remote.Put("source", "photos/empty/", "");
        await using var engine = new TransferEngine(remote, new MemoryTransferJournal(),
            new CrossConnections(source, destination), new TransferEngineOptions
            { CrossCopyPartSizeBytes = 8, MinimumPartBytes = 1 });
        await engine.StartAsync();

        await engine.EnqueueCrossCopiesAsync(source, destination, "source",
            [new DownloadItem("photos/", true, null)], "target", "archive/",
            TransferConflictPolicy.Skip, move: true);
        await WaitForAllCrossCopiesAsync(engine, 3);

        Assert.Equal("first", remote.Text("target", "archive/photos/a.txt"));
        Assert.Equal("second", remote.Text("target", "archive/photos/sub/b.txt"));
        Assert.True(remote.Contains("target", "archive/photos/empty/"));
        Assert.False(remote.Contains("source", "photos/a.txt"));
        Assert.False(remote.Contains("source", "photos/sub/b.txt"));
        Assert.False(remote.Contains("source", "photos/empty/"));
        Assert.Equal(3, remote.ConditionalDeletes);
    }

    [Fact]
    public async Task Cross_move_folder_rejects_descendant_destination_before_queuing()
    {
        var source = SampleConnection() with { Id = Guid.NewGuid() };
        var destination = source with { Id = Guid.NewGuid() };
        var remote = new FakeRemote();
        remote.Put("source", "photos/a.txt", "first");
        await using var engine = new TransferEngine(remote, new MemoryTransferJournal(),
            new CrossConnections(source, destination));
        await engine.StartAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => engine.EnqueueCrossCopiesAsync(
            source, destination, "source", [new DownloadItem("photos/", true, null)],
            "source", "photos/nested/", TransferConflictPolicy.Skip, move: true));
        Assert.True(remote.Contains("source", "photos/a.txt"));
        Assert.Empty(engine.Batches);
    }

    [Fact]
    public async Task Cross_folder_move_rejects_ambiguous_nested_selection_before_mutation()
    {
        var source = SampleConnection() with { Id = Guid.NewGuid() };
        var destination = SampleConnection() with { Id = Guid.NewGuid() };
        var remote = new FakeRemote();
        remote.Put("source", "folder/a.txt", "original");
        await using var engine = new TransferEngine(remote, new MemoryTransferJournal(),
            new CrossConnections(source, destination));
        await engine.StartAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => engine.EnqueueCrossCopiesAsync(
            source, destination, "source",
            [new DownloadItem("folder/", true, null), new DownloadItem("folder/a.txt", false, 8)],
            "target", "archive/", TransferConflictPolicy.Skip, move: true));
        Assert.Empty(engine.Batches);
        Assert.Equal("original", remote.Text("source", "folder/a.txt"));
    }

    [Fact]
    public async Task Cross_folder_move_limit_is_checked_before_creating_any_jobs()
    {
        var source = SampleConnection() with { Id = Guid.NewGuid() };
        var destination = SampleConnection() with { Id = Guid.NewGuid() };
        var remote = new FakeRemote();
        remote.Put("source", "folder/a.txt", "a");
        remote.Put("source", "folder/b.txt", "b");
        await using var engine = new TransferEngine(remote, new MemoryTransferJournal(),
            new CrossConnections(source, destination), new TransferEngineOptions
            { MaxCrossMoveManifestObjects = 1 });
        await engine.StartAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => engine.EnqueueCrossCopiesAsync(
            source, destination, "source", [new DownloadItem("folder/", true, null)],
            "target", "", TransferConflictPolicy.Skip, move: true));
        Assert.Empty(engine.Batches);
        Assert.True(remote.Contains("source", "folder/a.txt"));
        Assert.True(remote.Contains("source", "folder/b.txt"));
        Assert.False(remote.Contains("target", "folder/a.txt"));
    }

    [Fact]
    public async Task Cross_copy_keep_both_uses_shared_sibling_naming()
    {
        var source = SampleConnection() with { Id = Guid.NewGuid(), IsReadOnly = true };
        var destination = SampleConnection() with { Id = Guid.NewGuid() };
        var remote = new FakeRemote();
        remote.Put("source", "logo.png", "new");
        remote.Put("target", "logo.png", "old");
        remote.Put("target", "logo (2).png", "another");
        await using var engine = new TransferEngine(remote, new MemoryTransferJournal(),
            new CrossConnections(source, destination));
        await engine.StartAsync();

        var jobs = await engine.EnqueueCrossCopiesAsync(source, destination, "source",
            [new DownloadItem("logo.png", false, 3)], "target", "", TransferConflictPolicy.Rename);
        var result = await Wait(engine, Assert.Single(jobs).Id,
            job => job.State is TransferState.Completed or TransferState.Failed);

        Assert.Equal(TransferState.Completed, result.State);
        Assert.Equal("logo (3).png", result.Key);
        Assert.Equal("new", remote.Text("target", "logo (3).png"));
        Assert.Equal("old", remote.Text("target", "logo.png"));
        Assert.Equal("another", remote.Text("target", "logo (2).png"));
    }

    [Fact]
    public async Task Cross_copy_keep_both_renames_folder_root_not_individual_objects()
    {
        var source = SampleConnection() with { Id = Guid.NewGuid() };
        var destination = SampleConnection() with { Id = Guid.NewGuid() };
        var remote = new FakeRemote();
        remote.Put("source", "photos/a.txt", "a");
        remote.Put("source", "photos/sub/b.txt", "b");
        remote.Put("target", "archive/photos/existing.txt", "old");
        await using var engine = new TransferEngine(remote, new MemoryTransferJournal(),
            new CrossConnections(source, destination));
        await engine.StartAsync();

        await engine.EnqueueCrossCopiesAsync(source, destination, "source",
            [new DownloadItem("photos/", true, 0)], "target", "archive/",
            TransferConflictPolicy.Rename);
        await WaitForAllCrossCopiesAsync(engine, 2);

        Assert.Equal("a", remote.Text("target", "archive/photos (2)/a.txt"));
        Assert.Equal("b", remote.Text("target", "archive/photos (2)/sub/b.txt"));
        Assert.Equal("old", remote.Text("target", "archive/photos/existing.txt"));
    }

    [Fact]
    public async Task Cross_copy_of_same_object_creates_sibling_even_with_skip_policy()
    {
        var source = SampleConnection() with { Id = Guid.NewGuid() };
        var destination = source with { Id = Guid.NewGuid() };
        var remote = new FakeRemote();
        remote.Put("source", "file.txt", "contents");
        await using var engine = new TransferEngine(remote, new MemoryTransferJournal(),
            new CrossConnections(source, destination));
        await engine.StartAsync();

        var jobs = await engine.EnqueueCrossCopiesAsync(source, destination, "source",
            [new DownloadItem("file.txt", false, 8)], "source", "",
            TransferConflictPolicy.Skip);
        var result = await Wait(engine, Assert.Single(jobs).Id,
            job => job.State is TransferState.Completed or TransferState.Failed);

        Assert.Equal(TransferState.Completed, result.State);
        Assert.Equal("file (2).txt", result.Key);
        Assert.Equal("contents", remote.Text("source", "file.txt"));
        Assert.Equal("contents", remote.Text("source", "file (2).txt"));
    }

    [Fact]
    public async Task Cross_copy_conflict_probe_reports_the_first_matching_root()
    {
        var source = SampleConnection() with { Id = Guid.NewGuid() };
        var destination = SampleConnection() with { Id = Guid.NewGuid() };
        var remote = new FakeRemote();
        remote.Put("target", "archive/photos/old.txt", "old");
        await using var engine = new TransferEngine(remote, new MemoryTransferJournal(),
            new CrossConnections(source, destination));
        await engine.StartAsync();

        var name = await engine.FindCrossCopyConflictAsync(source, destination, "source",
            [new DownloadItem("photos/", true, 0)], "target", "archive/");

        Assert.Equal("photos", name);
    }

    [Fact]
    public async Task Cross_batch_created_callback_reports_a_durable_batch_before_objects_are_queued()
    {
        var source = SampleConnection() with { Id = Guid.NewGuid() };
        var destination = SampleConnection() with { Id = Guid.NewGuid() };
        var remote = new FakeRemote();
        remote.Put("source", "file.txt", "data");
        await using var engine = new TransferEngine(remote, new MemoryTransferJournal(),
            new CrossConnections(source, destination));
        await engine.StartAsync();

        Guid created = Guid.Empty;
        var stateAtCreation = CrossBatchState.Completed;
        var jobsAtCreation = -1;
        var jobs = await engine.EnqueueCrossCopiesAsync(source, destination, "source",
            [new DownloadItem("file.txt", false, 4)], "target", "",
            TransferConflictPolicy.Skip,
            batchCreated: id =>
            {
                created = id;
                stateAtCreation = Assert.Single(engine.Batches).State;
                jobsAtCreation = engine.Jobs.Count(job => job.BatchId == id);
            });

        Assert.NotEqual(Guid.Empty, created);
        Assert.Equal(CrossBatchState.Scanning, stateAtCreation);
        Assert.Equal(0, jobsAtCreation);
        Assert.Equal(created, Assert.Single(jobs).BatchId);
    }

    [Fact]
    public async Task Empty_cross_batch_still_reports_its_id_and_completed_state()
    {
        var source = SampleConnection() with { Id = Guid.NewGuid() };
        var destination = SampleConnection() with { Id = Guid.NewGuid() };
        await using var engine = new TransferEngine(new FakeRemote(), new MemoryTransferJournal(),
            new CrossConnections(source, destination));
        await engine.StartAsync();

        Guid created = Guid.Empty;
        var jobs = await engine.EnqueueCrossCopiesAsync(source, destination, "source",
            [], "target", "", TransferConflictPolicy.Skip,
            batchCreated: id => created = id);

        Assert.Empty(jobs);
        Assert.NotEqual(Guid.Empty, created);
        var batch = Assert.Single(engine.Batches);
        Assert.Equal(created, batch.Id);
        Assert.Equal(CrossBatchState.Completed, batch.State);
        var progress = await engine.GetBatchProgressAsync(created);
        Assert.Equal(0, progress.Queued);
        Assert.Equal(0, progress.Percent);
    }

    [Fact]
    public async Task Failed_folder_scan_preserves_batch_id_and_emits_failure_status()
    {
        var source = SampleConnection() with { Id = Guid.NewGuid(), IsReadOnly = true };
        var destination = SampleConnection() with { Id = Guid.NewGuid() };
        var remote = new FakeRemote { FailListingOnce = true };
        remote.Put("source", "folder/a.txt", "one");
        await using var engine = new TransferEngine(remote, new MemoryTransferJournal(),
            new CrossConnections(source, destination));
        await engine.StartAsync();
        var states = new System.Collections.Concurrent.ConcurrentQueue<CrossBatchState>();
        engine.BatchChanged += batch => states.Enqueue(batch.State);

        Guid created = Guid.Empty;
        await Assert.ThrowsAsync<StorageException>(() => engine.EnqueueCrossCopiesAsync(
            source, destination, "source", [new DownloadItem("folder/", true, null)],
            "target", "", TransferConflictPolicy.Skip, batchCreated: id => created = id));

        Assert.NotEqual(Guid.Empty, created);
        var failed = Assert.Single(engine.Batches);
        Assert.Equal(created, failed.Id);
        Assert.Equal(CrossBatchState.Failed, failed.State);
        Assert.Contains(CrossBatchState.Scanning, states);
        Assert.Contains(CrossBatchState.Failed, states);
    }

    [Fact]
    public async Task Cross_copy_skip_preserves_existing_destination()
    {
        var source = SampleConnection() with { Id = Guid.NewGuid(), IsReadOnly = true };
        var destination = SampleConnection() with { Id = Guid.NewGuid() };
        var remote = new FakeRemote();
        remote.Put("source", "item", "new");
        remote.Put("target", "item", "keep");
        await using var engine = new TransferEngine(remote, new MemoryTransferJournal(),
            new CrossConnections(source, destination));
        await engine.StartAsync();

        var jobs = await engine.EnqueueCrossCopiesAsync(source, destination, "source",
            [new DownloadItem("item", false, 3)], "target", "", TransferConflictPolicy.Skip);
        var result = await Wait(engine, Assert.Single(jobs).Id, job => job.State == TransferState.Completed);

        Assert.True(result.Skipped);
        Assert.Equal("keep", remote.Text("target", "item"));
        Assert.Equal(0, remote.PartCalls);
    }

    [Fact]
    public async Task Cross_copy_detects_same_size_destination_corruption()
    {
        var source = SampleConnection() with { Id = Guid.NewGuid(), IsReadOnly = true };
        var destination = SampleConnection() with { Id = Guid.NewGuid() };
        var remote = new FakeRemote { CorruptAfterComplete = true };
        remote.Put("source", "item", "data");
        await using var engine = new TransferEngine(remote, new MemoryTransferJournal(),
            new CrossConnections(source, destination), new TransferEngineOptions
            {
                CrossCopyPartSizeBytes = 2, MinimumPartBytes = 1
            });
        await engine.StartAsync();

        var jobs = await engine.EnqueueCrossCopiesAsync(source, destination, "source",
            [new DownloadItem("item", false, 4)], "target", "", TransferConflictPolicy.Skip);
        var result = await Wait(engine, Assert.Single(jobs).Id, job => job.State == TransferState.Failed);

        Assert.Equal(TransferFailureKind.Integrity, result.Failure?.Kind);
        Assert.Equal("DestinationChecksumMismatch", result.Failure?.Code);
        Assert.Equal("data", remote.Text("source", "item"));
    }

    [Fact]
    public async Task Cross_copy_reuses_committed_part_after_transient_failure()
    {
        var source = SampleConnection() with { Id = Guid.NewGuid(), IsReadOnly = true };
        var destination = SampleConnection() with { Id = Guid.NewGuid() };
        var remote = new FakeRemote { FailPartNumber = 2 };
        remote.Put("source", "blob", "abcdefghijklmnopqrst");
        await using var engine = new TransferEngine(remote, new MemoryTransferJournal(),
            new CrossConnections(source, destination), new TransferEngineOptions
            {
                CrossCopyPartSizeBytes = 8, MinimumPartBytes = 1
            }, new ImmediateClock());
        await engine.StartAsync();

        var jobs = await engine.EnqueueCrossCopiesAsync(source, destination, "source",
            [new DownloadItem("blob", false, 20)], "target", "", TransferConflictPolicy.Skip);
        var result = await Wait(engine, Assert.Single(jobs).Id, job => job.State == TransferState.Completed);

        Assert.Equal(20, result.BytesTransferred);
        Assert.Equal(4, remote.PartCalls);
        Assert.Equal("abcdefghijklmnopqrst", remote.Text("target", "blob"));
    }

    [Fact]
    public async Task Cross_copy_spools_large_parts_with_bounded_temporary_space()
    {
        var source = SampleConnection() with { Id = Guid.NewGuid(), IsReadOnly = true };
        var destination = SampleConnection() with { Id = Guid.NewGuid() };
        var remote = new FakeRemote();
        remote.Put("source", "blob", "abcdefghijklmnopqrst");
        await using var engine = new TransferEngine(remote, new MemoryTransferJournal(),
            new CrossConnections(source, destination), new TransferEngineOptions
            {
                CrossCopyPartSizeBytes = 8, MinimumPartBytes = 1,
                CrossCopyMemoryBudgetBytes = 8, CrossCopyTempBudgetBytes = 16
            });
        await engine.StartAsync();

        var jobs = await engine.EnqueueCrossCopiesAsync(source, destination, "source",
            [new DownloadItem("blob", false, 20)], "target", "", TransferConflictPolicy.Skip);
        await Wait(engine, Assert.Single(jobs).Id, job => job.State == TransferState.Completed);

        Assert.All(remote.PartStreams, stream => Assert.IsType<FileStream>(stream));
        Assert.All(remote.PartStreams.Cast<FileStream>(), stream => Assert.False(File.Exists(stream.Name)));
        Assert.Equal("abcdefghijklmnopqrst", remote.Text("target", "blob"));
    }

    [Fact]
    public async Task Lost_multipart_completion_response_is_reconciled_by_checksum()
    {
        var source = SampleConnection() with { Id = Guid.NewGuid(), IsReadOnly = true };
        var destination = SampleConnection() with { Id = Guid.NewGuid() };
        var remote = new FakeRemote { CompleteThenFail = true };
        remote.Put("source", "blob", "abcdefghijklmnopqrst");
        await using var engine = new TransferEngine(remote, new MemoryTransferJournal(),
            new CrossConnections(source, destination), new TransferEngineOptions
            {
                CrossCopyPartSizeBytes = 8, MinimumPartBytes = 1
            }, new ImmediateClock());
        await engine.StartAsync();

        var jobs = await engine.EnqueueCrossCopiesAsync(source, destination, "source",
            [new DownloadItem("blob", false, 20)], "target", "", TransferConflictPolicy.Skip);
        var result = await Wait(engine, Assert.Single(jobs).Id, job => job.State == TransferState.Completed);

        Assert.Equal("SHA256", result.CrossTransfer?.Verification);
        Assert.Equal(3, remote.PartCalls);
        Assert.Equal("abcdefghijklmnopqrst", remote.Text("target", "blob"));
    }

    [Fact]
    public async Task Changed_source_during_range_copy_never_completes_destination()
    {
        var source = SampleConnection() with { Id = Guid.NewGuid(), IsReadOnly = true };
        var destination = SampleConnection() with { Id = Guid.NewGuid() };
        var remote = new FakeRemote { MutateSourceOnSecondRange = true };
        remote.Put("source", "blob", "abcdefghijklmnopqrst");
        await using var engine = new TransferEngine(remote, new MemoryTransferJournal(),
            new CrossConnections(source, destination), new TransferEngineOptions
            {
                CrossCopyPartSizeBytes = 8, MinimumPartBytes = 1
            });
        await engine.StartAsync();

        var jobs = await engine.EnqueueCrossCopiesAsync(source, destination, "source",
            [new DownloadItem("blob", false, 20)], "target", "", TransferConflictPolicy.Skip);
        var result = await Wait(engine, Assert.Single(jobs).Id, job => job.State == TransferState.Failed);

        Assert.Equal("SourceChanged", result.Failure?.Code);
        Assert.False(remote.Contains("target", "blob"));
        Assert.Equal(0, remote.PendingUploads);
        Assert.True(remote.AbortCalls > 0);
    }

    [Fact]
    public async Task Failed_multipart_cleanup_is_retried_after_restart()
    {
        var source = SampleConnection() with { Id = Guid.NewGuid(), IsReadOnly = true };
        var destination = SampleConnection() with { Id = Guid.NewGuid() };
        var remote = new FakeRemote { MutateSourceOnSecondRange = true, FailAbortOnce = true };
        remote.Put("source", "blob", "abcdefghijklmnopqrst");
        var journal = new MemoryTransferJournal();
        var options = new TransferEngineOptions { CrossCopyPartSizeBytes = 8, MinimumPartBytes = 1 };
        Guid id;
        await using (var first = new TransferEngine(remote, journal,
            new CrossConnections(source, destination), options))
        {
            await first.StartAsync();
            var jobs = await first.EnqueueCrossCopiesAsync(source, destination, "source",
                [new DownloadItem("blob", false, 20)], "target", "", TransferConflictPolicy.Skip);
            id = Assert.Single(jobs).Id;
            var failed = await Wait(first, id, job => job.State == TransferState.Failed);
            Assert.True(failed.CrossTransfer?.CleanupPending);
            Assert.Equal(1, remote.PendingUploads);
        }
        await using var second = new TransferEngine(remote, journal,
            new CrossConnections(source, destination), options);
        await second.StartAsync();
        await Wait(second, id, job => job.CrossTransfer?.CleanupPending == false);
        Assert.Equal(0, remote.PendingUploads);
        Assert.False(remote.Contains("target", "blob"));
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    public async Task Paused_cross_copy_accepts_source_read_only_changes_but_rejects_backend_changes(
        bool legacyJournal, bool legacyReadOnly, bool changeBackend)
    {
        var source = SampleConnection() with { Id = Guid.NewGuid() };
        var destination = SampleConnection() with { Id = Guid.NewGuid() };
        var remote = new FakeRemote { BlockParts = true };
        remote.Put("source", "blob", "abcdefghijklmnopqrst");
        var journal = new MemoryTransferJournal();
        var options = new TransferEngineOptions { CrossCopyPartSizeBytes = 8, MinimumPartBytes = 1 };
        Guid id;
        await using (var first = new TransferEngine(remote, journal,
            new CrossConnections(source, destination), options))
        {
            await first.StartAsync();
            var jobs = await first.EnqueueCrossCopiesAsync(source, destination, "source",
                [new DownloadItem("blob", false, 20)], "target", "", TransferConflictPolicy.Skip);
            id = Assert.Single(jobs).Id;
            await remote.PartEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await first.PauseAsync(id);
            Assert.Equal(TransferState.Paused, first.Current(id).State);
            if (legacyJournal)
            {
                var old = source with { IsReadOnly = legacyReadOnly };
                var value = string.Join('\n', old.Provider, old.Endpoint, old.Region,
                    old.CredentialReference, old.IsReadOnly, old.RestrictedBucket,
                    old.KeyPrefix, old.ForcePathStyle, old.AccountId);
                var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
                var paused = first.Current(id);
                await first.PublishAsync(paused with
                {
                    CrossTransfer = paused.CrossTransfer! with { SourceConfiguration = fingerprint }
                }, CancellationToken.None);
            }
        }
        remote.BlockParts = false;
        remote.PartRelease.TrySetResult();
        var updated = source with
        {
            IsReadOnly = true,
            Endpoint = changeBackend ? "http://different-backend:9000" : source.Endpoint
        };
        await using var second = new TransferEngine(remote, journal,
            new CrossConnections(updated, destination), options);
        await second.StartAsync();
        Assert.Equal(TransferState.Paused, second.Current(id).State);
        await second.ResumeAsync(id);
        var result = await Wait(second, id, job => job.State is TransferState.Completed or TransferState.Failed);
        if (changeBackend)
        {
            Assert.Equal(TransferState.Failed, result.State);
            Assert.Equal(TransferFailureKind.ConnectionMissing, result.Failure?.Kind);
            Assert.False(remote.Contains("target", "blob"));
            return;
        }
        Assert.Equal(TransferState.Completed, result.State);
        Assert.Equal("SHA256", result.CrossTransfer?.Verification);
        Assert.Equal("abcdefghijklmnopqrst", remote.Text("target", "blob"));
    }

    [Fact]
    public async Task Restart_resumes_folder_scan_from_persisted_page_cursor()
    {
        var source = SampleConnection() with { Id = Guid.NewGuid(), IsReadOnly = true };
        var destination = SampleConnection() with { Id = Guid.NewGuid() };
        var remote = new FakeRemote();
        for (var index = 0; index < 3; index++)
            remote.Put("source", $"folder/item{index:0000}", "x");
        remote.Put("target", "copies/folder/item0000", "x");
        var journal = new MemoryTransferJournal();
        var batchId = Guid.NewGuid();
        var first = TransferJob.Create(TransferKind.Copy, destination.Id, "target",
            "copies/folder/item0000", "", 1, TransferConflictPolicy.Skip, batchId) with
        {
            State = TransferState.Completed,
            CrossTransfer = new CrossTransferSpec(new StorageObjectRef(source.Id, "source", "folder/item0000"),
                new StorageObjectRef(destination.Id, "target", "copies/folder/item0000"),
                TransferConflictPolicy.Skip)
        };
        Assert.True(await journal.TryCreateCrossJobAsync(first, CancellationToken.None));
        var batch = new CrossTransferBatch(batchId, source.Id, destination.Id, "source", "target",
            "copies/", [new DownloadItem("folder/", true, null)], TransferConflictPolicy.Skip,
            0, "folder/item0000", "folder/item0000", 1, CrossBatchState.Scanning,
            null, DateTimeOffset.UtcNow);
        await journal.UpsertBatchAsync(batch, CancellationToken.None);
        await using var engine = new TransferEngine(remote, journal,
            new CrossConnections(source, destination), new TransferEngineOptions
            {
                CrossCopyPartSizeBytes = 8, MinimumPartBytes = 1
            });
        await engine.StartAsync();
        await WaitForAllCrossCopiesAsync(engine, 3);

        Assert.Equal(CrossBatchState.Completed, Assert.Single(engine.Batches).State);
        Assert.Equal(3, (await engine.GetBatchProgressAsync(batchId)).Completed);
        Assert.Equal("x", remote.Text("target", "copies/folder/item0002"));
    }

    [Fact]
    public async Task Failed_batch_scan_can_retry_without_duplicate_jobs()
    {
        var source = SampleConnection() with { Id = Guid.NewGuid(), IsReadOnly = true };
        var destination = SampleConnection() with { Id = Guid.NewGuid() };
        var remote = new FakeRemote { FailListingOnce = true };
        remote.Put("source", "folder/one", "1");
        remote.Put("source", "folder/two", "2");
        await using var engine = new TransferEngine(remote, new MemoryTransferJournal(),
            new CrossConnections(source, destination), new TransferEngineOptions
            {
                CrossCopyPartSizeBytes = 8, MinimumPartBytes = 1
            });
        await engine.StartAsync();

        await Assert.ThrowsAsync<StorageException>(() => engine.EnqueueCrossCopiesAsync(
            source, destination, "source", [new DownloadItem("folder/", true, null)],
            "target", "copies/", TransferConflictPolicy.Skip));
        var failed = Assert.Single(engine.Batches);
        Assert.Equal(CrossBatchState.Failed, failed.State);
        await engine.RetryBatchAsync(failed.Id);
        await WaitForAllCrossCopiesAsync(engine, 2);

        Assert.Equal(CrossBatchState.Completed, Assert.Single(engine.Batches).State);
        Assert.Equal(2, (await engine.GetBatchProgressAsync(failed.Id)).Completed);
        Assert.Equal("2", remote.Text("target", "copies/folder/two"));
    }

    [Fact]
    public async Task Batch_retry_requeues_failed_items_without_touching_completed_items()
    {
        var source = SampleConnection() with { Id = Guid.NewGuid(), IsReadOnly = true };
        var destination = SampleConnection() with { Id = Guid.NewGuid() };
        var remote = new FakeRemote { DenyParts = true };
        remote.Put("source", "folder/one", "1");
        remote.Put("source", "folder/two", "2");
        await using var engine = new TransferEngine(remote, new MemoryTransferJournal(),
            new CrossConnections(source, destination), new TransferEngineOptions
            {
                CrossCopyPartSizeBytes = 8, MinimumPartBytes = 1
            });
        await engine.StartAsync();

        var jobs = await engine.EnqueueCrossCopiesAsync(source, destination, "source",
            [new DownloadItem("folder/", true, null)], "target", "copies/",
            TransferConflictPolicy.Skip);
        await WaitForAllCrossCopiesAsync(engine, 2);
        var batchId = Assert.Single(engine.Batches).Id;
        Assert.Equal(2, (await engine.GetBatchProgressAsync(batchId)).Failed);
        remote.DenyParts = false;
        await engine.RetryBatchAsync(batchId);
        await WaitForAllCrossCopiesAsync(engine, 2);

        Assert.Equal(2, (await engine.GetBatchProgressAsync(batchId)).Completed);
        Assert.Equal(2, engine.Jobs.Count(job => job.Kind == TransferKind.Copy));
        Assert.NotEmpty(jobs);
    }

    [Fact]
    public async Task Cross_folder_scan_preserves_all_objects_across_listing_pages()
    {
        var source = SampleConnection() with { Id = Guid.NewGuid(), IsReadOnly = true };
        var destination = SampleConnection() with { Id = Guid.NewGuid() };
        var remote = new FakeRemote();
        for (var index = 0; index < 1002; index++)
            remote.Put("source", $"folder/item{index:0000}", "x");
        remote.Put("source", "folder/empty/", "");
        await using var engine = new TransferEngine(remote, new MemoryTransferJournal(),
            new CrossConnections(source, destination), new TransferEngineOptions
            {
                CrossCopyPartSizeBytes = 8, MinimumPartBytes = 1
            });
        await engine.StartAsync();

        await engine.EnqueueCrossCopiesAsync(source, destination, "source",
            [new DownloadItem("folder/", true, null)], "target", "copies/",
            TransferConflictPolicy.Skip);
        await WaitForAllCrossCopiesAsync(engine, 1003);

        Assert.Equal(1003, engine.Jobs.Count(job => job.Kind == TransferKind.Copy));
        Assert.Equal(1003, engine.Jobs.Count(job => job.Kind == TransferKind.Copy
            && job.State == TransferState.Completed));
        Assert.True(remote.Contains("target", "copies/folder/empty/"));
        Assert.Equal("x", remote.Text("target", "copies/folder/item1001"));
    }

    private static async Task WaitForAllCrossCopiesAsync(TransferEngine engine, int expected)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Changed(TransferJob _) => Check();
        void Check()
        {
            var jobs = engine.Jobs.Where(job => job.Kind == TransferKind.Copy).ToList();
            if (jobs.Count == expected && jobs.All(job => job.State is TransferState.Completed or TransferState.Failed))
                done.TrySetResult();
        }
        engine.Changed += Changed;
        try
        {
            Check();
            try
            {
                await done.Task.WaitAsync(TimeSpan.FromSeconds(30));
            }
            catch (TimeoutException)
            {
                var states = engine.Jobs.Where(job => job.Kind == TransferKind.Copy)
                    .GroupBy(job => job.State).Select(group => $"{group.Key}:{group.Count()}");
                throw new TimeoutException($"Expected {expected}, got {engine.Jobs.Count(job => job.Kind == TransferKind.Copy)}; {string.Join(", ", states)}");
            }
        }
        finally
        {
            engine.Changed -= Changed;
        }
    }
    [Fact]
    public async Task Upload_preflight_only_reports_actual_destination_collision()
    {
        await using var fx = await Fixture.Start();
        var file = fx.Write("photo.png", "new");

        Assert.Null(await fx.Engine.FindUploadConflictAsync(
            fx.Connection, "bucket", "assets", [file]));
        fx.Remote.Put("bucket", "assets/photo.png", "existing");
        Assert.Equal("photo.png", await fx.Engine.FindUploadConflictAsync(
            fx.Connection, "bucket", "assets", [file]));
        Assert.Equal("existing", fx.Remote.Text("bucket", "assets/photo.png"));
        Assert.Empty(fx.Engine.Jobs);
    }

    [Fact]
    public async Task Download_preflight_uses_the_same_local_paths_as_download_jobs()
    {
        await using var fx = await Fixture.Start();
        fx.Remote.Put("bucket", "folder/report.txt", "remote");
        var destination = fx.MakeDirectory("incoming");
        var items = new[] { new DownloadItem("folder/", true, null) };

        Assert.Null(await fx.Engine.FindDownloadConflictAsync(
            fx.Connection, "bucket", items, destination));
        await File.WriteAllTextAsync(Path.Combine(destination, "report.txt"), "local");
        Assert.Equal("report.txt", await fx.Engine.FindDownloadConflictAsync(
            fx.Connection, "bucket", items, destination));
        Assert.Equal("local", await File.ReadAllTextAsync(Path.Combine(destination, "report.txt")));
        Assert.Empty(fx.Engine.Jobs);
    }

    [Fact]
    public async Task Single_file_download_preflight_maps_full_key()
    {
        await using var fx = await Fixture.Start();
        var destination = fx.MakeDirectory("incoming");
        var folder = Path.Combine(destination, "folder");
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, "photo.png"), "local");

        Assert.Equal("photo.png", await fx.Engine.FindDownloadConflictAsync(
            fx.Connection, "bucket", [new DownloadItem("folder/photo.png", false, 5)], destination));
        Assert.Empty(fx.Engine.Jobs);
    }

    [Fact]
    public async Task Removing_a_failed_transfer_clears_the_engine_without_deleting_remote_objects()
    {
        await using var fx = await Fixture.Start();
        fx.Remote.DenyPuts = true;
        var file = fx.Write("failure.txt", "hello");
        var queued = await fx.Engine.EnqueueUploadsAsync(fx.Connection, "bucket", "",
            [file], TransferConflictPolicy.Overwrite);
        var failed = await fx.Wait(queued[0].Id, job => job.State == TransferState.Failed);
        Assert.Equal(TransferFailureKind.AccessDenied, failed.Failure!.Kind);
        var removed = new List<Guid>();
        fx.Engine.Removed += removed.Add;

        var result = await fx.Engine.RemoveTerminalJobAsync(failed.Id);
        Assert.Contains(failed.Id, result.JobIds);
        Assert.Contains(failed.Id, removed);
        Assert.Null(fx.Engine.TryCurrent(failed.Id));
        Assert.True(File.Exists(file));
        Assert.DoesNotContain(fx.Engine.Jobs, job => job.Id == failed.Id);
        // A retry button command racing after deletion must not revive the record.
        await fx.Engine.RetryAsync(failed.Id);
        Assert.Null(fx.Engine.TryCurrent(failed.Id));
    }

    [Fact]
    public async Task Small_upload_is_verified_and_not_buffered_whole()
    {
        await using var fx = await Fixture.Start();
        var file = fx.Write("photo.png", "hello");

        var jobs = await fx.Engine.EnqueueUploadsAsync(fx.Connection, "bucket", "assets", [file], TransferConflictPolicy.Overwrite);
        var done = await fx.Wait(jobs[0].Id, job => job.State == TransferState.Completed);

        Assert.False(done.Skipped);
        Assert.Equal("assets/photo.png", done.Key);
        Assert.Equal("hello", fx.Remote.Text("bucket", "assets/photo.png"));
        Assert.False(fx.Remote.SawMemoryStream);
        Assert.Equal(ContentTypes.FromPath(file), done.ContentType);
    }

    [Fact]
    public async Task Successful_put_response_does_not_hide_a_truncated_remote_object()
    {
        await using var fx = await Fixture.Start();
        fx.Remote.TruncatePuts = true;
        var file = fx.Write("photo.png", "hello-world");

        var jobs = await fx.Engine.EnqueueUploadsAsync(fx.Connection, "bucket", "", [file], TransferConflictPolicy.Overwrite);
        var failed = await fx.Wait(jobs[0].Id, job => job.State == TransferState.Failed);

        Assert.Equal(TransferFailureKind.Integrity, failed.Failure!.Kind);
        Assert.Equal("Checksum", failed.Failure.Code);
    }


    [Fact]
    public async Task Upload_retry_does_not_adopt_an_unrelated_remote_destination()
    {
        await using var fx = await Fixture.Start();
        fx.Remote.FailPutsRemaining = 1;
        fx.Remote.PlantForeignBeforeSecondPutCondition = true;
        var file = fx.Write("photo.png", "uploaded");

        var jobs = await fx.Engine.EnqueueUploadsAsync(fx.Connection, "bucket", "",
            [file], TransferConflictPolicy.Overwrite);
        var result = await fx.Wait(jobs[0].Id, j => j.State == TransferState.Failed);

        Assert.Equal(TransferFailureKind.Conflict, result.Failure!.Kind);
        Assert.Equal("foreign", fx.Remote.Text("bucket", "photo.png"));
        Assert.Equal(2, fx.Remote.ConditionalPutCalls);
    }

    [Fact]
    public async Task Upload_skip_does_not_overwrite_an_object_created_after_head()
    {
        await using var fx = await Fixture.Start();
        fx.Remote.PlantForeignBeforePutCondition = true;
        var file = fx.Write("photo.png", "uploaded");
        var jobs = await fx.Engine.EnqueueUploadsAsync(fx.Connection, "bucket", "",
            [file], TransferConflictPolicy.Skip);

        var result = await fx.Wait(jobs[0].Id, j => j.State == TransferState.Failed);
        Assert.Equal(TransferFailureKind.Conflict, result.Failure!.Kind);
        Assert.Equal("foreign", fx.Remote.Text("bucket", "photo.png"));
        Assert.Equal(1, fx.Remote.ConditionalPutCalls);
    }

    [Fact]
    public async Task Unsupported_conditional_multipart_stops_before_creating_an_upload()
    {
        await using var fx = await Fixture.Start(multipartThreshold: 4, partSize: 8, minimumPart: 1);
        fx.Remote.RejectConditionalMultipart = true;
        var file = fx.Write("photo.png", new string('a', 20));
        var jobs = await fx.Engine.EnqueueUploadsAsync(fx.Connection, "bucket", "",
            [file], TransferConflictPolicy.Skip);

        var result = await fx.Wait(jobs[0].Id, job => job.State == TransferState.Failed);
        Assert.Equal(TransferFailureKind.Unsupported, result.Failure?.Kind);
        Assert.Equal("ConditionalMultipartUnsupported", result.Failure?.Code);
        Assert.Equal(0, fx.Remote.PendingUploads);
        Assert.Equal(0, fx.Remote.PartCalls);
    }

    [Fact]
    public async Task Multipart_upload_does_not_overwrite_an_object_created_before_completion()
    {
        await using var fx = await Fixture.Start(multipartThreshold: 4, partSize: 8, minimumPart: 1);
        fx.Remote.PlantForeignBeforeCompletionCondition = true;
        var file = fx.Write("photo.png", new string('a', 20));
        var jobs = await fx.Engine.EnqueueUploadsAsync(fx.Connection, "bucket", "",
            [file], TransferConflictPolicy.Skip);

        var result = await fx.Wait(jobs[0].Id, j => j.State == TransferState.Failed);
        Assert.Equal(TransferFailureKind.Conflict, result.Failure!.Kind);
        Assert.Equal("foreign", fx.Remote.Text("bucket", "photo.png"));
        Assert.Equal(1, fx.Remote.ConditionalCompleteCalls);
    }

    [Fact]
    public async Task Download_overwrites_an_unchanged_existing_destination_when_explicitly_requested()
    {
        await using var fx = await Fixture.Start();
        fx.Remote.Put("bucket", "photo.png", "new-download");
        var directory = fx.MakeDirectory("in");
        var path = Path.Combine(directory, "photo.png");
        await File.WriteAllTextAsync(path, "original");

        var jobs = await fx.Engine.EnqueueDownloadsAsync(fx.Connection, "bucket",
            [new DownloadItem("photo.png", false, null)], directory, TransferConflictPolicy.Overwrite);
        var result = await fx.Wait(jobs[0].Id, j => j.State is TransferState.Completed or TransferState.Failed);
        Assert.Equal(TransferState.Completed, result.State);
        Assert.Equal("new-download", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task Skip_does_not_replace_an_existing_object()
    {
        await using var fx = await Fixture.Start();
        fx.Remote.Put("bucket", "assets/photo.png", "keep");
        var file = fx.Write("photo.png", "new");

        var jobs = await fx.Engine.EnqueueUploadsAsync(fx.Connection, "bucket", "assets", [file], TransferConflictPolicy.Skip);
        var done = await fx.Wait(jobs[0].Id, job => job.State == TransferState.Completed);

        Assert.True(done.Skipped);
        Assert.Equal("keep", fx.Remote.Text("bucket", "assets/photo.png"));
        Assert.Equal(0, fx.Remote.PutCalls);
    }

    [Fact]
    public async Task Rename_keeps_the_original_object()
    {
        await using var fx = await Fixture.Start();
        fx.Remote.Put("bucket", "assets/photo.png", "keep");
        var file = fx.Write("photo.png", "new");

        var jobs = await fx.Engine.EnqueueUploadsAsync(fx.Connection, "bucket", "assets", [file], TransferConflictPolicy.Rename);
        var done = await fx.Wait(jobs[0].Id, job => job.State == TransferState.Completed);

        Assert.Equal("assets/photo (1).png", done.Key);
        Assert.Equal("keep", fx.Remote.Text("bucket", "assets/photo.png"));
        Assert.Equal("new", fx.Remote.Text("bucket", done.Key));
    }

    [Fact]
    public async Task Lost_put_response_is_accepted_when_the_etag_matches()
    {
        await using var fx = await Fixture.Start();
        fx.Remote.StoreThenFailPut = true;
        var file = fx.Write("photo.png", "hello");

        var jobs = await fx.Engine.EnqueueUploadsAsync(fx.Connection, "bucket", "assets", [file], TransferConflictPolicy.Overwrite);
        var done = await fx.Wait(jobs[0].Id, job => job.State == TransferState.Completed);

        Assert.Equal(1, fx.Remote.PutCalls);
        Assert.Equal("hello", fx.Remote.Text("bucket", done.Key));
    }

    [Fact]
    public async Task Retry_does_not_overwrite_a_foreign_object()
    {
        await using var fx = await Fixture.Start();
        fx.Remote.FailPutsRemaining = 1;
        fx.Remote.PlantForeignOnFailure = true;
        var file = fx.Write("photo.png", "hello");

        var jobs = await fx.Engine.EnqueueUploadsAsync(fx.Connection, "bucket", "assets", [file], TransferConflictPolicy.Overwrite);
        var done = await fx.Wait(jobs[0].Id, job => job.State == TransferState.Failed);

        Assert.Equal(TransferFailureKind.Conflict, done.Failure!.Kind);
        Assert.Equal("\t\t\t", Encoding.UTF8.GetString(fx.Remote.Bytes("bucket", "assets/photo.png")));
        Assert.Equal(1, fx.Remote.PutCalls);
    }

    [Fact]
    public async Task Transient_failure_retries_and_access_denied_does_not()
    {
        await using var retry = await Fixture.Start();
        retry.Remote.FailPutsRemaining = 1;
        var file = retry.Write("photo.png", "hello");
        var retried = await retry.Engine.EnqueueUploadsAsync(retry.Connection, "bucket", "", [file], TransferConflictPolicy.Overwrite);
        await retry.Wait(retried[0].Id, job => job.State == TransferState.Completed);
        Assert.True(retry.Clock.Delays.Count >= 1);
        Assert.Equal(2, retry.Remote.PutCalls);

        await using var denied = await Fixture.Start();
        denied.Remote.DenyPuts = true;
        var other = denied.Write("photo.png", "hello");
        var failed = await denied.Engine.EnqueueUploadsAsync(denied.Connection, "bucket", "", [other], TransferConflictPolicy.Overwrite);
        var job = await denied.Wait(failed[0].Id, item => item.State == TransferState.Failed);
        Assert.Equal(TransferFailureKind.AccessDenied, job.Failure!.Kind);
        Assert.Empty(denied.Clock.Delays);
    }

    [Fact]
    public async Task Multipart_upload_sends_bounded_parts_and_can_resume()
    {
        await using var fx = await Fixture.Start(multipartThreshold: 4, partSize: 8, minimumPart: 1);
        var file = fx.Write("blob.bin", new string('a', 20));
        fx.Remote.FailPartNumber = 2;

        var jobs = await fx.Engine.EnqueueUploadsAsync(fx.Connection, "bucket", "", [file], TransferConflictPolicy.Overwrite);
        var done = await fx.Wait(jobs[0].Id, job => job.State == TransferState.Completed);

        Assert.Equal(Encoding.UTF8.GetBytes(new string('a', 20)), fx.Remote.Bytes("bucket", "blob.bin"));
        Assert.Contains(8L, fx.Remote.PartLengths);
        Assert.Contains(4L, fx.Remote.PartLengths);
        Assert.All(fx.Remote.PartLengths, length => Assert.True(length is 8 or 4));
        Assert.DoesNotContain(fx.Remote.PartStreams, stream => stream is MemoryStream);
        Assert.True(fx.Remote.PartCalls >= 3);
        Assert.Equal(20, done.BytesTransferred);
    }

    [Fact]
    public async Task Single_upload_reports_live_progress_without_persisting_each_update()
    {
        var connection = SampleConnection();
        var remote = new FakeRemote { BlockPutAfterProgress = true };
        var journal = new MemoryTransferJournal();
        var engine = new TransferEngine(remote, journal, new MapConnections(connection),
            Options(8 * 1024 * 1024, 8 * 1024 * 1024, TransferPlanner.S3MinimumPartSize),
            new ImmediateClock());
        var root = Path.Combine(Path.GetTempPath(), "cavdeck-upload-progress-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await engine.StartAsync();
            var content = new string('x', 300);
            var file = Path.Combine(root, "progress.txt");
            await File.WriteAllTextAsync(file, content);
            var jobs = await engine.EnqueueUploadsAsync(connection, "bucket", "", [file],
                TransferConflictPolicy.Overwrite);
            var id = Assert.Single(jobs).Id;

            await remote.PutProgressEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.InRange(engine.Current(id).BytesTransferred, 1, content.Length - 1);
            var persisted = Assert.Single(await journal.LoadUnfinishedAsync(CancellationToken.None));
            Assert.Equal(0, persisted.BytesTransferred);

            remote.PutProgressRelease.TrySetResult();
            var completed = await Wait(engine, id, job => job.State == TransferState.Completed);
            Assert.Equal(content.Length, completed.BytesTransferred);
        }
        finally
        {
            await engine.DisposeAsync();
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Multipart_upload_aggregates_live_part_progress_without_persisting_partial_bytes()
    {
        var connection = SampleConnection();
        var remote = new FakeRemote { BlockPartAfterProgress = true };
        var journal = new MemoryTransferJournal();
        var engine = new TransferEngine(remote, journal, new MapConnections(connection),
            Options(4, 8, 1), new ImmediateClock());
        var root = Path.Combine(Path.GetTempPath(), "cavdeck-multipart-progress-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await engine.StartAsync();
            var content = new string('y', 20);
            var file = Path.Combine(root, "multipart-progress.bin");
            await File.WriteAllTextAsync(file, content);
            var jobs = await engine.EnqueueUploadsAsync(connection, "bucket", "", [file],
                TransferConflictPolicy.Overwrite);
            var id = Assert.Single(jobs).Id;

            await remote.PartProgressEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.InRange(engine.Current(id).BytesTransferred, 1, content.Length - 1);
            var persisted = Assert.Single(await journal.LoadUnfinishedAsync(CancellationToken.None));
            Assert.Equal(0, persisted.BytesTransferred);

            remote.PartProgressRelease.TrySetResult();
            var completed = await Wait(engine, id, job => job.State == TransferState.Completed);
            Assert.Equal(content.Length, completed.BytesTransferred);
        }
        finally
        {
            await engine.DisposeAsync();
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Pausing_single_upload_persists_only_durable_progress()
    {
        var connection = SampleConnection();
        var remote = new FakeRemote { BlockPutAfterProgress = true };
        var journal = new MemoryTransferJournal();
        var engine = new TransferEngine(remote, journal, new MapConnections(connection),
            Options(8 * 1024 * 1024, 8 * 1024 * 1024, TransferPlanner.S3MinimumPartSize),
            new ImmediateClock());
        var root = Path.Combine(Path.GetTempPath(), "cavdeck-paused-upload-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await engine.StartAsync();
            var file = Path.Combine(root, "paused.txt");
            await File.WriteAllTextAsync(file, new string('p', 300));
            var jobs = await engine.EnqueueUploadsAsync(connection, "bucket", "", [file],
                TransferConflictPolicy.Overwrite);
            var id = Assert.Single(jobs).Id;

            await remote.PutProgressEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(engine.Current(id).BytesTransferred > 0);

            await engine.PauseAsync(id);

            Assert.Equal(TransferState.Paused, engine.Current(id).State);
            Assert.Equal(0, engine.Current(id).BytesTransferred);
            var persisted = Assert.Single(await journal.LoadUnfinishedAsync(CancellationToken.None));
            Assert.Equal(TransferState.Paused, persisted.State);
            Assert.Equal(0, persisted.BytesTransferred);
        }
        finally
        {
            remote.PutProgressRelease.TrySetResult();
            await engine.DisposeAsync();
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Pausing_multipart_upload_does_not_persist_in_flight_part_bytes()
    {
        var connection = SampleConnection();
        var remote = new FakeRemote { BlockPartAfterProgress = true };
        var journal = new MemoryTransferJournal();
        var engine = new TransferEngine(remote, journal, new MapConnections(connection),
            Options(4, 8, 1), new ImmediateClock());
        var root = Path.Combine(Path.GetTempPath(), "cavdeck-paused-multipart-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await engine.StartAsync();
            var file = Path.Combine(root, "paused.bin");
            await File.WriteAllTextAsync(file, new string('m', 20));
            var jobs = await engine.EnqueueUploadsAsync(connection, "bucket", "", [file],
                TransferConflictPolicy.Overwrite);
            var id = Assert.Single(jobs).Id;

            await remote.PartProgressEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(engine.Current(id).BytesTransferred > 0);

            await engine.PauseAsync(id);

            var paused = engine.Current(id);
            Assert.Equal(TransferState.Paused, paused.State);
            Assert.Equal(0, paused.BytesTransferred);
            Assert.NotNull(paused.UploadId);
            var persisted = Assert.Single(await journal.LoadUnfinishedAsync(CancellationToken.None));
            Assert.Equal(TransferState.Paused, persisted.State);
            Assert.Equal(0, persisted.BytesTransferred);
        }
        finally
        {
            remote.PartProgressRelease.TrySetResult();
            await engine.DisposeAsync();
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Failed_multipart_completion_retains_upload_for_manual_retry()
    {
        await using var fx = await Fixture.Start(multipartThreshold: 4, partSize: 8, minimumPart: 1);
        fx.Remote.FailCompleteOnce = true;
        var file = fx.Write("blob.bin", new string('a', 20));

        var jobs = await fx.Engine.EnqueueUploadsAsync(fx.Connection, "bucket", "", [file], TransferConflictPolicy.Overwrite);
        var failed = await fx.Wait(jobs[0].Id, job => job.State == TransferState.Failed);
        Assert.NotNull(failed.UploadId);
        Assert.Equal(20, failed.BytesTransferred);
        Assert.Equal(3, fx.Remote.PartCalls);

        await fx.Engine.RetryAsync(failed.Id);
        var completed = await fx.Wait(failed.Id, job => job.State == TransferState.Completed);
        Assert.Equal(failed.UploadId, completed.UploadId);
        Assert.Equal(3, fx.Remote.PartCalls);
        Assert.Equal(20, fx.Remote.Bytes("bucket", "blob.bin").Length);
    }

    [Fact]
    public async Task Restart_continues_a_multipart_upload_from_the_journal()
    {
        var root = Path.Combine(Path.GetTempPath(), "cavdeck-transfer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var file = Path.Combine(root, "blob.bin");
        await File.WriteAllTextAsync(file, new string('b', 20));
        var connection = SampleConnection();
        var remote = new FakeRemote();
        var journal = new MemoryTransferJournal();
        var job = TransferJob.Create(
            TransferKind.Upload, connection.Id, "bucket", "blob.bin", file, 20,
            TransferConflictPolicy.Overwrite) with
        {
            State = TransferState.Running,
            UploadId = "upload-1",
            Resume = ResumeMode.Multipart,
            SourceModifiedAt = new DateTimeOffset(File.GetLastWriteTimeUtc(file))
        };
        await journal.UpsertAsync(job, CancellationToken.None);
        remote.SeedPart("bucket", "blob.bin", "upload-1", 1, 0, Encoding.UTF8.GetBytes(new string('b', 8)));
        await journal.UpsertPartAsync(job.Id, new TransferPart(1, 0, 8, "\"p1\""), CancellationToken.None);

        var engine = new TransferEngine(remote, journal, new MapConnections(connection), Options(4, 8, 1), new ImmediateClock());
        await engine.StartAsync();
        var done = await Wait(engine, job.Id, item => item.State is TransferState.Completed or TransferState.Failed);

        Assert.Equal(TransferState.Completed, done.State);
        Assert.Equal(20, remote.Bytes("bucket", "blob.bin").Length);
        Assert.Equal(2, remote.PartCalls);
        await engine.DisposeAsync();
        Directory.Delete(root, true);
    }

    [Fact]
    public async Task Resume_reconciles_remote_parts_into_the_durable_checkpoint_before_pause()
    {
        var root = Path.Combine(Path.GetTempPath(), "cavdeck-resume-checkpoint-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var file = Path.Combine(root, "resume.bin");
        await File.WriteAllTextAsync(file, new string('r', 20));
        var connection = SampleConnection();
        var remote = new FakeRemote { BlockPartAfterProgress = true };
        var journal = new MemoryTransferJournal();
        var modifiedAt = new DateTimeOffset(File.GetLastWriteTimeUtc(file));
        var job = TransferJob.Create(TransferKind.Upload, connection.Id, "bucket", "resume.bin",
            file, 20, TransferConflictPolicy.Overwrite) with
        {
            State = TransferState.Running,
            UploadId = "upload-existing",
            Resume = ResumeMode.Multipart,
            SourceModifiedAt = modifiedAt
        };
        await journal.UpsertAsync(job, CancellationToken.None);
        remote.SeedPart("bucket", "resume.bin", "upload-existing", 1, 0,
            Encoding.UTF8.GetBytes(new string('r', 8)));

        var engine = new TransferEngine(remote, journal, new MapConnections(connection), Options(4, 8, 1),
            new ImmediateClock());
        try
        {
            await engine.StartAsync();
            await remote.PartProgressEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(engine.Current(job.Id).BytesTransferred > 8);

            await engine.PauseAsync(job.Id);

            var paused = engine.Current(job.Id);
            Assert.Equal(TransferState.Paused, paused.State);
            Assert.Equal(8, paused.BytesTransferred);
            var persisted = Assert.Single(await journal.LoadUnfinishedAsync(CancellationToken.None));
            Assert.Equal(8, persisted.BytesTransferred);
            var parts = await journal.GetPartsAsync(job.Id, CancellationToken.None);
            Assert.Equal(8, Assert.Single(parts).Length);
        }
        finally
        {
            remote.PartProgressRelease.TrySetResult();
            await engine.DisposeAsync();
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Changed_local_file_restarts_instead_of_reusing_old_multipart_parts()
    {
        var root = Path.Combine(Path.GetTempPath(), "cavdeck-transfer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var file = Path.Combine(root, "blob.bin");
        await File.WriteAllTextAsync(file, new string('a', 20));
        var originalModifiedAt = new DateTimeOffset(File.GetLastWriteTimeUtc(file));
        var connection = SampleConnection();
        var remote = new FakeRemote();
        var journal = new MemoryTransferJournal();
        var job = TransferJob.Create(TransferKind.Upload, connection.Id, "bucket", "blob.bin", file, 20,
            TransferConflictPolicy.Overwrite) with
        {
            State = TransferState.Running,
            UploadId = "upload-1",
            Resume = ResumeMode.Multipart,
            SourceModifiedAt = originalModifiedAt
        };
        await journal.UpsertAsync(job, CancellationToken.None);
        remote.SeedPart("bucket", "blob.bin", "upload-1", 1, 0, Encoding.UTF8.GetBytes(new string('a', 8)));
        await File.WriteAllTextAsync(file, new string('b', 20));
        File.SetLastWriteTimeUtc(file, originalModifiedAt.UtcDateTime.AddSeconds(2));

        await using var engine = new TransferEngine(remote, journal, new MapConnections(connection), Options(4, 8, 1), new ImmediateClock());
        await engine.StartAsync();
        var done = await Wait(engine, job.Id, item => item.State == TransferState.Completed);

        Assert.Equal(new string('b', 20), remote.Text("bucket", done.Key));
        Assert.Equal(1, remote.AbortCalls);
        Assert.Equal(3, remote.PartCalls);
        Directory.Delete(root, true);
    }

    [Fact]
    public async Task Restart_restores_queued_jobs_older_than_the_recent_history_limit()
    {
        var root = Path.Combine(Path.GetTempPath(), "cavdeck-transfer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var file = Path.Combine(root, "waiting.txt");
        await File.WriteAllTextAsync(file, "queued");
        var connection = SampleConnection();
        var journal = new MemoryTransferJournal();
        var waiting = TransferJob.Create(TransferKind.Upload, connection.Id, "bucket", "waiting.txt",
            file, 6, TransferConflictPolicy.Overwrite, createdAt: DateTimeOffset.UtcNow.AddDays(-2));
        await journal.UpsertAsync(waiting, CancellationToken.None);
        for (var index = 0; index < 510; index++)
        {
            var finished = TransferJob.Create(TransferKind.Upload, connection.Id, "bucket", $"done-{index}",
                file, 0, TransferConflictPolicy.Overwrite) with { State = TransferState.Completed };
            await journal.UpsertAsync(finished, CancellationToken.None);
        }

        var remote = new FakeRemote();
        await using var engine = new TransferEngine(remote, journal, new MapConnections(connection));
        await engine.StartAsync();
        var completed = await Wait(engine, waiting.Id, job => job.State == TransferState.Completed);
        Assert.Equal("queued", remote.Text("bucket", completed.Key));
        Directory.Delete(root, true);
    }

    [Fact]
    public async Task Late_progress_cannot_replace_waiting_retry_state()
    {
        await using var fx = await Fixture.Start();
        fx.Remote.BlockPuts = true;
        var file = fx.Write("waiting.txt", "content");
        var jobs = await fx.Engine.EnqueueUploadsAsync(fx.Connection, "bucket", "", [file], TransferConflictPolicy.Overwrite);
        await fx.Remote.PutEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var running = fx.Engine.Current(jobs[0].Id);

        await fx.Engine.PublishAsync(running with { State = TransferState.WaitingRetry }, CancellationToken.None);
        await fx.Engine.PublishAsync(running with { BytesTransferred = 3 }, CancellationToken.None);

        Assert.Equal(TransferState.WaitingRetry, fx.Engine.Current(jobs[0].Id).State);
    }

    [Fact]
    public async Task Large_download_uses_parallel_fixed_ranges_and_commits_exact_bytes()
    {
        await using var fx = await Fixture.Start(downloadThreshold: 8, downloadPart: 8);
        fx.Remote.Put("bucket", "large.bin", "abcdefghijklmnopqrstuvwx");
        var directory = fx.MakeDirectory("in");

        var jobs = await fx.Engine.EnqueueDownloadsAsync(fx.Connection, "bucket",
            [new DownloadItem("large.bin", false, 24)], directory, TransferConflictPolicy.Overwrite);
        var done = await fx.Wait(jobs[0].Id, job => job.State == TransferState.Completed);

        Assert.Equal(ResumeMode.ParallelRange, done.Resume);
        Assert.Equal(new long[] { 0, 8, 16 }, fx.Remote.RangeReadOffsets.Order());
        Assert.Equal(0, fx.Remote.DownloadOpens);
        Assert.Equal("abcdefghijklmnopqrstuvwx", await File.ReadAllTextAsync(done.LocalPath));
        Assert.False(File.Exists(TransferPaths.Partial(done.LocalPath, done.Id)));
        Assert.Empty(await fx.Journal.GetPartsAsync(done.Id, CancellationToken.None));
    }

    [Fact]
    public async Task Parallel_download_falls_back_to_sequential_when_range_is_unsupported()
    {
        await using var fx = await Fixture.Start(downloadThreshold: 8, downloadPart: 8);
        fx.Remote.Put("bucket", "large.bin", "abcdefghijklmnopqrstuvwx");
        fx.Remote.RejectFixedRanges = true;
        var directory = fx.MakeDirectory("in");

        var jobs = await fx.Engine.EnqueueDownloadsAsync(fx.Connection, "bucket",
            [new DownloadItem("large.bin", false, 24)], directory, TransferConflictPolicy.Overwrite);
        var done = await fx.Wait(jobs[0].Id, job => job.State == TransferState.Completed);

        Assert.Equal(ResumeMode.SequentialOnly, done.Resume);
        Assert.NotEmpty(fx.Remote.RangeReadOffsets);
        Assert.Equal(1, fx.Remote.DownloadOpens);
        Assert.Equal("abcdefghijklmnopqrstuvwx", await File.ReadAllTextAsync(done.LocalPath));
        Assert.Empty(await fx.Journal.GetPartsAsync(done.Id, CancellationToken.None));
    }

    [Fact]
    public async Task Parallel_download_falls_back_when_provider_has_no_range_implementation()
    {
        await using var fx = await Fixture.Start(downloadThreshold: 8, downloadPart: 8);
        fx.Remote.Put("bucket", "large.bin", "abcdefghijklmnopqrstuvwx");
        fx.Remote.ThrowUnsupportedRange = true;
        var directory = fx.MakeDirectory("in");

        var jobs = await fx.Engine.EnqueueDownloadsAsync(fx.Connection, "bucket",
            [new DownloadItem("large.bin", false, 24)], directory, TransferConflictPolicy.Overwrite);
        var done = await fx.Wait(jobs[0].Id, job => job.State == TransferState.Completed);

        Assert.Equal(ResumeMode.SequentialOnly, done.Resume);
        Assert.Equal(1, fx.Remote.DownloadOpens);
        Assert.Equal("abcdefghijklmnopqrstuvwx", await File.ReadAllTextAsync(done.LocalPath));
    }

    [Fact]
    public async Task Parallel_download_does_not_commit_parts_from_changed_remote_object()
    {
        await using var fx = await Fixture.Start(downloadThreshold: 8, downloadPart: 8);
        fx.Remote.Put("source", "large.bin", "abcdefghijklmnopqrstuvwx");
        fx.Remote.MutateSourceOnSecondRange = true;
        var directory = fx.MakeDirectory("in");

        var jobs = await fx.Engine.EnqueueDownloadsAsync(fx.Connection, "source",
            [new DownloadItem("large.bin", false, 24)], directory, TransferConflictPolicy.Overwrite);
        var done = await fx.Wait(jobs[0].Id, job => job.State == TransferState.Failed);

        Assert.Equal(TransferFailureKind.Integrity, done.Failure?.Kind);
        Assert.False(File.Exists(done.LocalPath));
    }

    [Fact]
    public async Task Parallel_download_pause_keeps_only_durable_parts_and_resume_skips_them()
    {
        await using var fx = await Fixture.Start(downloadThreshold: 8, downloadPart: 8);
        fx.Remote.Put("bucket", "large.bin", "abcdefghijklmnopqrstuvwx");
        fx.Remote.BlockRangeAtOffset = 8;
        var directory = fx.MakeDirectory("in");

        var jobs = await fx.Engine.EnqueueDownloadsAsync(fx.Connection, "bucket",
            [new DownloadItem("large.bin", false, 24)], directory, TransferConflictPolicy.Overwrite);
        var id = jobs[0].Id;
        await fx.Remote.RangeReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline
               && !(await fx.Journal.GetPartsAsync(id, CancellationToken.None)).Any(part => part.Offset == 0))
            await Task.Delay(15);
        Assert.Contains(await fx.Journal.GetPartsAsync(id, CancellationToken.None), part => part.Offset == 0);

        await fx.Engine.PauseAsync(id);
        var paused = await fx.Wait(id, job => job.State == TransferState.Paused);
        Assert.Equal(ResumeMode.ParallelRange, paused.Resume);
        Assert.True(paused.BytesTransferred >= 8);
        Assert.True(paused.BytesTransferred < 24);
        Assert.True(File.Exists(TransferPaths.Partial(paused.LocalPath, id)));

        fx.Remote.BlockRangeAtOffset = null;
        fx.Remote.RangeReadRelease.TrySetResult();
        await fx.Engine.ResumeAsync(id);
        var done = await fx.Wait(id, job => job.State == TransferState.Completed);

        Assert.Equal("abcdefghijklmnopqrstuvwx", await File.ReadAllTextAsync(done.LocalPath));
        Assert.Equal(1, fx.Remote.RangeReadOffsets.Count(offset => offset == 0));
        Assert.True(fx.Remote.RangeReadOffsets.Count(offset => offset == 8) >= 2);
    }

    [Fact]
    public async Task Parallel_download_rechecks_checksum_and_redownloads_corrupted_checkpoint()
    {
        await using var fx = await Fixture.Start(downloadThreshold: 8, downloadPart: 8);
        fx.Remote.Put("bucket", "large.bin", "abcdefghijklmnopqrstuvwx");
        fx.Remote.BlockRangeAtOffset = 8;
        var directory = fx.MakeDirectory("in");
        var jobs = await fx.Engine.EnqueueDownloadsAsync(fx.Connection, "bucket",
            [new DownloadItem("large.bin", false, 24)], directory, TransferConflictPolicy.Overwrite);
        var id = jobs[0].Id;
        await fx.Remote.RangeReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline
               && !(await fx.Journal.GetPartsAsync(id, CancellationToken.None)).Any(part => part.Offset == 0))
            await Task.Delay(15);
        await fx.Engine.PauseAsync(id);
        var paused = await fx.Wait(id, job => job.State == TransferState.Paused);
        fx.Remote.BlockRangeAtOffset = null;
        fx.Remote.RangeReadRelease.TrySetResult();

        var partial = TransferPaths.Partial(paused.LocalPath, id);
        // Pause transitions before the old asynchronous reader has necessarily
        // released its Windows file handle. Wait for exclusive access before
        // simulating external corruption of the closed partial file.
        var writeDeadline = DateTime.UtcNow.AddSeconds(5);
        var corrupted = false;
        while (!corrupted)
        {
            try
            {
                await using var file = new FileStream(partial, FileMode.Open, FileAccess.Write, FileShare.None);
                file.Position = 0;
                await file.WriteAsync("BAD"u8.ToArray());
                corrupted = true;
            }
            catch (IOException) when (DateTime.UtcNow < writeDeadline)
            {
                await Task.Delay(15);
            }
        }

        await fx.Engine.ResumeAsync(id);
        var done = await fx.Wait(id, job => job.State == TransferState.Completed);
        Assert.Equal("abcdefghijklmnopqrstuvwx", await File.ReadAllTextAsync(done.LocalPath));
        Assert.Equal(2, fx.Remote.RangeReadOffsets.Count(offset => offset == 0));
    }

    [Fact]
    public async Task Cancelling_paused_parallel_download_removes_preallocated_temp_file()
    {
        await using var fx = await Fixture.Start(downloadThreshold: 8, downloadPart: 8);
        fx.Remote.Put("bucket", "large.bin", "abcdefghijklmnopqrstuvwx");
        fx.Remote.BlockRangeAtOffset = 8;
        var directory = fx.MakeDirectory("in");
        var jobs = await fx.Engine.EnqueueDownloadsAsync(fx.Connection, "bucket",
            [new DownloadItem("large.bin", false, 24)], directory, TransferConflictPolicy.Overwrite);
        var id = jobs[0].Id;
        await fx.Remote.RangeReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await fx.Engine.PauseAsync(id);
        var paused = await fx.Wait(id, job => job.State == TransferState.Paused);
        fx.Remote.RangeReadRelease.TrySetResult();
        await fx.Engine.CancelAsync(id);
        var partial = TransferPaths.Partial(paused.LocalPath, id);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (File.Exists(partial) && DateTime.UtcNow < deadline)
            await Task.Delay(15);

        Assert.False(File.Exists(partial));
        Assert.Empty(await fx.Journal.GetPartsAsync(id, CancellationToken.None));
    }

    [Fact]
    public async Task Parallel_download_retries_short_ranges_without_publishing_partial_target()
    {
        await using var fx = await Fixture.Start(downloadThreshold: 8, downloadPart: 8);
        fx.Remote.Put("bucket", "large.bin", "abcdefghijklmnopqrstuvwx");
        fx.Remote.ShortRangeAtOffset = 8;
        var directory = fx.MakeDirectory("in");
        var jobs = await fx.Engine.EnqueueDownloadsAsync(fx.Connection, "bucket",
            [new DownloadItem("large.bin", false, 24)], directory, TransferConflictPolicy.Overwrite);
        var done = await fx.Wait(jobs[0].Id, job => job.State == TransferState.Completed);

        Assert.True(fx.Remote.RangeReadOffsets.Count(offset => offset == 8) >= 2);
        Assert.Equal("abcdefghijklmnopqrstuvwx", await File.ReadAllTextAsync(done.LocalPath));
    }

    [Fact]
    public async Task Download_replaces_the_destination_only_after_verification()
    {
        await using var fx = await Fixture.Start();
        fx.Remote.Put("bucket", "assets/photo.png", "hello-world");
        fx.Remote.BlockDownloads = true;
        var directory = fx.MakeDirectory("in");

        var jobs = await fx.Engine.EnqueueDownloadsAsync(
            fx.Connection, "bucket", [new DownloadItem("assets/photo.png", false, null)], directory, TransferConflictPolicy.Overwrite);
        await fx.Remote.DownloadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var finalPath = Path.Combine(directory, "assets", "photo.png");
        Assert.False(File.Exists(finalPath));

        fx.Remote.DownloadRelease.TrySetResult();
        await fx.Wait(jobs[0].Id, job => job.State == TransferState.Completed);
        Assert.Equal("hello-world", await File.ReadAllTextAsync(finalPath));
        Assert.False(File.Exists(TransferPaths.Partial(finalPath, jobs[0].Id)));
    }

    [Fact]
    public async Task Download_resumes_from_the_partial_file()
    {
        await using var fx = await Fixture.Start();
        fx.Remote.Put("bucket", "photo.png", "hello-world");
        fx.Remote.ThrowDownloadAfter = 5;
        var directory = fx.MakeDirectory("in");

        var jobs = await fx.Engine.EnqueueDownloadsAsync(
            fx.Connection, "bucket", [new DownloadItem("photo.png", false, null)], directory, TransferConflictPolicy.Overwrite);
        var done = await fx.Wait(jobs[0].Id, job => job.State == TransferState.Completed);

        Assert.True(fx.Remote.RangeWasRequested);
        Assert.Equal("hello-world", await File.ReadAllTextAsync(done.LocalPath));
    }

    [Fact]
    public async Task Concurrent_downloads_to_one_path_use_separate_partial_files()
    {
        await using var fx = await Fixture.Start();
        fx.Remote.Put("bucket", "photo.png", "same-content");
        fx.Remote.BlockDownloads = true;
        var directory = fx.MakeDirectory("in");
        var first = await fx.Engine.EnqueueDownloadsAsync(fx.Connection, "bucket",
            [new DownloadItem("photo.png", false, null)], directory, TransferConflictPolicy.Overwrite);
        var second = await fx.Engine.EnqueueDownloadsAsync(fx.Connection, "bucket",
            [new DownloadItem("photo.png", false, null)], directory, TransferConflictPolicy.Overwrite);
        await fx.Remote.DownloadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        fx.Remote.DownloadRelease.TrySetResult();
        var firstResult = await fx.Wait(first[0].Id,
            job => job.State is TransferState.Completed or TransferState.Failed);
        var secondResult = await fx.Wait(second[0].Id,
            job => job.State is TransferState.Completed or TransferState.Failed);
        Assert.Single(new[] { firstResult, secondResult }, job => job.State == TransferState.Completed);
        Assert.Single(new[] { firstResult, secondResult }, job =>
            job.State == TransferState.Failed && job.Failure?.Kind == TransferFailureKind.Conflict);

        var path = Path.Combine(directory, "photo.png");
        Assert.Equal("same-content", await File.ReadAllTextAsync(path));
        var winner = firstResult.State == TransferState.Completed ? firstResult : secondResult;
        Assert.False(File.Exists(TransferPaths.Partial(path, winner.Id)));
    }


    [Fact]
    public async Task Download_refuses_to_replace_target_created_after_start()
    {
        await using var fx = await Fixture.Start();
        fx.Remote.Put("bucket", "photo.png", "downloaded");
        fx.Remote.BlockDownloads = true;
        var directory = fx.MakeDirectory("in");
        var path = Path.Combine(directory, "photo.png");
        var jobs = await fx.Engine.EnqueueDownloadsAsync(fx.Connection, "bucket",
            [new DownloadItem("photo.png", false, null)], directory, TransferConflictPolicy.Overwrite);
        await fx.Remote.DownloadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await File.WriteAllTextAsync(path, "other application's file");
        fx.Remote.DownloadRelease.TrySetResult();

        var result = await fx.Wait(jobs[0].Id, job => job.State == TransferState.Failed);
        Assert.Equal(TransferFailureKind.Conflict, result.Failure!.Kind);
        Assert.Equal("other application's file", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task Download_refuses_to_replace_existing_target_modified_after_start()
    {
        await using var fx = await Fixture.Start();
        fx.Remote.Put("bucket", "photo.png", "downloaded");
        fx.Remote.BlockDownloads = true;
        var directory = fx.MakeDirectory("in");
        var path = Path.Combine(directory, "photo.png");
        await File.WriteAllTextAsync(path, "original");
        var jobs = await fx.Engine.EnqueueDownloadsAsync(fx.Connection, "bucket",
            [new DownloadItem("photo.png", false, null)], directory, TransferConflictPolicy.Overwrite);
        await fx.Remote.DownloadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await File.WriteAllTextAsync(path, "modified with additional bytes");
        fx.Remote.DownloadRelease.TrySetResult();

        var result = await fx.Wait(jobs[0].Id, job => job.State == TransferState.Failed);
        Assert.Equal(TransferFailureKind.Conflict, result.Failure!.Kind);
        Assert.Equal("modified with additional bytes", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task Download_does_not_commit_when_the_object_changes()
    {
        await using var fx = await Fixture.Start();
        fx.Remote.Put("bucket", "photo.png", "hello-world");
        fx.Remote.MutateDuringDownload = true;
        var directory = fx.MakeDirectory("in");

        var jobs = await fx.Engine.EnqueueDownloadsAsync(
            fx.Connection, "bucket", [new DownloadItem("photo.png", false, null)], directory, TransferConflictPolicy.Overwrite);
        var done = await fx.Wait(jobs[0].Id, job => job.State == TransferState.Failed);

        Assert.Equal(TransferFailureKind.Conflict, done.Failure!.Kind);
        Assert.False(File.Exists(done.LocalPath));
    }

    [Fact]
    public async Task Download_skip_leaves_the_local_file_untouched()
    {
        await using var fx = await Fixture.Start();
        fx.Remote.Put("bucket", "photo.png", "remote");
        var directory = fx.MakeDirectory("in");
        var path = Path.Combine(directory, "photo.png");
        await File.WriteAllTextAsync(path, "local");

        var jobs = await fx.Engine.EnqueueDownloadsAsync(
            fx.Connection, "bucket", [new DownloadItem("photo.png", false, null)], directory, TransferConflictPolicy.Skip);
        var done = await fx.Wait(jobs[0].Id, job => job.State == TransferState.Completed);

        Assert.True(done.Skipped);
        Assert.Equal("local", await File.ReadAllTextAsync(path));
        Assert.Equal(0, fx.Remote.DownloadOpens);
    }

    [Fact]
    public async Task Pause_and_cancel_do_not_report_success()
    {
        await using var fx = await Fixture.Start(multipartThreshold: 4, partSize: 8, minimumPart: 1);
        fx.Remote.BlockParts = true;
        var small = fx.Write("photo.png", "hello");
        var paused = await fx.Engine.EnqueueUploadsAsync(fx.Connection, "bucket", "", [small], TransferConflictPolicy.Overwrite);
        await fx.Remote.PartEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await fx.Engine.PauseAsync(paused[0].Id);
        var pausedJob = await fx.Wait(paused[0].Id, job => job.State == TransferState.Paused);
        Assert.Null(pausedJob.Failure);
        Assert.Equal(0, fx.Remote.AbortCalls);
        Assert.False(fx.Remote.Contains("bucket", "photo.png"));

        fx.Remote.RearmParts();
        var blob = fx.Write("blob.bin", new string('c', 20));
        var cancelled = await fx.Engine.EnqueueUploadsAsync(fx.Connection, "bucket", "", [blob], TransferConflictPolicy.Overwrite);
        await fx.Remote.PartEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await fx.Engine.CancelAsync(cancelled[0].Id);
        var cancelledJob = await fx.Wait(cancelled[0].Id, job => job.State == TransferState.Cancelled);
        var abortDeadline = DateTime.UtcNow.AddSeconds(5);
        while (fx.Remote.AbortCalls < 1 && DateTime.UtcNow < abortDeadline)
            await Task.Delay(15);
        Assert.True(fx.Remote.AbortCalls >= 1);
        Assert.Equal(TransferState.Cancelled, cancelledJob.State);
    }

    [Fact]
    public async Task Cancel_during_multipart_creation_aborts_upload_before_its_id_is_journaled()
    {
        await using var fx = await Fixture.Start(multipartThreshold: 4, partSize: 8, minimumPart: 1);
        fx.Remote.BlockInitiate = true;
        var file = fx.Write("large.bin", new string('x', 20));
        var jobs = await fx.Engine.EnqueueUploadsAsync(fx.Connection, "bucket", "", [file], TransferConflictPolicy.Overwrite);

        await fx.Remote.InitiateEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await fx.Engine.CancelAsync(jobs[0].Id);
        fx.Remote.InitiateRelease.TrySetResult();

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (fx.Remote.AbortCalls == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(15);
        Assert.Equal(1, fx.Remote.AbortCalls);
        Assert.Equal(0, fx.Remote.PendingUploads);
        var cancelled = fx.Engine.Current(jobs[0].Id);
        Assert.Equal(TransferState.Cancelled, cancelled.State);
        Assert.Null(cancelled.Failure);
    }

    [Fact]
    public async Task Pause_during_multipart_creation_cleans_up_an_unjournaled_upload()
    {
        await using var fx = await Fixture.Start(multipartThreshold: 4, partSize: 8, minimumPart: 1);
        fx.Remote.BlockInitiate = true;
        var file = fx.Write("large.bin", new string('x', 20));
        var jobs = await fx.Engine.EnqueueUploadsAsync(fx.Connection, "bucket", "", [file], TransferConflictPolicy.Overwrite);

        await fx.Remote.InitiateEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await fx.Engine.PauseAsync(jobs[0].Id);
        fx.Remote.InitiateRelease.TrySetResult();

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (fx.Remote.AbortCalls == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(15);
        Assert.Equal(1, fx.Remote.AbortCalls);
        Assert.Equal(0, fx.Remote.PendingUploads);
        var paused = fx.Engine.Current(jobs[0].Id);
        Assert.Equal(TransferState.Paused, paused.State);
        Assert.Null(paused.UploadId);
        Assert.Null(paused.Failure);
    }

    [Fact]
    public async Task Cancel_queued_upload_does_not_start_it_when_a_slot_becomes_free()
    {
        await using var fx = await Fixture.Start();
        fx.Engine.SetMaxConcurrent(1);
        fx.Remote.BlockPuts = true;
        var first = fx.Write("first.bin", "first");
        var cancelledFile = fx.Write("cancelled.bin", "cancelled");
        var third = fx.Write("third.bin", "third");
        var jobs = await fx.Engine.EnqueueUploadsAsync(
            fx.Connection, "bucket", "", [first, cancelledFile], TransferConflictPolicy.Overwrite);

        await fx.Remote.PutEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await fx.Engine.CancelAsync(jobs[1].Id);
        fx.Remote.PutRelease.TrySetResult();
        var later = await fx.Engine.EnqueueUploadsAsync(
            fx.Connection, "bucket", "", [third], TransferConflictPolicy.Overwrite);
        await fx.Wait(later[0].Id, job => job.State == TransferState.Completed);

        Assert.Equal(2, fx.Remote.PutCalls);
        Assert.False(fx.Remote.Contains("bucket", "cancelled.bin"));
        var cancelled = fx.Engine.Current(jobs[1].Id);
        Assert.Equal(TransferState.Cancelled, cancelled.State);
        Assert.Null(cancelled.Failure);
    }

    [Fact]
    public async Task Sdk_part_cancellation_with_transport_error_stays_cancelled_and_aborts_multipart()
    {
        await using var fx = await Fixture.Start(multipartThreshold: 4, partSize: 8, minimumPart: 1);
        fx.Remote.BlockParts = true;
        fx.Remote.WrapPartCancellation = true;
        var file = fx.Write("large.bin", new string('x', 20));
        var jobs = await fx.Engine.EnqueueUploadsAsync(fx.Connection, "bucket", "", [file], TransferConflictPolicy.Overwrite);

        await fx.Remote.PartEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await fx.Engine.CancelAsync(jobs[0].Id);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (fx.Remote.AbortCalls == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(15);

        Assert.Equal(1, fx.Remote.AbortCalls);
        Assert.Equal(0, fx.Remote.PendingUploads);
        Assert.False(fx.Remote.Contains("bucket", "large.bin"));
        var cancelled = fx.Engine.Current(jobs[0].Id);
        Assert.Equal(TransferState.Cancelled, cancelled.State);
        Assert.Null(cancelled.Failure);
        Assert.Empty(fx.Clock.Delays);
    }

    [Fact]
    public async Task Unexpected_operation_cancellation_is_retried_instead_of_leaving_the_job_running()
    {
        await using var fx = await Fixture.Start();
        fx.Remote.ThrowUnexpectedCancellationOnce = true;
        var file = fx.Write("retry.txt", "payload");

        var jobs = await fx.Engine.EnqueueUploadsAsync(
            fx.Connection, "bucket", "", [file], TransferConflictPolicy.Overwrite);
        var result = await fx.Wait(jobs[0].Id,
            job => job.State is TransferState.Completed or TransferState.Failed);

        Assert.Equal(TransferState.Completed, result.State);
        Assert.Equal("payload", fx.Remote.Text("bucket", "retry.txt"));
        Assert.Single(fx.Clock.Delays);
    }

    [Fact]
    public async Task Worker_journal_failure_is_reported_and_does_not_leave_a_running_job()
    {
        var connection = SampleConnection();
        var remote = new FakeRemote();
        var journal = new FaultingTransferJournal();
        var engine = new TransferEngine(remote, journal, new MapConnections(connection),
            new TransferEngineOptions { MaxAttempts = 1 });
        var reported = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.BackgroundFaulted += exception => reported.TrySetResult(exception);
        var root = Path.Combine(Path.GetTempPath(), "cavdeck-journal-failure-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        await using (engine)
        {
            try
            {
                await engine.StartAsync();
                var file = Path.Combine(root, "journal.txt");
                await File.WriteAllTextAsync(file, "payload");
                var jobs = await engine.EnqueueUploadsAsync(
                    connection, "bucket", "", [file], TransferConflictPolicy.Overwrite);

                var result = await Wait(engine, Assert.Single(jobs).Id,
                    job => job.State is TransferState.Failed or TransferState.Completed);
                var backgroundFault = await reported.Task.WaitAsync(TimeSpan.FromSeconds(5));

                Assert.Equal(TransferState.Failed, result.State);
                Assert.Equal("WorkerFault", result.Failure?.Code);
                Assert.IsType<IOException>(backgroundFault);
                Assert.False(remote.Contains("bucket", "journal.txt"));
            }
            finally
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Only_one_transfer_runs_when_concurrency_is_one()
    {
        await using var fx = await Fixture.Start();
        fx.Engine.SetMaxConcurrent(1);
        fx.Remote.BlockPuts = true;
        var first = fx.Write("a.png", "one");
        var second = fx.Write("b.png", "two");

        await fx.Engine.EnqueueUploadsAsync(fx.Connection, "bucket", "", [first, second], TransferConflictPolicy.Overwrite);
        await fx.Remote.PutEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(80);

        Assert.Equal(1, fx.Remote.MaxActivePuts);
        fx.Remote.PutRelease.TrySetResult();
        await fx.WaitAll(job => job.State == TransferState.Completed);
        Assert.Equal(1, fx.Remote.MaxActivePuts);
    }

    [Fact]
    public async Task Folder_upload_keeps_relative_names_and_empty_markers()
    {
        await using var fx = await Fixture.Start();
        var folder = fx.MakeDirectory("pack");
        Directory.CreateDirectory(Path.Combine(folder, "nested"));
        await File.WriteAllTextAsync(Path.Combine(folder, "nested", "a.txt"), "a");
        var empty = fx.MakeDirectory("empty");

        var jobs = await fx.Engine.EnqueueUploadsAsync(
            fx.Connection, "bucket", "root", [folder, empty], TransferConflictPolicy.Overwrite);
        await fx.WaitAll(job => job.State == TransferState.Completed);

        Assert.Equal("a", fx.Remote.Text("bucket", "root/pack/nested/a.txt"));
        Assert.Contains(jobs, job => job.Key == "root/empty/" && job.FolderMarker);
    }

    [Fact]
    public async Task Read_only_connection_rejects_uploads()
    {
        await using var fx = await Fixture.Start(readOnly: true);
        var file = fx.Write("photo.png", "hello");

        await Assert.ThrowsAsync<StorageException>(() =>
            fx.Engine.EnqueueUploadsAsync(fx.Connection, "bucket", "", [file], TransferConflictPolicy.Overwrite));
        Assert.Equal(0, fx.Remote.PutCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Queued_upload_resolves_current_policy_or_deletion_instead_of_cached_connection(bool deleted)
    {
        var old = SampleConnection();
        var catalog = new MutableConnectionCatalog(old);
        var remote = new FakeRemote();
        await using var engine = new TransferEngine(remote, new MemoryTransferJournal(),
            new CatalogTransferConnections(catalog));
        var root = Path.Combine(Path.GetTempPath(), "cavdeck-policy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var file = Path.Combine(root, "draft.txt");
            await File.WriteAllTextAsync(file, "draft");
            // Queue while writable, then change policy before starting the worker.
            var jobs = await engine.EnqueueUploadsAsync(old, "assets", "", [file], TransferConflictPolicy.Skip);
            catalog.Current = deleted ? null : old with { IsReadOnly = true };
            await engine.StartAsync();
            var result = await Wait(engine, Assert.Single(jobs).Id, job => job.State == TransferState.Failed);
            Assert.Equal(deleted ? TransferFailureKind.ConnectionMissing : TransferFailureKind.ReadOnly,
                result.Failure?.Kind);
            Assert.Equal(0, remote.PutCalls);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Read_only_change_cancels_writes_but_keeps_downloads_and_cross_copy_sources()
    {
        var source = SampleConnection();
        var destination = SampleConnection();
        var remote = new FakeRemote();
        remote.Put("source", "item.txt", "draft");
        await using var engine = new TransferEngine(remote, new MemoryTransferJournal(),
            new CrossConnections(source, destination));
        var root = Path.Combine(Path.GetTempPath(), "cavdeck-lifecycle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var file = Path.Combine(root, "draft.txt");
            await File.WriteAllTextAsync(file, "draft");
            var uploads = await engine.EnqueueUploadsAsync(source, "source", "", [file], TransferConflictPolicy.Skip);
            var downloads = await engine.EnqueueDownloadsAsync(source, "source",
                [new DownloadItem("item.txt", false, 5)], root, TransferConflictPolicy.Skip);
            var copies = await engine.EnqueueCrossCopiesAsync(source, destination, "source",
                [new DownloadItem("item.txt", false, 5)], "target", "", TransferConflictPolicy.Skip);
            await engine.CancelConnectionAsync(source.Id, writesOnly: true);
            Assert.Equal(TransferState.Cancelled, engine.TryCurrent(Assert.Single(uploads).Id)?.State);
            Assert.Equal(TransferState.Queued, engine.TryCurrent(Assert.Single(downloads).Id)?.State);
            Assert.Equal(TransferState.Queued, engine.TryCurrent(Assert.Single(copies).Id)?.State);
            await engine.CancelConnectionAsync(source.Id);
            Assert.All(engine.Jobs, job => Assert.Equal(TransferState.Cancelled, job.State));
        }
        finally { Directory.Delete(root, true); }
    }

    private static StorageConnection SampleConnection(bool readOnly = false) => new(
        Guid.NewGuid(), "lab", StorageProviderKind.Minio, "http://127.0.0.1:9000", "us-east-1", "cred", readOnly);

    private static TransferEngineOptions Options(long threshold, long part, long minimum,
        long downloadThreshold = 128L * 1024 * 1024, long downloadPart = 16L * 1024 * 1024) => new()
    {
        MaxConcurrentTransfers = 3,
        MaxConcurrentParts = 2,
        MultipartThresholdBytes = threshold,
        PartSizeBytes = part,
        MinimumPartBytes = minimum,
        MaxAttempts = 5,
        DownloadThresholdBytes = downloadThreshold,
        DownloadPartSizeBytes = downloadPart
    };

    private static async Task<TransferJob> Wait(TransferEngine engine, Guid id, Func<TransferJob, bool> predicate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            var job = engine.Jobs.Single(item => item.Id == id);
            if (predicate(job))
                return job;
            await Task.Delay(15);
        }

        var last = engine.Jobs.Single(item => item.Id == id);
        throw new TimeoutException($"{last.State} {last.Failure?.Kind} {last.Failure?.Detail}");
    }

    private sealed class FaultingTransferJournal : ITransferJournal
    {
        private readonly MemoryTransferJournal _inner = new();

        public Task<TransferHistoryRemoval> RemoveTerminalAsync(
            TransferHistoryScope scope, Guid? id, CancellationToken cancellationToken) =>
            _inner.RemoveTerminalAsync(scope, id, cancellationToken);

        public Task UpsertAsync(TransferJob job, CancellationToken cancellationToken)
        {
            if (job.State != TransferState.Queued)
                return Task.FromException(new IOException("The transfer journal is unavailable."));
            return _inner.UpsertAsync(job, cancellationToken);
        }

        public Task<IReadOnlyList<TransferJob>> LoadRecentAsync(int limit, CancellationToken cancellationToken) =>
            _inner.LoadRecentAsync(limit, cancellationToken);

        public Task<IReadOnlyList<TransferJob>> LoadUnfinishedAsync(CancellationToken cancellationToken) =>
            _inner.LoadUnfinishedAsync(cancellationToken);

        public Task UpsertPartAsync(Guid jobId, TransferPart part, CancellationToken cancellationToken) =>
            _inner.UpsertPartAsync(jobId, part, cancellationToken);

        public Task ClearPartsAsync(Guid jobId, CancellationToken cancellationToken) =>
            _inner.ClearPartsAsync(jobId, cancellationToken);

        public Task<IReadOnlyList<TransferPart>> GetPartsAsync(Guid jobId, CancellationToken cancellationToken) =>
            _inner.GetPartsAsync(jobId, cancellationToken);

        public Task UpsertBatchAsync(CrossTransferBatch batch, CancellationToken cancellationToken) =>
            _inner.UpsertBatchAsync(batch, cancellationToken);

        public Task<IReadOnlyList<CrossTransferBatch>> LoadUnfinishedBatchesAsync(CancellationToken cancellationToken) =>
            _inner.LoadUnfinishedBatchesAsync(cancellationToken);

        public Task<IReadOnlyList<CrossTransferBatch>> LoadRecentBatchesAsync(int limit, CancellationToken cancellationToken) =>
            _inner.LoadRecentBatchesAsync(limit, cancellationToken);

        public Task<bool> TryCreateCrossJobAsync(TransferJob job, CancellationToken cancellationToken) =>
            _inner.TryCreateCrossJobAsync(job, cancellationToken);

        public Task<CrossBatchProgress> GetBatchProgressAsync(Guid batchId, CancellationToken cancellationToken) =>
            _inner.GetBatchProgressAsync(batchId, cancellationToken);

        public Task<IReadOnlyList<TransferJob>> LoadFailedBatchJobsAsync(
            Guid batchId, string? afterId, int limit, CancellationToken cancellationToken) =>
            _inner.LoadFailedBatchJobsAsync(batchId, afterId, limit, cancellationToken);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public required TransferEngine Engine { get; init; }
        public required MemoryTransferJournal Journal { get; init; }
        public required FakeRemote Remote { get; init; }
        public required ImmediateClock Clock { get; init; }
        public required StorageConnection Connection { get; init; }
        public required string Root { get; init; }

        public static async Task<Fixture> Start(long multipartThreshold = 8 * 1024 * 1024, long partSize = 8 * 1024 * 1024,
            long minimumPart = TransferPlanner.S3MinimumPartSize, bool readOnly = false,
            long downloadThreshold = 128L * 1024 * 1024, long downloadPart = 16L * 1024 * 1024)
        {
            var connection = SampleConnection(readOnly);
            var remote = new FakeRemote();
            var clock = new ImmediateClock();
            var journal = new MemoryTransferJournal();
            var engine = new TransferEngine(remote, journal, new MapConnections(connection),
                Options(multipartThreshold, partSize, minimumPart, downloadThreshold, downloadPart), clock);
            await engine.StartAsync();
            return new Fixture
            {
                Engine = engine,
                Journal = journal,
                Remote = remote,
                Clock = clock,
                Connection = connection,
                Root = Path.Combine(Path.GetTempPath(), "cavdeck-transfer-" + Guid.NewGuid().ToString("N"))
            };
        }

        public string Write(string name, string text)
        {
            Directory.CreateDirectory(Root);
            var path = Path.Combine(Root, name);
            File.WriteAllText(path, text);
            return path;
        }

        public string MakeDirectory(string name)
        {
            var path = Path.Combine(Root, name);
            System.IO.Directory.CreateDirectory(path);
            return path;
        }

        public Task<TransferJob> Wait(Guid id, Func<TransferJob, bool> predicate) =>
            TransferEngineTests.Wait(Engine, id, predicate);

        public async Task WaitAll(Func<TransferJob, bool> predicate)
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline)
            {
                if (Engine.Jobs.Count > 0 && Engine.Jobs.All(predicate))
                    return;
                await Task.Delay(15);
            }

            throw new TimeoutException(string.Join(", ", Engine.Jobs.Select(job => job.State)));
        }

        public async ValueTask DisposeAsync()
        {
            await Engine.DisposeAsync();
            if (System.IO.Directory.Exists(Root))
                System.IO.Directory.Delete(Root, true);
        }
    }
}

internal sealed class ImmediateClock : ITransferClock
{
    public List<TimeSpan> Delays { get; } = [];
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        Delays.Add(delay);
        return Task.CompletedTask;
    }
}

internal sealed class MapConnections(StorageConnection connection) : ITransferConnections
{
    public Task<StorageConnection?> FindAsync(Guid id, CancellationToken cancellationToken) =>
        Task.FromResult(id == connection.Id ? connection : null);
}

internal sealed class CrossConnections(params StorageConnection[] connections) : ITransferConnections
{
    public Task<StorageConnection?> FindAsync(Guid id, CancellationToken cancellationToken) =>
        Task.FromResult(connections.FirstOrDefault(connection => connection.Id == id));
}

internal sealed class FakeRemote : IRemoteObjectTransfer
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Stored> _objects = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Multipart> _uploads = new(StringComparer.Ordinal);

    public int PutCalls;
    public int FailPutsRemaining;
    public bool StoreThenFailPut;
    public bool TruncatePuts;
    public bool PlantForeignOnFailure;
    public bool PlantForeignBeforePutCondition;
    public bool PlantForeignBeforeSecondPutCondition;
    public bool PlantForeignBeforeCompletionCondition;
    public int ConditionalPutCalls;
    public int ConditionalCompleteCalls;
    public bool DenyPuts;
    public bool DenyParts;
    public bool FailListingOnce;
    public bool CorruptAfterComplete;
    public bool CompleteThenFail;
    public bool MutateSourceOnSecondRange;
    public bool BlockPuts;
    public bool ThrowUnexpectedCancellationOnce;
    public bool BlockPutAfterProgress;
    public TaskCompletionSource PutProgressEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource PutProgressRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool BlockPartAfterProgress;
    public TaskCompletionSource PartProgressEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource PartProgressRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool SawMemoryStream;
    public int ActivePuts;
    public int MaxActivePuts;
    public int PartCalls;
    public int ConditionalDeletes;
    public bool MutateBeforeConditionalDelete;
    public bool DeleteThenFailOnce;
    public int? FailPartNumber;
    public bool FailCompleteOnce;
    public bool BlockParts;
    public bool WrapPartCancellation;
    public bool BlockInitiate;
    public bool RejectConditionalMultipart;
    public bool RejectVerifiedPut;
    public int AbortCalls;
    public bool FailAbortOnce;
    public int PendingUploads
    {
        get { lock (_gate) return _uploads.Count; }
    }
    public int DownloadOpens;
    public bool BlockDownloads;
    public int ThrowDownloadAfter = -1;
    public bool MutateDuringDownload;
    public bool RangeWasRequested;
    public bool RejectFixedRanges;
    public bool ThrowUnsupportedRange;
    public long? BlockRangeAtOffset;
    public long? ShortRangeAtOffset;
    public List<long> RangeReadOffsets { get; } = [];
    public TaskCompletionSource RangeReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource RangeReadRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public List<long> PartLengths { get; } = [];
    public List<Stream> PartStreams { get; } = [];
    public TaskCompletionSource PutEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource PutRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource PartEntered { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource PartRelease { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource InitiateEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource InitiateRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void RearmParts()
    {
        PartEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        PartRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    public TaskCompletionSource DownloadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource DownloadRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Put(string bucket, string key, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        _objects[Id(bucket, key)] = new Stored { Bytes = bytes, ETag = Etag(bytes) };
    }

    public void SeedPart(string bucket, string key, string uploadId, int number, long offset, byte[] bytes)
    {
        var id = Id(bucket, key) + "|" + uploadId;
        if (!_uploads.TryGetValue(id, out var upload))
        {
            upload = new Multipart();
            _uploads[id] = upload;
        }

        upload.Parts[number] = new TransferPart(number, offset, bytes.Length, "\"p" + number + "\"");
        upload.Bytes[number] = bytes;
    }

    public string Text(string bucket, string key) => Encoding.UTF8.GetString(Bytes(bucket, key));
    public byte[] Bytes(string bucket, string key) => _objects[Id(bucket, key)].Bytes;
    public bool Contains(string bucket, string key) => _objects.ContainsKey(Id(bucket, key));

    public Task<IRemoteTransferLease> OpenAsync(StorageConnection connection, bool write, CancellationToken cancellationToken)
    {
        if (write && connection.IsReadOnly)
            throw new StorageException(new StorageFailure(StorageFailureKind.ReadOnly, null, null, null, null));
        return Task.FromResult<IRemoteTransferLease>(new Lease(this));
    }

    private sealed class Lease(FakeRemote owner) : IRemoteTransferLease
    {
        public bool SupportsVerifiedConditionalMultipart => !owner.RejectConditionalMultipart;
        public void EnsureVerifiedConditionalDelete() { }
        public void EnsureVerifiedConditionalPut()
        {
            if (owner.RejectVerifiedPut)
                throw new StorageException(new StorageFailure(StorageFailureKind.Unsupported,
                    "ConditionalWriteUnsupported", null, null, null));
        }

        public void EnsureConditionalMultipartSupported()
        {
            if (owner.RejectConditionalMultipart)
                throw new StorageException(new StorageFailure(StorageFailureKind.Unsupported,
                    "ConditionalMultipartUnsupported", null, null, null));
        }

        public Task<RemoteIdentity?> HeadAsync(string bucket, string key, CancellationToken cancellationToken)
        {
            lock (owner._gate)
            {
                return Task.FromResult(owner._objects.TryGetValue(Id(bucket, key), out var stored)
                    ? new RemoteIdentity(stored.ETag, stored.Bytes.Length, DateTimeOffset.UnixEpoch, true)
                    : null);
            }
        }

        public async Task<RemoteIdentity> PutAsync(
            string bucket, string key, Stream content, long length, string? contentType,
            string contentMd5, IProgress<long>? progress, CancellationToken cancellationToken)
        {
            if (owner.ThrowUnexpectedCancellationOnce)
            {
                owner.ThrowUnexpectedCancellationOnce = false;
                throw new TaskCanceledException("The storage request timed out.");
            }

            if (content is MemoryStream)
                owner.SawMemoryStream = true;
            var bytes = await ReadAsync(content, int.MaxValue, cancellationToken);
            Assert.Equal(contentMd5, Convert.ToBase64String(MD5.HashData(bytes)));
            lock (owner._gate)
            {
                owner.ActivePuts++;
                owner.MaxActivePuts = Math.Max(owner.MaxActivePuts, owner.ActivePuts);
            }

            try
            {
                owner.PutEntered.TrySetResult();
                if (owner.BlockPuts)
                    await owner.PutRelease.Task.WaitAsync(cancellationToken);
                if (progress is not null && owner.BlockPutAfterProgress)
                {
                    progress.Report(Math.Max(1, bytes.Length / 3));
                    owner.PutProgressEntered.TrySetResult();
                    await owner.PutProgressRelease.Task.WaitAsync(cancellationToken);
                    progress.Report(Math.Max(1, bytes.Length * 2 / 3));
                }

                lock (owner._gate)
                {
                    owner.PutCalls++;
                    if (owner.DenyPuts)
                        throw Denied();
                    if (owner.StoreThenFailPut)
                    {
                        owner.Store(bucket, key, bytes);
                        owner.StoreThenFailPut = false;
                        throw Network();
                    }

                    if (owner.FailPutsRemaining > 0)
                    {
                        owner.FailPutsRemaining--;
                        if (owner.PlantForeignOnFailure)
                        {
                            owner.Store(bucket, key, [9, 9, 9]);
                            owner.PlantForeignOnFailure = false;
                        }

                        throw Network();
                    }

                    if (owner.TruncatePuts)
                    {
                        owner.Store(bucket, key, bytes[..^1]);
                        var truncated = owner._objects[Id(bucket, key)];
                        return new RemoteIdentity(truncated.ETag, length, DateTimeOffset.UnixEpoch, true);
                    }

                    owner.Store(bucket, key, bytes);
                    var stored = owner._objects[Id(bucket, key)];
                    progress?.Report(bytes.Length);
                    return new RemoteIdentity(stored.ETag, stored.Bytes.Length, DateTimeOffset.UnixEpoch, true);
                }
            }
            finally
            {
                lock (owner._gate)
                    owner.ActivePuts--;
            }
        }

        public Task<RemoteIdentity> PutConditionalAsync(string bucket, string key,
            Stream content, long length, string? contentType, string contentMd5,
            string? expectedETag, bool requireMissing, CancellationToken cancellationToken) =>
            PutConditionalAsync(bucket, key, content, length, contentType, contentMd5,
                expectedETag, requireMissing, null, cancellationToken);

        public async Task<RemoteIdentity> PutConditionalAsync(string bucket, string key,
            Stream content, long length, string? contentType, string contentMd5,
            string? expectedETag, bool requireMissing, IProgress<long>? progress,
            CancellationToken cancellationToken)
        {
            lock (owner._gate)
            {
                owner.ConditionalPutCalls++;
                if (owner.PlantForeignBeforePutCondition ||
                    owner.PlantForeignBeforeSecondPutCondition && owner.ConditionalPutCalls == 2)
                {
                    owner.PlantForeignBeforePutCondition = false;
                    owner.PlantForeignBeforeSecondPutCondition = false;
                    owner.Store(bucket, key, Encoding.UTF8.GetBytes("foreign"));
                }
                owner._objects.TryGetValue(Id(bucket, key), out var existing);
                if (requireMissing && existing is not null || expectedETag is not null && existing?.ETag != expectedETag)
                    throw new StorageException(new StorageFailure(StorageFailureKind.PreconditionFailed,
                        "PreconditionFailed", 412, null, "changed"));
            }
            return await PutAsync(bucket, key, content, length, contentType, contentMd5, progress, cancellationToken);
        }

        public async Task<string> InitiateMultipartAsync(string bucket, string key, string? contentType, CancellationToken cancellationToken)
        {
            var uploadId = "upload-" + Guid.NewGuid().ToString("N");
            lock (owner._gate)
                owner._uploads[Id(bucket, key) + "|" + uploadId] = new Multipart();
            owner.InitiateEntered.TrySetResult();
            if (owner.BlockInitiate)
                await owner.InitiateRelease.Task;
            return uploadId;
        }

        public async Task<string> UploadPartAsync(
            string bucket, string key, string uploadId, int partNumber, Stream content, long length,
            string contentMd5, IProgress<long>? progress, CancellationToken cancellationToken)
        {
            owner.PartStreams.Add(content);
            owner.PartEntered.TrySetResult();
            if (owner.BlockParts && partNumber == 1)
            {
                try
                {
                    await owner.PartRelease.Task.WaitAsync(cancellationToken);
                }
                catch (OperationCanceledException) when (owner.WrapPartCancellation)
                {
                    throw new TaskCanceledException("The operation was canceled.", new IOException("The transport request was aborted."));
                }
            }
            var bytes = await ReadAsync(content, (int)length, cancellationToken);
            Assert.Equal(contentMd5, Convert.ToBase64String(MD5.HashData(bytes)));
            if (progress is not null && owner.BlockPartAfterProgress)
            {
                progress.Report(Math.Max(1, bytes.Length / 2));
                owner.PartProgressEntered.TrySetResult();
                await owner.PartProgressRelease.Task.WaitAsync(cancellationToken);
            }
            lock (owner._gate)
            {
                owner.PartCalls++;
                if (owner.DenyParts)
                    throw Denied();
                owner.PartLengths.Add(bytes.Length);
                if (owner.FailPartNumber == partNumber)
                {
                    owner.FailPartNumber = null;
                    throw Network();
                }

                var upload = owner._uploads[Id(bucket, key) + "|" + uploadId];
                var etag = "\"p" + partNumber + "\"";
                upload.Parts[partNumber] = new TransferPart(partNumber, 0, bytes.Length, etag);
                upload.Bytes[partNumber] = bytes;
                progress?.Report(bytes.Length);
                return etag;
            }
        }

        public Task CompleteMultipartAsync(
            string bucket, string key, string uploadId, IReadOnlyList<TransferPart> parts, CancellationToken cancellationToken)
        {
            lock (owner._gate)
            {
                if (owner.FailCompleteOnce)
                {
                    owner.FailCompleteOnce = false;
                    throw new StorageException(new StorageFailure(
                        StorageFailureKind.InvalidConfiguration, "CompleteFailure", 400, null, "Completion failed."));
                }
                var upload = owner._uploads[Id(bucket, key) + "|" + uploadId];
                var bytes = parts.OrderBy(part => part.Number).SelectMany(part => upload.Bytes[part.Number]).ToArray();
                owner.Store(bucket, key, bytes, multipart: true);
                if (owner.CorruptAfterComplete && bytes.Length > 0)
                {
                    owner.CorruptAfterComplete = false;
                    var corrupted = bytes.ToArray();
                    corrupted[0] ^= 0xff;
                    owner.Store(bucket, key, corrupted, multipart: true);
                }
                owner._uploads.Remove(Id(bucket, key) + "|" + uploadId);
            }

            return Task.CompletedTask;
        }

        public Task CompleteMultipartConditionalAsync(string bucket, string key, string uploadId,
            IReadOnlyList<TransferPart> parts, string? expectedETag, bool requireMissing,
            CancellationToken cancellationToken)
        {
            lock (owner._gate)
            {
                owner.ConditionalCompleteCalls++;
                if (owner.PlantForeignBeforeCompletionCondition)
                {
                    owner.PlantForeignBeforeCompletionCondition = false;
                    owner.Store(bucket, key, Encoding.UTF8.GetBytes("foreign"));
                }
                owner._objects.TryGetValue(Id(bucket, key), out var existing);
                if (requireMissing && existing is not null || expectedETag is not null && existing?.ETag != expectedETag)
                    throw new StorageException(new StorageFailure(StorageFailureKind.PreconditionFailed,
                        "PreconditionFailed", 412, null, "changed"));
            }
            var task = CompleteMultipartAsync(bucket, key, uploadId, parts, cancellationToken);
            if (owner.CompleteThenFail)
            {
                owner.CompleteThenFail = false;
                throw Network();
            }
            return task;
        }

        public Task AbortMultipartAsync(string bucket, string key, string uploadId, CancellationToken cancellationToken)
        {
            lock (owner._gate)
            {
                owner.AbortCalls++;
                if (owner.FailAbortOnce)
                {
                    owner.FailAbortOnce = false;
                    throw Network();
                }
                owner._uploads.Remove(Id(bucket, key) + "|" + uploadId);
            }

            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<TransferPart>> ListPartsAsync(
            string bucket, string key, string uploadId, CancellationToken cancellationToken)
        {
            lock (owner._gate)
            {
                if (!owner._uploads.TryGetValue(Id(bucket, key) + "|" + uploadId, out var upload))
                    throw new StorageException(new StorageFailure(StorageFailureKind.BucketNotFound, "NoSuchUpload", 404, null, "gone"));
                return Task.FromResult<IReadOnlyList<TransferPart>>(upload.Parts.Values.OrderBy(part => part.Number).ToList());
            }
        }

        public Task<DownloadBody> OpenDownloadAsync(string bucket, string key, long? rangeStart, CancellationToken cancellationToken)
        {
            lock (owner._gate)
            {
                owner.DownloadOpens++;
                if (!owner._objects.TryGetValue(Id(bucket, key), out var stored))
                    throw new StorageException(new StorageFailure(StorageFailureKind.BucketNotFound, "NoSuchKey", 404, null, "missing"));
                var identity = new RemoteIdentity(stored.ETag, stored.Bytes.Length, DateTimeOffset.UnixEpoch, true);
                var start = rangeStart ?? 0;
                if (start > 0)
                    owner.RangeWasRequested = true;
                var honored = start == 0 || !false;
                var slice = stored.Bytes.Skip((int)start).ToArray();
                if (owner.MutateDuringDownload)
                {
                    stored.Bytes = [1, 2, 3, 4];
                    stored.ETag = "\"changed\"";
                    owner.MutateDuringDownload = false;
                }

                Stream stream = new MemoryStream(slice);
                if (owner.ThrowDownloadAfter >= 0)
                    stream = new LimitedStream(stream, owner.ThrowDownloadAfter, () => owner.ThrowDownloadAfter = -1);
                if (owner.BlockDownloads)
                    stream = new GatedStream(stream, owner.DownloadStarted, owner.DownloadRelease);
                return Task.FromResult(new DownloadBody { Content = stream, Identity = identity, RangeHonored = honored });
            }
        }

        public Task<DownloadBody> OpenRangeAsync(string bucket, string key, long offset, long length,
            string expectedETag, string? versionId, CancellationToken cancellationToken)
        {
            lock (owner._gate)
            {
                if (owner.MutateSourceOnSecondRange && bucket == "source" && offset > 0)
                {
                    owner.MutateSourceOnSecondRange = false;
                    owner.Store(bucket, key, Encoding.UTF8.GetBytes(new string('z', 20)));
                }
                if (!owner._objects.TryGetValue(Id(bucket, key), out var stored)
                    || stored.ETag != expectedETag)
                    throw new StorageException(new StorageFailure(StorageFailureKind.PreconditionFailed,
                        "SourceChanged", 412, null, "changed"));
                owner.RangeReadOffsets.Add(offset);
                if (owner.ThrowUnsupportedRange)
                    throw new NotSupportedException("Range GET is not implemented.");
                if (owner.RejectFixedRanges)
                    throw new StorageException(new StorageFailure(StorageFailureKind.Network,
                        "InvalidRangeResponse", 200, null, "Ranges not supported"));
                var slice = stored.Bytes.Skip((int)offset).Take((int)length).ToArray();
                if (owner.ShortRangeAtOffset == offset)
                {
                    owner.ShortRangeAtOffset = null;
                    slice = slice[..^1];
                }
                Stream stream = new MemoryStream(slice);
                if (owner.BlockRangeAtOffset == offset)
                    stream = new GatedStream(stream, owner.RangeReadStarted, owner.RangeReadRelease);
                return Task.FromResult(new DownloadBody
                {
                    Content = stream,
                    Identity = new RemoteIdentity(stored.ETag, stored.Bytes.Length, DateTimeOffset.UnixEpoch),
                    RangeHonored = true
                });
            }
        }

        public async IAsyncEnumerable<ListedObject> ListAsync(
            string bucket, string prefix, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            List<ListedObject> items;
            lock (owner._gate)
            {
                items = owner._objects
                    .Where(pair => pair.Key.StartsWith(bucket + "\n" + prefix, StringComparison.Ordinal))
                    .Select(pair => new ListedObject(pair.Key[(bucket.Length + 1)..], pair.Value.Bytes.Length, pair.Value.ETag))
                    .ToList();
            }

            foreach (var item in items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return item;
            }

            await Task.CompletedTask;
        }

        public Task<ListedPage> ListPageAsync(string bucket, string prefix,
            string? continuationToken, CancellationToken cancellationToken)
        {
            lock (owner._gate)
            {
                if (owner.FailListingOnce)
                {
                    owner.FailListingOnce = false;
                    throw Network();
                }
                var page = owner._objects
                    .Where(pair => pair.Key.StartsWith(bucket + "\n" + prefix, StringComparison.Ordinal))
                    .Select(pair => new ListedObject(pair.Key[(bucket.Length + 1)..],
                        pair.Value.Bytes.Length, pair.Value.ETag))
                    .OrderBy(item => item.Key, StringComparer.Ordinal)
                    .Where(item => continuationToken is null
                        || string.CompareOrdinal(item.Key, continuationToken) > 0)
                    .Take(1001).ToList();
                var next = page.Count > 1000 ? page[999].Key : null;
                return Task.FromResult(new ListedPage(page.Take(1000).ToList(), next));
            }
        }

        public Task DeleteAsync(string bucket, string key, CancellationToken cancellationToken)
        {
            lock (owner._gate)
                owner._objects.Remove(Id(bucket, key));
            return Task.CompletedTask;
        }

        public Task DeleteIfMatchAsync(string bucket, string key, string expectedETag,
            CancellationToken cancellationToken)
        {
            lock (owner._gate)
            {
                if (owner.MutateBeforeConditionalDelete)
                {
                    owner.MutateBeforeConditionalDelete = false;
                    owner.Store(bucket, key, Encoding.UTF8.GetBytes("changed source"));
                }
                if (!owner._objects.TryGetValue(Id(bucket, key), out var existing)
                    || existing.ETag != expectedETag)
                    throw new StorageException(new StorageFailure(StorageFailureKind.PreconditionFailed,
                        "PreconditionFailed", 412, null, "The source changed."));
                owner.ConditionalDeletes++;
                owner._objects.Remove(Id(bucket, key));
                if (owner.DeleteThenFailOnce)
                {
                    owner.DeleteThenFailOnce = false;
                    throw Network();
                }
            }
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private void Store(string bucket, string key, byte[] bytes, bool multipart = false)
    {
        var etag = multipart ? "\"" + Guid.NewGuid().ToString("N") + "-1\"" : Etag(bytes);
        _objects[Id(bucket, key)] = new Stored { Bytes = bytes, ETag = etag };
    }

    private static string Id(string bucket, string key) => bucket + "\n" + key;
    private static string Etag(byte[] bytes) => "\"" + Convert.ToHexString(MD5.HashData(bytes)).ToLowerInvariant() + "\"";
    private static StorageException Network() =>
        new(new StorageFailure(StorageFailureKind.Network, "RequestTimeout", 500, null, "timeout"));
    private static StorageException Denied() =>
        new(new StorageFailure(StorageFailureKind.AccessDenied, "AccessDenied", 403, null, "no"));

    private static async Task<byte[]> ReadAsync(Stream stream, int max, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[4];
        while (buffer.Length < max)
        {
            var read = await stream.ReadAsync(chunk.AsMemory(0, Math.Min(chunk.Length, max - (int)buffer.Length)), cancellationToken);
            if (read == 0)
                break;
            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private sealed class Stored
    {
        public byte[] Bytes { get; set; } = [];
        public string ETag { get; set; } = "";
    }

    private sealed class Multipart
    {
        public Dictionary<int, TransferPart> Parts { get; } = [];
        public Dictionary<int, byte[]> Bytes { get; } = [];
    }

    private sealed class LimitedStream(Stream inner, int failAfter, Action onFail) : Stream
    {
        private int _read;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_read >= failAfter)
            {
                onFail();
                throw new IOException("reset");
            }

            var allowed = Math.Min(buffer.Length, failAfter - _read);
            var read = await inner.ReadAsync(buffer[..allowed], cancellationToken);
            _read += read;
            return read;
        }
    }

    private sealed class GatedStream(Stream inner, TaskCompletionSource started, TaskCompletionSource release) : Stream
    {
        private bool _opened;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!_opened)
            {
                _opened = true;
                started.TrySetResult();
                await release.Task.WaitAsync(cancellationToken);
            }

            return await inner.ReadAsync(buffer, cancellationToken);
        }
    }
}
