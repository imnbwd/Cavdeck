using Cavdeck.Domain.Transfers;
using Cavdeck.Infrastructure.Transfers;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Cavdeck.Infrastructure.Tests;

public sealed class SqliteTransferJournalTests
{
    [Fact]
    public async Task ReadOnlyHistoryDoesNotCreateOrMigrateDatabases()
    {
        var root = Directory.CreateTempSubdirectory("cavdeck-readonly-journal-").FullName;
        try
        {
            var missing = Path.Combine(root, "missing", "transfers.db");
            using (var absent = new SqliteTransferJournal(missing, readOnly: true))
                await Assert.ThrowsAsync<SqliteException>(() => absent.ReadHistoryCountsAsync(CancellationToken.None));
            Assert.False(Directory.Exists(Path.GetDirectoryName(missing)));

            var path = Path.Combine(root, "legacy.db");
            await using (var db = new SqliteConnection($"Data Source={path};Pooling=False"))
            {
                await db.OpenAsync();
                await using var command = db.CreateCommand();
                command.CommandText = "CREATE TABLE jobs (id TEXT PRIMARY KEY, state TEXT, updated TEXT, payload TEXT);";
                await command.ExecuteNonQueryAsync();
            }
            var before = await File.ReadAllBytesAsync(path);
            using (var history = new SqliteTransferJournal(path, readOnly: true))
                Assert.Empty(await history.ReadHistoryCountsAsync(CancellationToken.None));
            Assert.Equal(before, await File.ReadAllBytesAsync(path));
            Assert.Single(Directory.EnumerateFiles(root));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Default_journal_path_does_not_share_the_installer_directory()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var expectedRoot = Path.Combine(localAppData, "CavdeckData");
        var path = SqliteTransferJournal.DefaultPath();

        Assert.Equal(Path.Combine(expectedRoot, "transfers.db"), path);
        Assert.True(Directory.Exists(expectedRoot));
        Assert.NotEqual(Path.Combine(localAppData, "Cavdeck"), Path.GetDirectoryName(path));
    }

    [Fact]
    public async Task Cleared_failed_records_do_not_reappear_after_restart_and_parts_are_removed()
    {
        var path = Path.Combine(Path.GetTempPath(), "cavdeck-history-" + Guid.NewGuid().ToString("N") + ".db");
        var connection = Guid.NewGuid();
        var failed = TransferJob.Create(TransferKind.Upload, connection, "bucket", "failed.txt",
            @"C:\source\failed.txt", 10, TransferConflictPolicy.Skip) with
        {
            State = TransferState.Failed,
            Failure = new TransferFailure(TransferFailureKind.AccessDenied, false, "AccessDenied", "private detail")
        };
        var queued = TransferJob.Create(TransferKind.Upload, connection, "bucket", "queued.txt",
            @"C:\source\queued.txt", 10, TransferConflictPolicy.Skip);
        var pending = failed with { Id = Guid.NewGuid(), UploadId = "multipart-pending" };
        using (var journal = new SqliteTransferJournal(path))
        {
            await journal.UpsertAsync(failed, CancellationToken.None);
            await journal.UpsertAsync(queued, CancellationToken.None);
            await journal.UpsertAsync(pending, CancellationToken.None);
            await journal.UpsertPartAsync(failed.Id, new TransferPart(1, 0, 10, "etag"), CancellationToken.None);
            var removed = await journal.RemoveTerminalAsync(
                Cavdeck.Application.Transfers.TransferHistoryScope.Failed, null, CancellationToken.None);
            Assert.Equal(new[] { failed.Id }, removed.JobIds);
        }
        using (var journal = new SqliteTransferJournal(path))
        {
            var remaining = await journal.LoadRecentAsync(10, CancellationToken.None);
            Assert.DoesNotContain(remaining, job => job.Id == failed.Id);
            Assert.Contains(remaining, job => job.Id == queued.Id);
            Assert.Contains(remaining, job => job.Id == pending.Id);
            Assert.Empty(await journal.GetPartsAsync(failed.Id, CancellationToken.None));
        }
        File.Delete(path);
    }

    [Fact]
    public async Task Sqlite_cross_batch_mappings_survive_scanning_and_are_removed_with_last_finished_job()
    {
        var path = Path.Combine(Path.GetTempPath(), "cavdeck-cross-history-" + Guid.NewGuid().ToString("N") + ".db");
        var source = Guid.NewGuid();
        var destination = Guid.NewGuid();
        var batch = new Cavdeck.Application.Transfers.CrossTransferBatch(
            Guid.NewGuid(), source, destination, "source", "target", "",
            [], TransferConflictPolicy.Skip, 0, null, null, 1,
            Cavdeck.Application.Transfers.CrossBatchState.Scanning, null, DateTimeOffset.UtcNow);
        var job = TransferJob.Create(TransferKind.Copy, destination, "target", "file.txt", "",
            10, TransferConflictPolicy.Skip, batch.Id) with
        {
            State = TransferState.Failed,
            CrossTransfer = new CrossTransferSpec(
                new StorageObjectRef(source, "source", "file.txt"),
                new StorageObjectRef(destination, "target", "file.txt"),
                TransferConflictPolicy.Skip)
        };
        using (var journal = new SqliteTransferJournal(path))
        {
            await journal.UpsertBatchAsync(batch, CancellationToken.None);
            Assert.True(await journal.TryCreateCrossJobAsync(job, CancellationToken.None));
            Assert.Empty((await journal.RemoveTerminalAsync(
                Cavdeck.Application.Transfers.TransferHistoryScope.Failed, null, CancellationToken.None)).JobIds);
            await journal.UpsertBatchAsync(batch with
            {
                State = Cavdeck.Application.Transfers.CrossBatchState.Completed
            }, CancellationToken.None);
            var result = await journal.RemoveTerminalAsync(
                Cavdeck.Application.Transfers.TransferHistoryScope.Failed, null, CancellationToken.None);
            Assert.Contains(job.Id, result.JobIds);
            Assert.Contains(batch.Id, result.BatchIds);
        }
        using (var journal = new SqliteTransferJournal(path))
        {
            Assert.Empty(await journal.LoadRecentAsync(10, CancellationToken.None));
            Assert.Empty(await journal.LoadRecentBatchesAsync(10, CancellationToken.None));
        }
        File.Delete(path);
    }

    [Fact]
    public async Task Jobs_and_parts_survive_reopening_the_database()
    {
        var path = Path.Combine(Path.GetTempPath(), "cavdeck-journal-" + Guid.NewGuid().ToString("N") + ".db");
        var job = TransferJob.Create(
            TransferKind.Upload, Guid.NewGuid(), "bucket", "blob.bin", @"C:\out\blob.bin", 20,
            TransferConflictPolicy.Overwrite) with
        {
            UploadId = "upload-1",
            Resume = ResumeMode.Multipart,
            State = TransferState.Running
        };

        var first = new SqliteTransferJournal(path);
        await first.UpsertAsync(job, CancellationToken.None);
        await first.UpsertPartAsync(job.Id, new TransferPart(1, 0, 8, "\"p1\""), CancellationToken.None);
        first.Dispose();

        var second = new SqliteTransferJournal(path);
        var loaded = await second.LoadRecentAsync(10, CancellationToken.None);
        var parts = await second.GetPartsAsync(job.Id, CancellationToken.None);
        second.Dispose();
        File.Delete(path);

        var restored = Assert.Single(loaded);
        Assert.Equal(job.Id, restored.Id);
        Assert.Equal("upload-1", restored.UploadId);
        Assert.Equal(TransferState.Running, restored.State);
        var part = Assert.Single(parts);
        Assert.Equal(8, part.Length);
        Assert.Equal("\"p1\"", part.ETag);
    }

    [Fact]
    public async Task Cross_batch_and_part_checksum_survive_reopen_without_changing_old_records()
    {
        var path = Path.Combine(Path.GetTempPath(), "cavdeck-cross-" + Guid.NewGuid().ToString("N") + ".db");
        var source = Guid.NewGuid();
        var target = Guid.NewGuid();
        var batch = new Cavdeck.Application.Transfers.CrossTransferBatch(
            Guid.NewGuid(), source, target, "source", "target", "目标/",
            [new Cavdeck.Application.Transfers.DownloadItem("a/b.txt", false, 8)],
            TransferConflictPolicy.Rename, 0, "cursor", "a/b.txt", 1,
            Cavdeck.Application.Transfers.CrossBatchState.Scanning, null, DateTimeOffset.UtcNow);
        var job = TransferJob.Create(TransferKind.Copy, target, "target", "目标/b.txt", "", 8,
            TransferConflictPolicy.Rename, batch.Id) with
        {
            CrossTransfer = new CrossTransferSpec(new StorageObjectRef(source, "source", "a/b.txt"),
                new StorageObjectRef(target, "target", "目标/b.txt"), TransferConflictPolicy.Rename)
        };
        using (var first = new SqliteTransferJournal(path))
        {
            await first.UpsertBatchAsync(batch, CancellationToken.None);
            Assert.True(await first.TryCreateCrossJobAsync(job, CancellationToken.None));
            Assert.False(await first.TryCreateCrossJobAsync(job with { Id = Guid.NewGuid() }, CancellationToken.None));
            await first.UpsertPartAsync(job.Id, new TransferPart(1, 0, 8, "p1")
            {
                Sha256 = "abc123"
            }, CancellationToken.None);
        }
        using (var second = new SqliteTransferJournal(path))
        {
            var restored = Assert.Single(await second.LoadUnfinishedBatchesAsync(CancellationToken.None));
            Assert.Equal(batch.Id, restored.Id);
            Assert.Equal(batch.ContinuationToken, restored.ContinuationToken);
            Assert.Equal(batch.Items, restored.Items);
            Assert.Equal(job.CrossTransfer,
                Assert.Single(await second.LoadRecentAsync(10, CancellationToken.None)).CrossTransfer);
            Assert.Equal("abc123", Assert.Single(await second.GetPartsAsync(job.Id, CancellationToken.None)).Sha256);
            var progress = await second.GetBatchProgressAsync(batch.Id, CancellationToken.None);
            Assert.Equal(1, progress.Queued);
            Assert.Equal(1, progress.Open);
        }
        File.Delete(path);
    }

    [Fact]
    public async Task Failed_cross_copy_with_pending_cleanup_is_loaded_after_restart()
    {
        var path = Path.Combine(Path.GetTempPath(), "cavdeck-cleanup-" + Guid.NewGuid().ToString("N") + ".db");
        var destination = Guid.NewGuid();
        var job = TransferJob.Create(TransferKind.Copy, destination, "target", "item", "", 8,
            TransferConflictPolicy.Skip) with
        {
            State = TransferState.Failed,
            UploadId = "orphan-upload",
            CrossTransfer = new CrossTransferSpec(
                new StorageObjectRef(Guid.NewGuid(), "source", "item"),
                new StorageObjectRef(destination, "target", "item"),
                TransferConflictPolicy.Skip, CleanupPending: true)
        };
        using (var first = new SqliteTransferJournal(path))
            await first.UpsertAsync(job, CancellationToken.None);
        using (var second = new SqliteTransferJournal(path))
        {
            var restored = Assert.Single(await second.LoadUnfinishedAsync(CancellationToken.None));
            Assert.Equal(job.Id, restored.Id);
            Assert.True(restored.CrossTransfer?.CleanupPending);
        }
        File.Delete(path);
    }

    [Fact]
    public async Task Existing_parts_table_is_migrated_without_losing_rows()
    {
        var path = Path.Combine(Path.GetTempPath(), "cavdeck-old-journal-" + Guid.NewGuid().ToString("N") + ".db");
        var id = Guid.NewGuid();
        await using (var old = new SqliteConnection($"Data Source={path}"))
        {
            await old.OpenAsync();
            await using var schema = old.CreateCommand();
            schema.CommandText = """
                CREATE TABLE parts (
                    job_id TEXT NOT NULL, number INTEGER NOT NULL, offset INTEGER NOT NULL,
                    length INTEGER NOT NULL, etag TEXT NULL, PRIMARY KEY (job_id, number));
                INSERT INTO parts VALUES ($id, 1, 0, 8, 'legacy-etag');
                """;
            schema.Parameters.AddWithValue("$id", id.ToString("N"));
            await schema.ExecuteNonQueryAsync();
        }
        using (var journal = new SqliteTransferJournal(path))
        {
            var original = Assert.Single(await journal.GetPartsAsync(id, CancellationToken.None));
            Assert.Equal("legacy-etag", original.ETag);
            Assert.Null(original.Sha256);
            await journal.UpsertPartAsync(id, original with { Sha256 = "new-sha" }, CancellationToken.None);
            Assert.Equal("new-sha", Assert.Single(await journal.GetPartsAsync(id, CancellationToken.None)).Sha256);
        }
        File.Delete(path);
    }

    [Fact]
    public async Task Unfinished_jobs_are_loaded_even_when_history_exceeds_page_size()
    {
        var path = Path.Combine(Path.GetTempPath(), "cavdeck-journal-" + Guid.NewGuid().ToString("N") + ".db");
        using var journal = new SqliteTransferJournal(path);
        var connection = Guid.NewGuid();
        var waiting = TransferJob.Create(TransferKind.Download, connection, "bucket", "waiting",
            @"C:\out\waiting", null, TransferConflictPolicy.Overwrite);
        await journal.UpsertAsync(waiting, CancellationToken.None);
        for (var index = 0; index < 510; index++)
        {
            var finished = TransferJob.Create(TransferKind.Download, connection, "bucket", $"done-{index}",
                @"C:\out\done", 0, TransferConflictPolicy.Overwrite) with { State = TransferState.Completed };
            await journal.UpsertAsync(finished, CancellationToken.None);
        }

        Assert.DoesNotContain(await journal.LoadRecentAsync(500, CancellationToken.None), job => job.Id == waiting.Id);
        Assert.Contains(await journal.LoadUnfinishedAsync(CancellationToken.None), job => job.Id == waiting.Id);
    }
}
