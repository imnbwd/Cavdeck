using Cavdeck.Application.Abstractions;
using Cavdeck.Application.Explorer;
using Cavdeck.Domain.Storage;
using Xunit;

namespace Cavdeck.Application.Tests;

public sealed class ExplorerServiceTests
{
    [Fact]
    public async Task Prefix_search_scans_every_page_under_the_current_prefix()
    {
        var store = new MemoryStorage
        {
            Pages =
            [
                [new StorageObject("assets", "images/other.txt", 1, DateTimeOffset.UnixEpoch, null, null)],
                [new StorageObject("assets", "images/deep/logo.png", 2, DateTimeOffset.UnixEpoch, null, null),
                 new StorageObject("assets", "outside/logo.png", 3, DateTimeOffset.UnixEpoch, null, null)]
            ]
        };
        var updates = new List<PrefixSearchProgress>();
        var service = new ExplorerService(new SingleFactory(store));

        var result = await service.SearchPrefixAsync(Connection(), "assets", "images/", "logo",
            updates.Add, CancellationToken.None);

        Assert.True(result.Complete);
        Assert.Equal(2, result.Requests);
        Assert.Equal(2, result.ScannedObjects);
        Assert.Equal("images/deep/logo.png", Assert.Single(result.Matches).Key);
        Assert.Equal("logo.png", result.Matches[0].Name);
        Assert.Equal(2, updates.Count);
    }

    [Fact]
    public async Task Key_prefix_search_lists_only_keys_starting_with_the_requested_prefix()
    {
        var store = new MemoryStorage
        {
            Objects =
            [
                new StorageObject("assets", "images/logo.png", 1, DateTimeOffset.UnixEpoch, null, null),
                new StorageObject("assets", "images/deep/logo.png", 1, DateTimeOffset.UnixEpoch, null, null),
                new StorageObject("assets", "images/Logo-large.png", 1, DateTimeOffset.UnixEpoch, null, null)
            ]
        };
        var service = new ExplorerService(new SingleFactory(store));

        var result = await service.SearchKeyPrefixAsync(Connection(), "assets", "images/", "logo",
            null, CancellationToken.None);

        Assert.Equal("images/logo", store.LastListedPrefix);
        Assert.Equal("images/logo.png", Assert.Single(result.Matches).Key);
        Assert.True(result.Complete);
    }

    [Fact]
    public async Task Prefix_search_honors_cancellation_before_listing()
    {
        var store = new MemoryStorage();
        var service = new ExplorerService(new SingleFactory(store));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.SearchPrefixAsync(Connection(), "assets", "images/", "logo", null, cancellation.Token));
    }

    [Fact]
    public async Task Prefix_search_can_cancel_between_pages()
    {
        var store = new MemoryStorage
        {
            Pages =
            [
                [new StorageObject("assets", "images/one.txt", 1, DateTimeOffset.UnixEpoch, null, null)],
                [new StorageObject("assets", "images/two.txt", 1, DateTimeOffset.UnixEpoch, null, null)]
            ]
        };
        var service = new ExplorerService(new SingleFactory(store));
        using var cancellation = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.SearchPrefixAsync(Connection(), "assets", "images/", "txt",
                _ => cancellation.Cancel(), cancellation.Token));
    }

    [Fact]
    public async Task Prefix_search_marks_result_limit_as_incomplete()
    {
        var store = new MemoryStorage
        {
            Objects = Enumerable.Range(0, ExplorerService.MaxSearchResults + 1)
                .Select(index => new StorageObject("assets", $"images/file-{index}.txt", 1,
                    DateTimeOffset.UnixEpoch, null, null)).ToArray()
        };
        var service = new ExplorerService(new SingleFactory(store));

        var result = await service.SearchPrefixAsync(Connection(), "assets", "images/", "file", null,
            CancellationToken.None);

        Assert.False(result.Complete);
        Assert.Equal(ExplorerService.MaxSearchResults, result.Matches.Count);
    }

    [Fact]
    public async Task Search_results_stream_a_page_at_a_time_and_include_virtual_folder_names()
    {
        var store = new MemoryStorage
        {
            Pages =
            [
                [
                    new StorageObject("assets", "images/reports/notes.txt", 1, DateTimeOffset.UnixEpoch, null, null),
                    new StorageObject("assets", "images/2026/report.csv", 2, DateTimeOffset.UnixEpoch, null, null)
                ],
                [new StorageObject("assets", "images/archive/report.pdf", 3, DateTimeOffset.UnixEpoch, null, null)]
            ]
        };
        var batches = new List<IReadOnlyList<ObjectEntry>>();
        var service = new ExplorerService(new SingleFactory(store));

        var result = await service.SearchPrefixAsync(Connection(), "assets", "images/", "report", null,
            CancellationToken.None, batch => batches.Add(batch.ToArray()));

        Assert.True(result.Complete);
        Assert.Equal(2, batches.Count);
        Assert.Equal(new[] { "images/reports/", "images/2026/report.csv", "images/archive/report.pdf" },
            result.Matches.Select(entry => entry.Key));
        Assert.Equal(ObjectEntryKind.Folder, result.Matches[0].Kind);
        Assert.Equal(result.Matches.Select(entry => entry.Key),
            batches.SelectMany(batch => batch).Select(entry => entry.Key));
    }

    [Fact]
    public async Task Search_can_resume_exactly_after_a_result_limit_within_a_page()
    {
        var store = new MemoryStorage
        {
            Objects = Enumerable.Range(0, ExplorerService.MaxSearchResults + 1)
                .Select(i => new StorageObject("assets", $"images/file-{i}.txt", 1,
                    DateTimeOffset.UnixEpoch, null, null)).ToArray()
        };
        var service = new ExplorerService(new SingleFactory(store));

        var first = await service.SearchPrefixAsync(Connection(), "assets", "images/", "file",
            null, CancellationToken.None);
        Assert.False(first.Complete);
        Assert.NotNull(first.Cursor);
        Assert.Equal(ExplorerService.MaxSearchResults, first.Matches.Count);

        var second = await service.SearchPrefixAsync(Connection(), "assets", "images/", "file",
            null, CancellationToken.None, cursor: first.Cursor);
        Assert.True(second.Complete);
        Assert.Null(second.Cursor);
        Assert.Equal("images/file-2000.txt", Assert.Single(second.Matches).Key);
        Assert.Equal(ExplorerService.MaxSearchResults + 1,
            first.Matches.Select(x => x.Key).Concat(second.Matches.Select(x => x.Key)).Distinct().Count());
    }

    [Fact]
    public async Task Search_budget_pauses_and_resumes_across_s3_pages_without_skipping_matches()
    {
        var count = ExplorerService.MaxSearchRequestsPerRun + 1;
        var store = new MemoryStorage
        {
            Pages = Enumerable.Range(0, count).Select(i => new[]
            {
                new StorageObject("assets", $"images/report-{i:D3}.txt", 1,
                    DateTimeOffset.UnixEpoch, null, null)
            }).ToArray()
        };
        var service = new ExplorerService(new SingleFactory(store));

        var first = await service.SearchPrefixAsync(Connection(), "assets", "images/", "report",
            null, CancellationToken.None);
        Assert.False(first.Complete);
        Assert.NotNull(first.Cursor);
        Assert.Equal(ExplorerService.MaxSearchRequestsPerRun, first.Requests);

        var second = await service.SearchPrefixAsync(Connection(), "assets", "images/", "report",
            null, CancellationToken.None, cursor: first.Cursor);
        Assert.True(second.Complete);
        Assert.Single(second.Matches);
        Assert.Equal(count, first.Matches.Concat(second.Matches).Select(x => x.Key).Distinct().Count());
    }

    [Fact]
    public async Task Virtual_folder_matches_are_unique_across_pages_and_continuations()
    {
        var count = ExplorerService.MaxSearchRequestsPerRun + 1;
        var store = new MemoryStorage
        {
            Pages = Enumerable.Range(0, count).Select(i => new[]
            {
                new StorageObject("assets", $"images/reports/notes-{i}.txt", 1,
                    DateTimeOffset.UnixEpoch, null, null)
            }).ToArray()
        };
        var service = new ExplorerService(new SingleFactory(store));

        var first = await service.SearchPrefixAsync(Connection(), "assets", "images/", "reports",
            null, CancellationToken.None);
        Assert.False(first.Complete);
        Assert.Equal("images/reports/", Assert.Single(first.Matches).Key);

        var second = await service.SearchPrefixAsync(Connection(), "assets", "images/", "reports",
            null, CancellationToken.None, cursor: first.Cursor,
            previousKeys: first.Matches.Select(entry => entry.Key).ToArray());
        Assert.True(second.Complete);
        Assert.Empty(second.Matches);
    }

    [Fact]
    public async Task Key_prefix_search_continuation_remains_case_sensitive()
    {
        var store = new MemoryStorage
        {
            Pages =
            [
                [new StorageObject("assets", "images/report-a.txt", 1, DateTimeOffset.UnixEpoch, null, null)],
                [new StorageObject("assets", "images/report-b.txt", 1, DateTimeOffset.UnixEpoch, null, null)]
            ]
        };
        var service = new ExplorerService(new SingleFactory(store));
        var first = await service.SearchKeyPrefixAsync(Connection(), "assets", "images/", "report",
            null, CancellationToken.None);
        var noMatch = await service.SearchKeyPrefixAsync(Connection(), "assets", "images/", "Report",
            null, CancellationToken.None);
        Assert.Equal(2, first.Matches.Count);
        Assert.Empty(noMatch.Matches);
        Assert.Equal("images/Report", store.LastListedPrefix);
    }

    [Fact]
    public async Task Delete_preview_executes_only_the_confirmed_key_snapshot()
    {
        var store = new MemoryStorage
        {
            Objects = [new StorageObject("assets", "images/folder/a.txt", 1, DateTimeOffset.UnixEpoch, null, null)]
        };
        var service = new ExplorerService(new SingleFactory(store));
        var folder = new ObjectEntry(ObjectEntryKind.Folder, "folder", "images/folder/", 0, null, null);

        var preview = await service.PreviewDeleteAsync(Connection(), "assets", [folder], CancellationToken.None);
        store.Objects = [..store.Objects, new StorageObject("assets", "images/folder/new.txt", 1, DateTimeOffset.UnixEpoch, null, null)];
        var report = await service.DeletePreparedAsync(Connection(preview.ConnectionId), preview, CancellationToken.None);

        Assert.Single(preview.Keys);
        Assert.Equal(1, preview.ListRequests);
        Assert.Equal(1, report.Succeeded);
        Assert.Equal(["images/folder/a.txt"], store.Deleted);
    }

    [Fact]
    public async Task Rename_preview_blocks_when_a_new_destination_conflict_appears()
    {
        var store = new MemoryStorage
        {
            Objects = [new StorageObject("assets", "images/folder/a.txt", 1, DateTimeOffset.UnixEpoch, null, null)]
        };
        var service = new ExplorerService(new SingleFactory(store));
        var connection = Connection();
        var folder = new ObjectEntry(ObjectEntryKind.Folder, "folder", "images/folder/", 0, null, null);
        var preview = await service.PreviewRenameAsync(connection, "assets", folder, "renamed", CancellationToken.None);
        store.Existing.Add("images/renamed/a.txt");

        var report = await service.RenamePreparedAsync(connection, preview, ConflictPolicy.Overwrite, CancellationToken.None);

        Assert.True(report.BlockedOnConflict);
        Assert.Empty(store.Copies);
        Assert.Empty(store.Deleted);
    }

    [Fact]
    public async Task Rename_uses_the_previewed_source_snapshot()
    {
        var store = new MemoryStorage
        {
            Objects = [new StorageObject("assets", "images/folder/a.txt", 1, DateTimeOffset.UnixEpoch, null, null)]
        };
        var service = new ExplorerService(new SingleFactory(store));
        var connection = Connection();
        var folder = new ObjectEntry(ObjectEntryKind.Folder, "folder", "images/folder/", 0, null, null);
        var preview = await service.PreviewRenameAsync(connection, "assets", folder, "renamed", CancellationToken.None);
        store.Objects = [..store.Objects, new StorageObject("assets", "images/folder/new.txt", 1, DateTimeOffset.UnixEpoch, null, null)];

        var report = await service.RenamePreparedAsync(connection, preview, ConflictPolicy.FailIfExists,
            CancellationToken.None);

        Assert.Equal(1, report.Succeeded);
        Assert.Equal([("images/folder/a.txt", "images/renamed/a.txt")], store.Copies);
        Assert.Equal(["images/folder/a.txt"], store.Deleted);
    }

    [Fact]
    public async Task Browse_composes_a_page_and_keeps_the_continuation()
    {
        var store = new MemoryStorage
        {
            PrefixPage = new PrefixPage(
                ["images/photos/"],
                [new StorageObject("assets", "images/logo.png", 4, DateTimeOffset.UnixEpoch, null, null)],
                "next")
        };
        var service = new ExplorerService(new SingleFactory(store));

        var page = await service.BrowseAsync(Connection(), "assets", "images", null, CancellationToken.None);

        Assert.Equal("images/", page.Prefix);
        Assert.Equal("next", page.ContinuationToken);
        Assert.Equal(200, store.PageSize);
        Assert.Equal(["photos", "logo.png"], page.Entries.Select(entry => entry.Name).ToArray());
    }

    [Fact]
    public async Task Read_only_delete_does_not_touch_storage()
    {
        var store = new MemoryStorage();
        var service = new ExplorerService(new SingleFactory(store));
        var entry = new ObjectEntry(ObjectEntryKind.File, "a.txt", "images/a.txt", 1, null, null);

        var report = await service.DeleteAsync(Connection(readOnly: true), "assets", [entry], CancellationToken.None);

        Assert.Equal(StorageFailureKind.ReadOnly, Assert.Single(report.Issues).Failure.Kind);
        Assert.Empty(store.Deleted);
    }

    [Fact]
    public async Task Delete_expands_a_folder_and_keeps_going_after_one_failure()
    {
        var store = new MemoryStorage
        {
            Objects =
            [
                new StorageObject("assets", "images/photos/a.jpg", 1, DateTimeOffset.UnixEpoch, null, null),
                new StorageObject("assets", "images/photos/b.jpg", 1, DateTimeOffset.UnixEpoch, null, null)
            ],
            FailDeletes = ["images/photos/a.jpg"]
        };
        var service = new ExplorerService(new SingleFactory(store));
        var folder = new ObjectEntry(ObjectEntryKind.Folder, "photos", "images/photos/", 0, null, null);

        var report = await service.DeleteAsync(Connection(), "assets", [folder], CancellationToken.None);

        Assert.Equal(1, report.Succeeded);
        Assert.Equal(1, report.Failed);
        Assert.Contains("images/photos/b.jpg", store.Deleted);
        Assert.DoesNotContain("images/photos/a.jpg", store.Deleted);
    }

    [Fact]
    public async Task Rename_does_not_delete_the_source_when_copy_fails()
    {
        var store = new MemoryStorage { FailCopies = ["images/b.txt"] };
        var service = new ExplorerService(new SingleFactory(store));
        var file = new ObjectEntry(ObjectEntryKind.File, "a.txt", "images/a.txt", 1, null, null);

        var report = await service.RenameAsync(
            Connection(), "assets", file, "b.txt", ConflictPolicy.Overwrite, CancellationToken.None);

        Assert.Equal(1, report.Failed);
        Assert.Empty(store.Deleted);
        Assert.Equal("images/b.txt", Assert.Single(store.Copies).Destination);
    }

    [Fact]
    public async Task Copy_blocks_before_writing_when_the_destination_exists()
    {
        var store = new MemoryStorage { Existing = ["images/logo.png"] };
        var service = new ExplorerService(new SingleFactory(store));
        var file = new ObjectEntry(ObjectEntryKind.File, "logo.png", "other/logo.png", 1, null, null);

        var blocked = await service.CopyAsync(
            Connection(), "assets", [file], "assets", "images/", false, ConflictPolicy.FailIfExists, CancellationToken.None);
        var skipped = await service.CopyAsync(
            Connection(), "assets", [file], "assets", "images/", false, ConflictPolicy.SkipExisting, CancellationToken.None);

        Assert.True(blocked.BlockedOnConflict);
        Assert.Empty(store.Copies);
        Assert.Equal(1, skipped.Skipped);
        Assert.Empty(skipped.SucceededKeys);
        Assert.Empty(store.Copies);
    }

    [Fact]
    public async Task Folder_cannot_be_copied_into_itself()
    {
        var store = new MemoryStorage();
        var service = new ExplorerService(new SingleFactory(store));
        var folder = new ObjectEntry(ObjectEntryKind.Folder, "photos", "images/photos/", 0, null, null);

        var report = await service.CopyAsync(
            Connection(), "assets", [folder], "assets", "images/photos/raw/",
            false, ConflictPolicy.Overwrite, CancellationToken.None);

        Assert.Equal("IntoItself", Assert.Single(report.Issues).Failure.ErrorCode);
        Assert.Empty(store.Copies);
    }

    [Fact]
    public async Task Paste_into_the_same_folder_uses_a_sibling_name()
    {
        var store = new MemoryStorage();
        var service = new ExplorerService(new SingleFactory(store));
        var file = new ObjectEntry(ObjectEntryKind.File, "logo.png", "images/logo.png", 1, null, null);

        var report = await service.CopyAsync(
            Connection(), "assets", [file], "assets", "images/", false, ConflictPolicy.FailIfExists, CancellationToken.None);

        Assert.Equal(1, report.Succeeded);
        Assert.Equal(("images/logo.png", "images/logo (2).png"), Assert.Single(store.Copies));
        Assert.Equal("images/logo (2).png", Assert.Single(report.SucceededKeys));
    }

    [Fact]
    public async Task Keep_both_uses_next_available_sibling_for_a_same_folder_copy()
    {
        var store = new MemoryStorage { Existing = ["images/logo (2).png"] };
        var service = new ExplorerService(new SingleFactory(store));
        var file = new ObjectEntry(ObjectEntryKind.File, "logo.png", "images/logo.png", 1, null, null);

        var report = await service.CopyAsync(Connection(), "assets", [file],
            "assets", "images/", false, ConflictPolicy.Rename, CancellationToken.None);

        Assert.Equal(1, report.Succeeded);
        Assert.Equal("images/logo (3).png", Assert.Single(store.Copies).Destination);
    }

    [Fact]
    public async Task Keep_both_renames_destination_file_instead_of_overwriting()
    {
        var store = new MemoryStorage { Existing = ["images/logo.png"] };
        var service = new ExplorerService(new SingleFactory(store));
        var file = new ObjectEntry(ObjectEntryKind.File, "logo.png", "other/logo.png", 1, null, null);

        var report = await service.CopyAsync(Connection(), "assets", [file],
            "assets", "images/", false, ConflictPolicy.Rename, CancellationToken.None);

        Assert.Equal(1, report.Succeeded);
        Assert.Equal(("other/logo.png", "images/logo (2).png"), Assert.Single(store.Copies));
    }

    [Fact]
    public async Task Keep_both_preserves_nested_folder_layout_under_one_new_root()
    {
        var store = new MemoryStorage
        {
            Objects =
            [
                new StorageObject("assets", "images/photos/a.txt", 1, DateTimeOffset.UtcNow, null, null),
                new StorageObject("assets", "images/photos/sub/b.txt", 1, DateTimeOffset.UtcNow, null, null),
                new StorageObject("assets", "images/archive/photos/existing.txt", 1, DateTimeOffset.UtcNow, null, null)
            ]
        };
        var service = new ExplorerService(new SingleFactory(store));
        var folder = new ObjectEntry(ObjectEntryKind.Folder, "photos", "images/photos/", 0, null, null);

        var report = await service.CopyAsync(Connection(), "assets", [folder],
            "assets", "images/archive/", false, ConflictPolicy.Rename, CancellationToken.None);

        Assert.Equal(2, report.Succeeded);
        Assert.Contains(store.Copies, item => item.Destination == "images/archive/photos (2)/a.txt");
        Assert.Contains(store.Copies, item => item.Destination == "images/archive/photos (2)/sub/b.txt");
        Assert.DoesNotContain(store.Copies, item => item.Destination.StartsWith("images/archive/photos/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Cut_to_another_bucket_with_the_same_key_copies_then_deletes_source()
    {
        var store = new MemoryStorage();
        var service = new ExplorerService(new SingleFactory(store));
        var file = new ObjectEntry(ObjectEntryKind.File, "logo.png", "images/logo.png", 1, null, null);

        var report = await service.CopyAsync(Connection(), "source", [file], "target", "images/",
            true, ConflictPolicy.FailIfExists, CancellationToken.None);

        Assert.Equal(1, report.Succeeded);
        Assert.Equal(("source", "images/logo.png", "target", "images/logo.png"),
            Assert.Single(store.CopyLocations));
        Assert.Equal("images/logo.png", Assert.Single(store.Deleted));
    }

    [Fact]
    public async Task Copy_to_another_bucket_keeps_the_original_name()
    {
        var store = new MemoryStorage();
        var service = new ExplorerService(new SingleFactory(store));
        var file = new ObjectEntry(ObjectEntryKind.File, "logo.png", "images/logo.png", 1, null, null);

        var report = await service.CopyAsync(Connection(), "source", [file], "target", "images/",
            false, ConflictPolicy.FailIfExists, CancellationToken.None);

        Assert.Equal(1, report.Succeeded);
        Assert.Equal("images/logo.png", Assert.Single(store.Copies).Destination);
    }

    [Fact]
    public async Task Folder_can_copy_to_a_matching_nested_key_in_another_bucket()
    {
        var store = new MemoryStorage
        {
            Objects = [new StorageObject("source", "images/photos/a.txt", 1, DateTimeOffset.UtcNow, null, null)]
        };
        var service = new ExplorerService(new SingleFactory(store));
        var folder = new ObjectEntry(ObjectEntryKind.Folder, "photos", "images/photos/", 0, null, null);

        var report = await service.CopyAsync(Connection(), "source", [folder], "target", "images/photos/raw/",
            false, ConflictPolicy.FailIfExists, CancellationToken.None);

        Assert.Equal(1, report.Succeeded);
        Assert.Equal("images/photos/raw/photos/a.txt", Assert.Single(store.Copies).Destination);
    }

    [Fact]
    public async Task Canceling_a_move_between_objects_preserves_uncopied_sources()
    {
        var store = new MemoryStorage();
        var service = new ExplorerService(new SingleFactory(store));
        var files = new[]
        {
            new ObjectEntry(ObjectEntryKind.File, "a.txt", "images/a.txt", 1, null, null),
            new ObjectEntry(ObjectEntryKind.File, "b.txt", "images/b.txt", 1, null, null)
        };
        using var cancel = new CancellationTokenSource();
        var progress = new System.Collections.Concurrent.ConcurrentQueue<CopyProgress>();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.CopyAsync(
            Connection(), "source", files, "target", "images/", true,
            ConflictPolicy.FailIfExists, cancel.Token, update =>
            {
                progress.Enqueue(update);
                if (update.Stage == CopyStage.Copying && update.Completed == 1)
                    cancel.Cancel();
            }));

        Assert.Single(store.Copies);
        Assert.Empty(store.Deleted);
        Assert.Contains(progress, update => update.Stage == CopyStage.Checking && update.Completed == 2);
    }

    [Fact]
    public async Task Move_with_failed_source_delete_reports_partial_completion()
    {
        var store = new MemoryStorage { FailDeletes = ["images/a.txt"] };
        var service = new ExplorerService(new SingleFactory(store));
        var file = new ObjectEntry(ObjectEntryKind.File, "a.txt", "images/a.txt", 1, null, null);

        var report = await service.CopyAsync(Connection(), "source", [file], "target", "images/",
            true, ConflictPolicy.FailIfExists, CancellationToken.None);

        Assert.Equal(1, report.Succeeded);
        Assert.Equal(1, report.Failed);
        Assert.False(report.IsFullSuccess);
        Assert.Empty(store.Deleted);
    }

    [Fact]
    public async Task Retrying_a_partially_completed_cut_skips_objects_already_moved()
    {
        var store = new MemoryStorage
        {
            FailCopies = ["images/b.txt"],
            ExistingObjects = [("source", "images/a.txt"), ("source", "images/b.txt")]
        };
        var service = new ExplorerService(new SingleFactory(store));
        var files = new[]
        {
            new ObjectEntry(ObjectEntryKind.File, "a.txt", "images/a.txt", 1, null, null),
            new ObjectEntry(ObjectEntryKind.File, "b.txt", "images/b.txt", 1, null, null)
        };

        var first = await service.CopyAsync(Connection(), "source", files, "target", "images/",
            true, ConflictPolicy.FailIfExists, CancellationToken.None);
        Assert.False(first.IsFullSuccess);
        Assert.DoesNotContain(("source", "images/a.txt"), store.ExistingObjects);
        store.FailCopies.Clear();

        var retry = await service.CopyAsync(Connection(), "source", files, "target", "images/",
            true, ConflictPolicy.FailIfExists, CancellationToken.None, resumeCut: true);

        Assert.True(retry.IsFullSuccess);
        Assert.Equal(2, retry.Succeeded);
        Assert.Equal(1, store.CopyLocations.Count(copy => copy.Source == "images/a.txt"));
        Assert.DoesNotContain(store.ExistingObjects, item => item.Item1 == "source");
    }

    [Fact]
    public async Task Retrying_a_cut_does_not_accept_a_different_sized_destination()
    {
        var store = new MemoryStorage { ExistingObjects = [("target", "images/a.txt")] };
        var service = new ExplorerService(new SingleFactory(store));
        var file = new ObjectEntry(ObjectEntryKind.File, "a.txt", "images/a.txt", 2, null, null);

        var report = await service.CopyAsync(Connection(), "source", [file], "target", "images/",
            true, ConflictPolicy.FailIfExists, CancellationToken.None, resumeCut: true);

        Assert.False(report.IsFullSuccess);
        Assert.Equal(1, report.Failed);
        Assert.Empty(store.Copies);
    }

    [Fact]
    public async Task Canceling_a_confirmed_delete_stops_before_the_next_key()
    {
        var store = new MemoryStorage();
        var service = new ExplorerService(new SingleFactory(store));
        var connection = Connection();
        var preview = new DeletePreview(connection.Id, "assets", ["images/a.txt", "images/b.txt"], 0)
        {
            SourceETags = new Dictionary<string, string>
            {
                ["images/a.txt"] = "mock-etag", ["images/b.txt"] = "mock-etag"
            }
        };
        using var cancel = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.DeletePreparedAsync(
            connection, preview, cancel.Token, progress =>
            {
                if (progress.Stage == CopyStage.Deleting && progress.Completed == 1)
                    cancel.Cancel();
            }));

        Assert.Equal("images/a.txt", Assert.Single(store.Deleted));
    }

    [Fact]
    public async Task Destination_checks_use_bounded_parallelism()
    {
        var store = new MemoryStorage { ExistenceDelay = TimeSpan.FromMilliseconds(25) };
        var service = new ExplorerService(new SingleFactory(store));
        var files = Enumerable.Range(0, 12)
            .Select(index => new ObjectEntry(ObjectEntryKind.File, $"{index}.txt",
                $"images/{index}.txt", 1, null, null)).ToArray();

        var report = await service.CopyAsync(Connection(), "source", files, "target", "images/",
            false, ConflictPolicy.FailIfExists, CancellationToken.None);

        Assert.Equal(12, report.Succeeded);
        Assert.InRange(store.MaxConcurrentChecks, 2, 4);
    }

    [Fact]
    public async Task New_text_file_uses_visible_prefix_and_never_overwrites()
    {
        var store = new MemoryStorage { Existing = ["images/existing.txt"] };
        var service = new ExplorerService(new SingleFactory(store));

        var created = await service.CreateTextFileAsync(Connection(), "assets", "images/", "notes.txt", CancellationToken.None);
        var blocked = await service.CreateTextFileAsync(Connection(), "assets", "images/", "existing.txt", CancellationToken.None);
        var denied = await service.CreateTextFileAsync(Connection(readOnly: true), "assets", "images/", "readonly.txt", CancellationToken.None);

        Assert.Equal(1, created.Succeeded);
        Assert.Equal(("images/notes.txt", "text/plain; charset=utf-8", 0L), Assert.Single(store.Uploads));
        Assert.True(blocked.BlockedOnConflict);
        Assert.Equal(0, denied.Succeeded);
        Assert.Single(store.Uploads);
    }


    [Fact]
    public async Task Confirmed_delete_refuses_to_remove_a_modified_object()
    {
        var store = new MemoryStorage
        {
            Objects = [new StorageObject("assets", "images/a.txt", 1,
                DateTimeOffset.UnixEpoch, "mock-etag", null)]
        };
        var service = new ExplorerService(new SingleFactory(store));
        var connection = Connection();
        var entry = new ObjectEntry(ObjectEntryKind.File, "a.txt", "images/a.txt",
            1, DateTimeOffset.UnixEpoch, "mock-etag");
        var preview = await service.PreviewDeleteAsync(connection, "assets", [entry], CancellationToken.None);
        store.ChangeSourceBeforeConditionalDelete = true;

        var report = await service.DeletePreparedAsync(connection, preview, CancellationToken.None);

        Assert.Equal(1, report.Failed);
        Assert.Empty(store.Deleted);
    }

    [Fact]
    public async Task Move_does_not_delete_a_source_modified_after_copy()
    {
        var store = new MemoryStorage { ChangeSourceBeforeConditionalDelete = true };
        var service = new ExplorerService(new SingleFactory(store));
        var connection = Connection();
        var entry = new ObjectEntry(ObjectEntryKind.File, "a.txt", "images/a.txt",
            1, DateTimeOffset.UnixEpoch, "mock-etag");

        var report = await service.CopyAsync(connection, "assets", [entry],
            "assets", "images/other/", true, ConflictPolicy.FailIfExists, CancellationToken.None);

        Assert.NotEmpty(store.Copies);
        Assert.Empty(store.Deleted);
        Assert.Equal(1, report.Failed);
    }

    private static StorageConnection Connection(bool readOnly = false) => Connection(Guid.NewGuid(), readOnly);

    private static StorageConnection Connection(Guid id, bool readOnly = false) => new(
        id, "Local", StorageProviderKind.Minio, "http://127.0.0.1:9000", "us-east-1",
        "Cavdeck:test", readOnly, KeyPrefix: "images");

    private sealed class SingleFactory(IObjectStorageProvider provider) : IObjectStorageProviderFactory
    {
        public IObjectStorageProvider Create(StorageProviderKind kind) => provider;
    }

    private sealed class MemoryStorage : IObjectStorageProvider
    {
        public List<(string Source, string Destination)> Copies { get; } = [];
        public List<(string SourceBucket, string Source, string DestinationBucket, string Destination)> CopyLocations { get; } = [];
        public List<string> Deleted { get; } = [];
        public List<(string Key, string? ContentType, long Length)> Uploads { get; } = [];
        public HashSet<string> Existing { get; set; } = [];
        public HashSet<(string Bucket, string Key)> ExistingObjects { get; set; } = [];
        public HashSet<string> FailDeletes { get; set; } = [];
        public bool ChangeSourceBeforeConditionalDelete { get; set; }
        public HashSet<string> FailCopies { get; set; } = [];
        public PrefixPage PrefixPage { get; set; } = new([], [], null);
        public StorageObject[] Objects { get; set; } = [];
        public StorageObject[][]? Pages { get; set; }
        public int PageSize { get; private set; }
        public string? LastListedPrefix { get; private set; }
        public TimeSpan ExistenceDelay { get; init; }
        private int _activeChecks;
        private int _maxConcurrentChecks;
        public int MaxConcurrentChecks => _maxConcurrentChecks;

        public StorageProviderKind Kind => StorageProviderKind.Minio;
        public ProviderCapabilities Capabilities { get; } = ConnectionPlanner.Capabilities(StorageProviderKind.Minio);

        public Task<IReadOnlyList<StorageBucket>> ListBucketsAsync(
            StorageConnection connection, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<StorageBucket>>([new StorageBucket("assets", null)]);

        public Task<StoragePage<StorageObject>> ListObjectsAsync(
            StorageConnection connection, string bucket, string prefix, string? continuationToken, int pageSize,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastListedPrefix = prefix;
            if (Pages is { } pages)
            {
                var index = continuationToken is null ? 0 : int.Parse(continuationToken);
                var items = pages[index].Where(item => item.Key.StartsWith(prefix, StringComparison.Ordinal)).ToList();
                return Task.FromResult(new StoragePage<StorageObject>(items,
                    index + 1 < pages.Length ? (index + 1).ToString() : null));
            }
            return Task.FromResult(new StoragePage<StorageObject>(
                Objects.Where(item => item.Key.StartsWith(prefix, StringComparison.Ordinal)).ToList(), null));
        }

        public Task<PrefixPage> ListPrefixAsync(
            StorageConnection connection, string bucket, string prefix, string? continuationToken, int pageSize,
            CancellationToken cancellationToken)
        {
            PageSize = pageSize;
            return Task.FromResult(PrefixPage);
        }

        public async Task<bool> ObjectExistsAsync(
            StorageConnection connection, string bucket, string key, CancellationToken cancellationToken)
        {
            if (ExistenceDelay > TimeSpan.Zero)
            {
                var active = Interlocked.Increment(ref _activeChecks);
                var maximum = _maxConcurrentChecks;
                while (active > maximum && Interlocked.CompareExchange(ref _maxConcurrentChecks, active, maximum) != maximum)
                    maximum = _maxConcurrentChecks;
                try { await Task.Delay(ExistenceDelay, cancellationToken); }
                finally { Interlocked.Decrement(ref _activeChecks); }
            }
            return Existing.Contains(key) || ExistingObjects.Contains((bucket, key));
        }

        public Task CopyObjectAsync(
            StorageConnection connection, string sourceBucket, string sourceKey, string destinationBucket,
            string destinationKey, CancellationToken cancellationToken)
        {
            Copies.Add((sourceKey, destinationKey));
            CopyLocations.Add((sourceBucket, sourceKey, destinationBucket, destinationKey));
            if (FailCopies.Contains(destinationKey))
                throw new StorageException(new StorageFailure(StorageFailureKind.Network, "SlowDown", 503, null, null));
            ExistingObjects.Add((destinationBucket, destinationKey));
            return Task.CompletedTask;
        }

        public Task UploadAsync(
            StorageConnection connection, string bucket, string key, Stream content, string? contentType,
            IProgress<long>? progress, CancellationToken cancellationToken)
        {
            Uploads.Add((key, contentType, content.Length));
            return Task.CompletedTask;
        }

        public Task DownloadAsync(
            StorageConnection connection, string bucket, string key, Stream destination, IProgress<long>? progress,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task DeleteAsync(
            StorageConnection connection, string bucket, string key, CancellationToken cancellationToken)
        {
            if (FailDeletes.Contains(key))
                throw new StorageException(new StorageFailure(StorageFailureKind.AccessDenied, "AccessDenied", 403, null, null));
            Deleted.Add(key);
            ExistingObjects.Remove((bucket, key));
            return Task.CompletedTask;
        }

        public Task DeleteIfMatchAsync(
            StorageConnection connection, string bucket, string key, string expectedETag,
            CancellationToken cancellationToken)
        {
            if (ChangeSourceBeforeConditionalDelete || expectedETag != "mock-etag")
            {
                ChangeSourceBeforeConditionalDelete = false;
                throw new StorageException(new StorageFailure(
                    StorageFailureKind.PreconditionFailed, "PreconditionFailed", 412, null, null));
            }
            return DeleteAsync(connection, bucket, key, cancellationToken);
        }

        public Task<ObjectHead> HeadObjectAsync(
            StorageConnection connection, string bucket, string key, CancellationToken cancellationToken) =>
            Task.FromResult(new ObjectHead(bucket, key,
                ExistingObjects.Contains((bucket, key)) ? 1 : 0, "mock-etag", null));

        public Task<ObjectBody> ReadObjectAsync(
            StorageConnection connection, string bucket, string key, int maxBytes, CancellationToken cancellationToken) =>
            Task.FromResult(new ObjectBody(new ObjectHead(bucket, key, 0, null, null), [], false));

        public Task<ObjectHead> ReplaceObjectAsync(
            StorageConnection connection, string bucket, string key, Stream content, string? contentType,
            string? ifMatchETag, CancellationToken cancellationToken) =>
            Task.FromResult(new ObjectHead(bucket, key, 0, ifMatchETag, contentType));
    }
}
