using Cavdeck.Application.Abstractions;
using Cavdeck.Domain.Storage;

namespace Cavdeck.Application.Explorer;

public sealed record PrefixSearchProgress(int ScannedObjects, int Requests, int Matches);
/// <summary>Continuation within an S3 listing page is necessary when a result limit cuts through a page.</summary>
public sealed record PrefixSearchCursor(string? ContinuationToken, int Offset);
public sealed record PrefixSearchResult(string Prefix, IReadOnlyList<ObjectEntry> Matches,
    int ScannedObjects, int Requests, bool Complete, PrefixSearchCursor? Cursor = null);
public sealed record OperationScanProgress(int ScannedObjects, int ListRequests, int ExistenceChecks);
public sealed record ResolvedExplorerPath(string Bucket, BrowseResult Listing, ObjectHead? File = null);
public enum CopyStage { Checking, Copying, Removing, Deleting }
public sealed record CopyProgress(CopyStage Stage, int Completed, int Total);
public sealed record DeletePreview(Guid ConnectionId, string Bucket, IReadOnlyList<string> Keys, int ListRequests)
{
    public IReadOnlyDictionary<string, string> SourceETags { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
}
public sealed record RenameMapping(string SourceKey, string DestinationKey, bool CreateMarker);
public sealed record RenamePreview(Guid ConnectionId, string Bucket, IReadOnlyList<RenameMapping> Items,
    IReadOnlyList<MutationIssue> Issues, IReadOnlyList<string> ConflictKeys, string TargetKey,
    int ListRequests, int ExistenceChecks)
{
    public int ConflictCount => ConflictKeys.Count;
}

// Bucket browsing and metadata operations. Byte transfers stay in the transfer engine.
public sealed class ExplorerService(IObjectStorageProviderFactory providers, IConnectionCatalog? catalog = null)
{
    public const int PageSize = 200;
    public const int MaxSearchResults = 2000;
    public const int MaxSearchRequestsPerRun = 20;
    public const int MaxSearchObjectsPerRun = 20000;
    public static readonly TimeSpan MaxSearchDurationPerRun = TimeSpan.FromSeconds(20);

    public Task<PrefixSearchResult> SearchPrefixAsync(
        StorageConnection connection, string bucket, string? prefix, string query,
        Action<PrefixSearchProgress>? progress, CancellationToken cancellationToken,
        Action<IReadOnlyList<ObjectEntry>>? onMatches = null, PrefixSearchCursor? cursor = null,
        IReadOnlyCollection<string>? previousKeys = null)
        => SearchObjectsAsync(connection, bucket, prefix, query, matchKeyPrefix: false,
            progress, cancellationToken, onMatches, cursor, previousKeys);

    // The key-prefix listing is case-sensitive and is not a substring search.
    public Task<PrefixSearchResult> SearchKeyPrefixAsync(
        StorageConnection connection, string bucket, string? prefix, string query,
        Action<PrefixSearchProgress>? progress, CancellationToken cancellationToken,
        Action<IReadOnlyList<ObjectEntry>>? onMatches = null, PrefixSearchCursor? cursor = null,
        IReadOnlyCollection<string>? previousKeys = null)
        => SearchObjectsAsync(connection, bucket, prefix, query, matchKeyPrefix: true,
            progress, cancellationToken, onMatches, cursor, previousKeys);

    private async Task<PrefixSearchResult> SearchObjectsAsync(
        StorageConnection connection, string bucket, string? prefix, string query, bool matchKeyPrefix,
        Action<PrefixSearchProgress>? progress, CancellationToken cancellationToken,
        Action<IReadOnlyList<ObjectEntry>>? onMatches, PrefixSearchCursor? cursor,
        IReadOnlyCollection<string>? previousKeys)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (string.IsNullOrWhiteSpace(query))
            throw new ArgumentException("A search term is required.", nameof(query));
        if (cursor is { Offset: < 0 })
            throw new ArgumentOutOfRangeException(nameof(cursor));

        var visible = VisiblePrefix(connection, prefix);
        var needle = query.Trim();
        var listPrefix = matchKeyPrefix ? visible + needle : visible;
        var provider = Provider(connection);
        var matches = new List<ObjectEntry>();
        var seenTokens = new HashSet<string>(StringComparer.Ordinal);
        var seenKeys = previousKeys is null
            ? new HashSet<string>(StringComparer.Ordinal)
            : new HashSet<string>(previousKeys, StringComparer.Ordinal);
        var scanned = 0;
        var requests = 0;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        string? token = cursor?.ContinuationToken;
        var offset = cursor?.Offset ?? 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Budgets are per continuation segment, never an unbounded full-bucket scan.
            if (requests >= MaxSearchRequestsPerRun || scanned >= MaxSearchObjectsPerRun ||
                requests > 0 && stopwatch.Elapsed >= MaxSearchDurationPerRun)
                return new PrefixSearchResult(visible, matches, scanned, requests, false,
                    new PrefixSearchCursor(token, offset));

            var page = await provider.ListObjectsAsync(
                connection, bucket, listPrefix, token, 1000, cancellationToken);
            requests++;
            if (offset > page.Items.Count)
                throw new InvalidOperationException("The search continuation is no longer valid.");

            var batch = new List<ObjectEntry>();
            for (var index = offset; index < page.Items.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var item = page.Items[index];
                if (string.IsNullOrEmpty(item.Key) || !item.Key.StartsWith(listPrefix, StringComparison.Ordinal))
                    continue;
                scanned++;
                if (item.Key.Length <= visible.Length)
                    continue;
                var entry = SearchEntry(item, visible, needle, matchKeyPrefix);
                if (entry is null || !seenKeys.Add(entry.Key))
                    continue;
                matches.Add(entry);
                batch.Add(entry);
                if (matches.Count >= MaxSearchResults)
                {
                    if (batch.Count > 0)
                        onMatches?.Invoke(batch);
                    progress?.Invoke(new PrefixSearchProgress(scanned, requests, matches.Count));
                    var next = index + 1 < page.Items.Count
                        ? new PrefixSearchCursor(token, index + 1)
                        : string.IsNullOrEmpty(page.ContinuationToken)
                            ? null : new PrefixSearchCursor(page.ContinuationToken, 0);
                    return new PrefixSearchResult(visible, matches, scanned, requests,
                        next is null, next);
                }
            }
            if (batch.Count > 0)
                onMatches?.Invoke(batch);
            progress?.Invoke(new PrefixSearchProgress(scanned, requests, matches.Count));
            if (string.IsNullOrEmpty(page.ContinuationToken))
                return new PrefixSearchResult(visible, matches, scanned, requests, Complete: true);
            if (!seenTokens.Add(page.ContinuationToken))
                throw new InvalidOperationException("The storage provider repeated a continuation token.");
            token = page.ContinuationToken;
            offset = 0;
        }
    }

    // S3 folders may be virtual (no explicit trailing-slash object). Return the nearest
    // matching ancestor folder once, rather than returning every unrelated child file.
    private static ObjectEntry? SearchEntry(
        StorageObject item, string visible, string query, bool matchKeyPrefix)
    {
        var name = ObjectLayout.DisplayName(item.Key);
        if (matchKeyPrefix || name.Contains(query, StringComparison.OrdinalIgnoreCase))
            return new ObjectEntry(item.Key.EndsWith('/') ? ObjectEntryKind.Folder : ObjectEntryKind.File,
                name, item.Key, item.SizeBytes, item.LastModified, item.ETag);

        var relative = item.Key[visible.Length..];
        var start = 0;
        while (start < relative.Length)
        {
            var end = relative.IndexOf('/', start);
            if (end < 0)
                break;
            var folderName = relative[start..end];
            if (folderName.Contains(query, StringComparison.OrdinalIgnoreCase))
                return new ObjectEntry(ObjectEntryKind.Folder, folderName,
                    visible + relative[..(end + 1)], 0, null, null);
            start = end + 1;
        }
        return null;
    }

    public Task<IReadOnlyList<StorageBucket>> ListBucketsAsync(
        StorageConnection connection, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return Provider(connection).ListBucketsAsync(connection, cancellationToken);
    }

    public async Task<BrowseResult> BrowseAsync(
        StorageConnection connection, string bucket, string? prefix,
        string? continuationToken, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var visible = VisiblePrefix(connection, prefix);
        var page = await Provider(connection).ListPrefixAsync(
            connection, bucket, visible, continuationToken, PageSize, cancellationToken);
        return ComposePage(visible, page);
    }

    public Task<ResolvedExplorerPath?> ResolvePathAsync(
        StorageConnection connection, string bucket, string currentPrefix, string input,
        CancellationToken cancellationToken) =>
        ResolvePathAsync(connection, bucket, currentPrefix, new ExplorerPathInput(input), cancellationToken);

    public async Task<ResolvedExplorerPath?> ResolvePathAsync(
        StorageConnection connection, string sourceBucket, string currentPrefix, ExplorerPathInput input,
        CancellationToken cancellationToken)
    {
        var root = ObjectLayout.RootPrefix(connection);
        var current = VisiblePrefix(connection, currentPrefix);
        var issue = ExplorerPath.ParseTarget(input, sourceBucket, root, current, out var bucket, out var path);
        if (issue != ExplorerPathIssue.None)
            throw new StorageException(ObjectLayout.Named("ExplorerPath_" + issue));

        ObjectAccess.Ensure(connection, bucket, path.Key, write: false);
        if (!path.Key.StartsWith(root, StringComparison.Ordinal))
            throw new StorageException(new StorageFailure(
                StorageFailureKind.AccessDenied, "RestrictedPrefix", null, null, null));
        var parent = path.Key[..(path.Key.LastIndexOf('/') + 1)];
        // The provider normalizes browse prefixes. Refuse a path that would list a different key.
        if (ObjectLayout.NormalizePrefix(parent) != parent)
            throw new StorageException(ObjectLayout.Named("ExplorerPath_Unbrowsable"));
        cancellationToken.ThrowIfCancellationRequested();
        var provider = Provider(connection);
        if (!path.DirectoryOnly)
        {
            ObjectHead? file = null;
            try
            {
                file = await provider.HeadObjectAsync(connection, bucket, path.Key, cancellationToken);
            }
            catch (StorageException exception) when (IsMissingObject(exception.Failure))
            {
                // Some S3-compatible HEAD responses have a bare 404 with no error body.
            }

            if (file is not null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var listing = await BrowseAsync(connection, bucket, parent, null, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                return new ResolvedExplorerPath(bucket, listing, file);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (input.FileOnly)
            return null;
        var prefix = path.DirectoryOnly ? path.Key : path.Key + "/";
        if (!ExplorerPath.FitsKey(prefix))
            return null;
        if (ObjectLayout.NormalizePrefix(prefix) != prefix)
            return null;
        var page = await provider.ListPrefixAsync(connection, bucket, prefix, null, PageSize, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var exists = prefix == root
            || page.CommonPrefixes.Any(child => child.Length > prefix.Length && child.StartsWith(prefix, StringComparison.Ordinal))
            || page.Objects.Any(item => item.Key.StartsWith(prefix, StringComparison.Ordinal))
            || await provider.ObjectExistsAsync(connection, bucket, prefix, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return exists ? new ResolvedExplorerPath(bucket, ComposePage(prefix, page)) : null;
    }

    private static bool IsMissingObject(StorageFailure failure) =>
        failure.Kind is StorageFailureKind.BucketNotFound or StorageFailureKind.Unknown
        && (AlreadyGone(failure.ErrorCode) || failure.ErrorCode == "404"
            || failure.ErrorCode is null && failure.StatusCode == 404);

    private static BrowseResult ComposePage(string prefix, PrefixPage page) =>
        new(prefix, ObjectLayout.Compose(prefix, page.CommonPrefixes, page.Objects), page.ContinuationToken);

    public async Task<MutationReport> CreateFolderAsync(
        StorageConnection connection, string bucket, string? prefix, string name,
        CancellationToken cancellationToken)
    {
        connection = await Cavdeck.Application.Connections.ConnectionAccess.ResolveAsync(catalog, connection, cancellationToken);
        if (ReadOnly(connection) is { } denied)
            return denied;
        if (!ObjectLayout.TryJoin(VisiblePrefix(connection, prefix), name, folder: true, out var key, out var issue))
            return MutationReport.FromIssue(name ?? "", ObjectLayout.NameFailure(issue));

        var provider = Provider(connection);
        if (await provider.ObjectExistsAsync(connection, bucket, key, cancellationToken))
            return MutationReport.Conflict(1);

        await using var empty = new MemoryStream([]);
        await provider.UploadAsync(connection, bucket, key, empty, null, null, cancellationToken);
        return new MutationReport(1, 0, 0, false, 0, []);
    }

    public async Task<MutationReport> CreateTextFileAsync(
        StorageConnection connection, string bucket, string? prefix, string name,
        CancellationToken cancellationToken)
    {
        connection = await Cavdeck.Application.Connections.ConnectionAccess.ResolveAsync(catalog, connection, cancellationToken);
        if (ReadOnly(connection) is { } denied)
            return denied;
        if (!ObjectLayout.TryJoin(VisiblePrefix(connection, prefix), name, folder: false, out var key, out var issue))
            return MutationReport.FromIssue(name ?? "", ObjectLayout.NameFailure(issue));

        var provider = Provider(connection);
        if (await provider.ObjectExistsAsync(connection, bucket, key, cancellationToken))
            return MutationReport.Conflict(1);

        await using var empty = new MemoryStream([]);
        await provider.UploadAsync(connection, bucket, key, empty, "text/plain; charset=utf-8", null, cancellationToken);
        return new MutationReport(1, 0, 0, false, 0, []);
    }

    public async Task<MutationReport> DeleteAsync(
        StorageConnection connection, string bucket, IReadOnlyList<ObjectEntry> entries,
        CancellationToken cancellationToken)
    {
        connection = await Cavdeck.Application.Connections.ConnectionAccess.ResolveAsync(catalog, connection, cancellationToken);
        if (ReadOnly(connection) is { } denied)
            return denied;
        return await DeleteExpandedAsync(connection, bucket, entries, cancellationToken);
    }

    public async Task<DeletePreview> PreviewDeleteAsync(
        StorageConnection connection, string bucket, IReadOnlyList<ObjectEntry> entries,
        CancellationToken cancellationToken, Action<OperationScanProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ObjectAccess.Ensure(connection, bucket, null, write: true);
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var etags = new Dictionary<string, string>(StringComparer.Ordinal);
        var requests = 0;
        var scanned = 0;
        var provider = Provider(connection);
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObjectAccess.Ensure(connection, bucket, entry.Key, write: true);
            if (entry.Kind == ObjectEntryKind.File)
            {
                keys.Add(entry.Key);
                if (!string.IsNullOrWhiteSpace(entry.ETag))
                    etags[entry.Key] = entry.ETag;
                scanned++;
                progress?.Invoke(new OperationScanProgress(scanned, requests, 0));
            }
            else
                foreach (var key in await ListKeysAsync(provider, connection, bucket, entry.Key,
                    cancellationToken, pageCount =>
                    {
                        requests++;
                        scanned += pageCount;
                        progress?.Invoke(new OperationScanProgress(scanned, requests, 0));
                    }, item =>
                    {
                        if (!string.IsNullOrWhiteSpace(item.ETag))
                            etags[item.Key] = item.ETag;
                    }))
                    keys.Add(key);
        }
        // Some compatible providers omit ETags from listings. Resolve these
        // before showing a destructive confirmation, never after confirmation.
        foreach (var key in keys)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (etags.ContainsKey(key))
                continue;
            var head = await provider.HeadObjectAsync(connection, bucket, key, cancellationToken);
            if (string.IsNullOrWhiteSpace(head.ETag))
                throw new StorageException(new StorageFailure(
                    StorageFailureKind.InvalidConfiguration, "SourceHasNoETag", null, null,
                    "Conditional deletion requires a readable source ETag."));
            etags[key] = head.ETag;
        }
        return new DeletePreview(connection.Id, bucket, keys.Order(StringComparer.Ordinal).ToArray(), requests)
        { SourceETags = etags };
    }

    public async Task<MutationReport> DeletePreparedAsync(
        StorageConnection connection, DeletePreview preview, CancellationToken cancellationToken,
        Action<CopyProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(preview);
        if (preview.ConnectionId != connection.Id)
            throw new ArgumentException("The preview belongs to another connection.", nameof(preview));
        connection = await Cavdeck.Application.Connections.ConnectionAccess.ResolveAsync(catalog, connection, cancellationToken);
        if (ReadOnly(connection) is { } denied)
            return denied;
        foreach (var key in preview.Keys)
            ObjectAccess.Ensure(connection, preview.Bucket, key, write: true);
        return await DeleteKeysAsync(connection, preview.Bucket, preview.Keys, cancellationToken,
            progress, preview.SourceETags);
    }

    public Task<MutationReport> RenameAsync(
        StorageConnection connection, string bucket, ObjectEntry entry, string newName,
        ConflictPolicy policy, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var renamed = entry with { Name = newName.Trim() };
        var parent = ObjectLayout.ParentPrefix(entry.Key) ?? "";
        return CopyAsync(
            connection, bucket, [renamed], bucket, parent,
            deleteSource: true, policy, cancellationToken);
    }

    public async Task<RenamePreview> PreviewRenameAsync(
        StorageConnection connection, string bucket, ObjectEntry entry, string newName,
        CancellationToken cancellationToken, Action<OperationScanProgress>? progress = null)
    {
        ObjectAccess.Ensure(connection, bucket, entry.Key, write: true);
        var provider = Provider(connection);
        if (!provider.Capabilities.CanUseServerSideCopy)
            throw new InvalidOperationException("Server-side copy is unavailable.");
        var parent = ObjectLayout.ParentPrefix(entry.Key) ?? "";
        var renamed = entry with { Name = newName.Trim() };
        var requests = 0;
        var scanned = entry.Kind == ObjectEntryKind.File ? 1 : 0;
        var plan = await PlanAsync(provider, connection, bucket, bucket, [renamed], parent,
            deleteSource: true, cancellationToken, pageCount =>
            {
                requests++;
                scanned += pageCount;
                progress?.Invoke(new OperationScanProgress(scanned, requests, 0));
            });
        var conflicts = new List<string>();
        var checks = 0;
        foreach (var item in plan.Items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await provider.ObjectExistsAsync(connection, bucket, item.DestinationKey, cancellationToken))
                conflicts.Add(item.DestinationKey);
            checks++;
            progress?.Invoke(new OperationScanProgress(scanned, requests, checks));
        }
        var target = ObjectLayout.TryJoin(parent, newName, entry.Kind == ObjectEntryKind.Folder,
            out var key, out _) ? key : "";
        return new RenamePreview(connection.Id, bucket,
            plan.Items.Select(item => new RenameMapping(item.SourceKey, item.DestinationKey, item.CreateMarker)).ToArray(),
            plan.Issues, conflicts, target, requests, plan.Items.Count);
    }

    public async Task<MutationReport> RenamePreparedAsync(
        StorageConnection connection, RenamePreview preview, ConflictPolicy policy,
        CancellationToken cancellationToken, Action<CopyProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(preview);
        if (preview.ConnectionId != connection.Id)
            throw new ArgumentException("The preview belongs to another connection.", nameof(preview));
        connection = await Cavdeck.Application.Connections.ConnectionAccess.ResolveAsync(catalog, connection, cancellationToken);
        if (ReadOnly(connection) is { } denied)
            return denied;
        foreach (var item in preview.Items)
        {
            ObjectAccess.Ensure(connection, preview.Bucket, item.SourceKey, write: true);
            ObjectAccess.Ensure(connection, preview.Bucket, item.DestinationKey, write: true);
        }
        var planned = new Plan(preview.Items.Select(item =>
            new WorkItem(item.SourceKey, item.DestinationKey, item.CreateMarker)).ToList(),
            preview.Issues.ToList());
        var provider = Provider(connection);
        var expected = preview.ConflictKeys.ToHashSet(StringComparer.Ordinal);
        foreach (var item in planned.Items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var exists = await provider.ObjectExistsAsync(connection, preview.Bucket,
                item.DestinationKey, cancellationToken);
            if (exists != expected.Contains(item.DestinationKey))
                return MutationReport.Conflict(1);
        }
        return await ExecuteCopyPlanAsync(provider, connection, preview.Bucket,
            preview.Bucket, planned, deleteSource: true, policy, cancellationToken, progress);
    }

    public async Task<MutationReport> CopyAsync(
        StorageConnection connection,
        string sourceBucket,
        IReadOnlyList<ObjectEntry> entries,
        string destinationBucket,
        string destinationPrefix,
        bool deleteSource,
        ConflictPolicy policy,
        CancellationToken cancellationToken,
        Action<CopyProgress>? progress = null,
        bool resumeCut = false)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(entries);
        connection = await Cavdeck.Application.Connections.ConnectionAccess.ResolveAsync(catalog, connection, cancellationToken);
        if (ReadOnly(connection) is { } denied)
            return denied;

        var provider = Provider(connection);
        if (!provider.Capabilities.CanUseServerSideCopy)
            return MutationReport.FromIssue("*", ObjectLayout.Named("ServerSideCopy"));

        var destination = VisiblePrefix(connection, destinationPrefix);
        var planned = await PlanAsync(
            provider, connection, sourceBucket, destinationBucket, entries, destination, deleteSource, cancellationToken,
            resumeCut: deleteSource && resumeCut,
            renameOnConflict: policy == ConflictPolicy.Rename);
        return await ExecuteCopyPlanAsync(provider, connection, sourceBucket, destinationBucket,
            planned, deleteSource, policy == ConflictPolicy.Rename ? ConflictPolicy.FailIfExists : policy,
            cancellationToken, progress);
    }

    private static async Task<MutationReport> ExecuteCopyPlanAsync(
        IObjectStorageProvider provider, StorageConnection connection, string sourceBucket,
        string destinationBucket, Plan planned, bool deleteSource, ConflictPolicy policy,
        CancellationToken cancellationToken, Action<CopyProgress>? progress = null)
    {
        if (planned.Items.Count == 0)
        {
            return planned.Issues.Count == 0
                ? new MutationReport(planned.AlreadyMoved, 0, 0, false, 0, [])
                : new MutationReport(planned.AlreadyMoved, 0, planned.Issues.Count, false, 0, planned.Issues);
        }

        const int maximumConcurrentChecks = 4;
        var exists = new bool[planned.Items.Count];
        var checkedCount = 0;
        await Parallel.ForEachAsync(Enumerable.Range(0, planned.Items.Count),
            new ParallelOptions { MaxDegreeOfParallelism = maximumConcurrentChecks, CancellationToken = cancellationToken },
            async (index, token) =>
            {
                exists[index] = await provider.ObjectExistsAsync(
                    connection, destinationBucket, planned.Items[index].DestinationKey, token);
                progress?.Invoke(new CopyProgress(CopyStage.Checking,
                    Interlocked.Increment(ref checkedCount), planned.Items.Count));
            });
        var conflicts = planned.Items.Where((_, index) => exists[index])
            .Select(item => item.DestinationKey).ToList();

        if (conflicts.Count > 0 && policy == ConflictPolicy.FailIfExists)
            return MutationReport.Conflict(conflicts.Count);

        var conflictSet = conflicts.ToHashSet(StringComparer.Ordinal);
        var succeeded = planned.AlreadyMoved;
        var skipped = 0;
        var failed = planned.Issues.Count;
        var issues = new List<MutationIssue>(planned.Issues);
        var copied = new List<(WorkItem Item, string? SourceETag)>();

        var processed = 0;
        foreach (var item in planned.Items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (conflictSet.Contains(item.DestinationKey) && policy == ConflictPolicy.SkipExisting)
            {
                skipped++;
                progress?.Invoke(new CopyProgress(CopyStage.Copying, ++processed, planned.Items.Count));
                continue;
            }

            try
            {
                string? sourceETag = null;
                if (deleteSource && !item.CreateMarker
                    && !(sourceBucket == destinationBucket && item.SourceKey == item.DestinationKey))
                {
                    var head = await provider.HeadObjectAsync(connection, sourceBucket,
                        item.SourceKey, cancellationToken);
                    sourceETag = head.ETag;
                    if (string.IsNullOrWhiteSpace(sourceETag))
                    {
                        failed++;
                        issues.Add(new MutationIssue(item.SourceKey, new StorageFailure(
                            StorageFailureKind.InvalidConfiguration, "SourceHasNoETag", null, null,
                            "A source ETag is required for safe moving.")));
                        progress?.Invoke(new CopyProgress(CopyStage.Copying, ++processed, planned.Items.Count));
                        continue;
                    }
                }

                if (item.CreateMarker)
                {
                    await using var empty = new MemoryStream([]);
                    await provider.UploadAsync(
                        connection, destinationBucket, item.DestinationKey, empty, null, null, cancellationToken);
                }
                else
                {
                    await provider.CopyObjectAsync(
                        connection, sourceBucket, item.SourceKey, destinationBucket, item.DestinationKey,
                        cancellationToken);
                }

                succeeded++;
                copied.Add((item, sourceETag));
            }
            catch (StorageException exception)
            {
                failed++;
                issues.Add(new MutationIssue(item.DestinationKey, exception.Failure));
            }
            progress?.Invoke(new CopyProgress(CopyStage.Copying, ++processed, planned.Items.Count));
        }

        if (deleteSource)
        {
            var removed = 0;
            foreach (var copy in copied)
            {
                var item = copy.Item;
                cancellationToken.ThrowIfCancellationRequested();
                if (item.CreateMarker || (sourceBucket == destinationBucket && item.SourceKey == item.DestinationKey))
                {
                    progress?.Invoke(new CopyProgress(CopyStage.Removing, ++removed, copied.Count));
                    continue;
                }
                try
                {
                    await provider.DeleteIfMatchAsync(connection, sourceBucket, item.SourceKey,
                        copy.SourceETag!, cancellationToken);
                }
                catch (StorageException exception) when (AlreadyGone(exception))
                {
                }
                catch (StorageException exception)
                {
                    failed++;
                    issues.Add(new MutationIssue(item.SourceKey, exception.Failure));
                }
                progress?.Invoke(new CopyProgress(CopyStage.Removing, ++removed, copied.Count));
            }
        }

        return new MutationReport(succeeded, skipped, failed, false, conflicts.Count, issues)
        {
            SucceededKeys = copied.Select(copy => copy.Item.DestinationKey).ToArray()
        };
    }

    private async Task<MutationReport> DeleteExpandedAsync(
        StorageConnection connection, string bucket, IReadOnlyList<ObjectEntry> entries,
        CancellationToken cancellationToken)
    {
        var provider = Provider(connection);
        var keys = new List<string>();
        foreach (var entry in entries)
        {
            if (entry.Kind == ObjectEntryKind.File)
            {
                keys.Add(entry.Key);
                continue;
            }

            keys.AddRange(await ListKeysAsync(provider, connection, bucket, entry.Key, cancellationToken));
        }

        return await DeleteKeysAsync(connection, bucket, keys.Distinct(StringComparer.Ordinal).ToList(), cancellationToken);
    }

    private async Task<MutationReport> DeleteKeysAsync(
        StorageConnection connection, string bucket, IReadOnlyList<string> distinct,
        CancellationToken cancellationToken, Action<CopyProgress>? progress = null,
        IReadOnlyDictionary<string, string>? expectedETags = null)
    {
        var provider = Provider(connection);
        if (distinct.Count == 0)
            return MutationReport.None;

        var succeeded = 0;
        var failed = 0;
        var issues = new List<MutationIssue>();
        var processed = 0;
        foreach (var key in distinct)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (expectedETags is not null)
                {
                    if (!expectedETags.TryGetValue(key, out var etag)
                        || string.IsNullOrWhiteSpace(etag))
                        throw new StorageException(new StorageFailure(
                            StorageFailureKind.InvalidConfiguration, "SourceHasNoETag", null, null,
                            "A confirmed delete cannot proceed without an ETag."));
                    await provider.DeleteIfMatchAsync(connection, bucket, key, etag, cancellationToken);
                }
                else
                    await provider.DeleteAsync(connection, bucket, key, cancellationToken);
                succeeded++;
            }
            catch (StorageException exception) when (AlreadyGone(exception))
            {
                succeeded++;
            }
            catch (StorageException exception)
            {
                failed++;
                issues.Add(new MutationIssue(key, exception.Failure));
            }
            progress?.Invoke(new CopyProgress(CopyStage.Deleting, ++processed, distinct.Count));
        }

        return new MutationReport(succeeded, 0, failed, false, 0, issues);
    }

    private async Task<Plan> PlanAsync(
        IObjectStorageProvider provider,
        StorageConnection connection,
        string sourceBucket,
        string destinationBucket,
        IReadOnlyList<ObjectEntry> entries,
        string destinationPrefix,
        bool deleteSource,
        CancellationToken cancellationToken, Action<int>? pageVisited = null,
        bool resumeCut = false, bool renameOnConflict = false)
    {
        var items = new List<WorkItem>();
        var reserved = new HashSet<string>(StringComparer.Ordinal);
        var issues = new List<MutationIssue>();
        var alreadyMoved = 0;

        foreach (var entry in entries)
        {
            var sameBucket = string.Equals(sourceBucket, destinationBucket, StringComparison.Ordinal);
            string destinationKey;
            StorageFailure? issue;
            bool available;
            if (renameOnConflict)
            {
                var allocated = await TryAllocateFreeAsync(provider, connection, destinationBucket,
                    destinationPrefix, entry, deleteSource, sameBucket, reserved, cancellationToken);
                destinationKey = allocated ?? "";
                issue = allocated is null ? ObjectLayout.Named("Name") : null;
                available = allocated is not null;
            }
            else
            {
                available = TryAllocate(destinationPrefix, entry, deleteSource, sameBucket, reserved,
                    out destinationKey, out issue);
            }
            if (!available)
            {
                issues.Add(new MutationIssue(entry.Key, issue!));
                continue;
            }

            if (sameBucket && destinationKey == entry.Key && deleteSource)
                continue;

            if (sameBucket && entry.Kind == ObjectEntryKind.Folder
                && destinationKey != entry.Key
                && ObjectLayout.IsInside(destinationKey, entry.Key))
            {
                issues.Add(new MutationIssue(entry.Key, ObjectLayout.Named("IntoItself")));
                reserved.Remove(destinationKey);
                continue;
            }

            if (entry.Kind == ObjectEntryKind.File)
            {
                if (resumeCut && !await provider.ObjectExistsAsync(connection, sourceBucket, entry.Key, cancellationToken))
                {
                    if (await provider.ObjectExistsAsync(connection, destinationBucket, destinationKey, cancellationToken))
                    {
                        var destinationHead = await provider.HeadObjectAsync(connection, destinationBucket,
                            destinationKey, cancellationToken);
                        if (destinationHead.SizeBytes == entry.SizeBytes)
                            alreadyMoved++;
                        else
                            issues.Add(new MutationIssue(entry.Key, ObjectLayout.Named("DestinationChanged")));
                    }
                    else
                        issues.Add(new MutationIssue(entry.Key, ObjectLayout.Named("SourceMissing")));
                    continue;
                }
                items.Add(new WorkItem(entry.Key, destinationKey, false));
                continue;
            }

            var children = await ListKeysAsync(provider, connection, sourceBucket, entry.Key,
                cancellationToken, pageVisited);
            var mapped = children.Where(child => child.StartsWith(entry.Key, StringComparison.Ordinal)).ToList();
            if (mapped.Count == 0)
            {
                if (resumeCut)
                {
                    var destinationKeys = await ListKeysAsync(provider, connection, destinationBucket,
                        destinationKey, cancellationToken);
                    if (destinationKeys.Count > 0)
                        alreadyMoved++;
                    else
                        items.Add(new WorkItem(entry.Key, destinationKey, true));
                    continue;
                }
                items.Add(new WorkItem(entry.Key, destinationKey, true));
                continue;
            }

            foreach (var child in mapped)
                items.Add(new WorkItem(child, ObjectLayout.MapKey(child, entry.Key, destinationKey), false));
        }

        return new Plan(items, issues, alreadyMoved);
    }

    private static async Task<string?> TryAllocateFreeAsync(
        IObjectStorageProvider provider, StorageConnection connection,
        string destinationBucket, string prefix, ObjectEntry entry,
        bool deleteSource, bool sameBucket, HashSet<string> reserved, CancellationToken token)
    {
        for (var index = 1; index < 100; index++)
        {
            var name = ObjectLayout.SiblingName(entry.Name, index);
            if (!ObjectLayout.TryJoin(prefix, name, entry.Kind == ObjectEntryKind.Folder,
                out var candidate, out _))
                return null;
            if (!deleteSource && sameBucket && candidate == entry.Key)
                continue;
            if (reserved.Contains(candidate))
                continue;

            var occupied = entry.Kind == ObjectEntryKind.File
                ? await provider.ObjectExistsAsync(connection, destinationBucket, candidate, token)
                : await HasFolderContentsAsync(provider, connection, destinationBucket, candidate, token);
            if (occupied)
                continue;
            reserved.Add(candidate);
            return candidate;
        }
        return null;
    }

    private static async Task<bool> HasFolderContentsAsync(
        IObjectStorageProvider provider, StorageConnection connection, string bucket,
        string prefix, CancellationToken token)
    {
        var page = await provider.ListObjectsAsync(connection, bucket, prefix, null, 1, token);
        return page.Items.Any(item => item.Key.StartsWith(prefix, StringComparison.Ordinal));
    }

    private static bool TryAllocate(
        string destinationPrefix, ObjectEntry entry, bool deleteSource, bool sameBucket,
        HashSet<string> reserved, out string key, out StorageFailure? failure)
    {
        key = "";
        failure = null;
        for (var index = 1; index < 100; index++)
        {
            var name = ObjectLayout.SiblingName(entry.Name, index);
            if (!ObjectLayout.TryJoin(destinationPrefix, name, entry.Kind == ObjectEntryKind.Folder, out var candidate, out var issue))
            {
                failure = ObjectLayout.NameFailure(issue);
                return false;
            }

            if (sameBucket && candidate == entry.Key && !deleteSource)
                continue;
            if (!reserved.Add(candidate))
                continue;

            key = candidate;
            return true;
        }

        failure = ObjectLayout.Named("Name");
        return false;
    }

    private static async Task<IReadOnlyList<string>> ListKeysAsync(
        IObjectStorageProvider provider, StorageConnection connection, string bucket, string prefix,
        CancellationToken cancellationToken, Action<int>? pageVisited = null,
        Action<StorageObject>? objectVisited = null)
    {
        var keys = new List<string>();
        var seenTokens = new HashSet<string>(StringComparer.Ordinal);
        string? token = null;
        while (true)
        {
            var page = await provider.ListObjectsAsync(connection, bucket, prefix, token, 1000, cancellationToken);
            var accepted = 0;
            foreach (var item in page.Items)
            {
                if (!string.IsNullOrEmpty(item.Key)
                    && item.Key.StartsWith(prefix, StringComparison.Ordinal))
                {
                    keys.Add(item.Key);
                    objectVisited?.Invoke(item);
                    accepted++;
                }
            }
            pageVisited?.Invoke(accepted);

            if (string.IsNullOrEmpty(page.ContinuationToken))
                break;
            if (!seenTokens.Add(page.ContinuationToken))
                throw new InvalidOperationException("The storage provider repeated a continuation token.");
            token = page.ContinuationToken;
        }

        return keys;
    }

    private static string VisiblePrefix(StorageConnection connection, string? prefix)
    {
        var root = ObjectLayout.RootPrefix(connection);
        var asked = ObjectLayout.NormalizePrefix(prefix);
        if (asked.Length == 0)
            return root;
        if (root.Length > 0 && !asked.StartsWith(root, StringComparison.Ordinal))
            throw new StorageException(new StorageFailure(
                StorageFailureKind.AccessDenied, "RestrictedPrefix", null, null, null));
        return asked;
    }

    private static MutationReport? ReadOnly(StorageConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return connection.IsReadOnly
            ? MutationReport.FromIssue("*", new StorageFailure(StorageFailureKind.ReadOnly, null, null, null, null))
            : null;
    }

    private static bool AlreadyGone(StorageException exception) => AlreadyGone(exception.Failure.ErrorCode);

    private static bool AlreadyGone(string? errorCode) =>
        string.Equals(errorCode, "NoSuchKey", StringComparison.OrdinalIgnoreCase)
        || string.Equals(errorCode, "NotFound", StringComparison.OrdinalIgnoreCase);

    private IObjectStorageProvider Provider(StorageConnection connection) =>
        providers.Create(connection.Provider);

    private sealed record WorkItem(string SourceKey, string DestinationKey, bool CreateMarker);

    private sealed record Plan(List<WorkItem> Items, List<MutationIssue> Issues, int AlreadyMoved = 0);
}
