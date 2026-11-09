using System.Threading.Channels;
using Cavdeck.Application.Abstractions;
using Cavdeck.Domain.Storage;
using Cavdeck.Domain.Transfers;

namespace Cavdeck.Application.Transfers;

public sealed partial class TransferEngine : ITransferActivity, ITransferSink, IDisposable, IAsyncDisposable
{
    private readonly IRemoteObjectTransfer _remote;
    private readonly ITransferJournal _journal;
    private readonly ITransferConnections _connections;
    private readonly ITransferClock _clock;
    private readonly TransferEngineOptions _options;
    private readonly ConcurrencyGate _slots;
    private readonly ConcurrencyGate _parts;
    private readonly UploadCoordinator _uploads;
    private readonly DownloadCoordinator _downloads;
    private readonly CrossTransferCoordinator _crossCopies;
    private readonly CrossPartBudget _crossPartBudget;
    private readonly Channel<Guid> _ready = Channel.CreateUnbounded<Guid>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Dictionary<Guid, TransferJob> _jobs = [];
    private readonly Dictionary<Guid, CrossTransferBatch> _batches = [];
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, CancellationTokenSource> _scanControls = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, CancellationTokenSource> _controls = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Task, byte> _running = new();
    private readonly SemaphoreSlim _batchScanner = new(1, 1);
    private TaskCompletionSource<bool> _capacityChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _gate = new();
    private readonly SemaphoreSlim _persist = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private Task? _pump;
    private int _started;
    private int _disposed;
    private int _activeBatchScans;
    private int _terminalSincePrune;
    private volatile bool _stopping;
    private StorageConnection? _isolatedConnection;

    public TransferEngine(
        IRemoteObjectTransfer remote,
        ITransferJournal journal,
        ITransferConnections connections,
        TransferEngineOptions? options = null,
        ITransferClock? clock = null)
    {
        _remote = remote;
        _journal = journal;
        _connections = connections;
        _clock = clock ?? new SystemTransferClock();
        _options = (options ?? new TransferEngineOptions()).Clamp();
        _slots = new ConcurrencyGate(_options.MaxConcurrentTransfers);
        _parts = new ConcurrencyGate(_options.MaxConcurrentParts);
        _uploads = new UploadCoordinator(this, _options, _clock, _parts);
        _downloads = new DownloadCoordinator(this, _clock, _options, _parts);
        _crossPartBudget = new CrossPartBudget(_options.CrossCopyTempBudgetBytes);
        _crossCopies = new CrossTransferCoordinator(this, _journal, _remote, _options, _clock, _crossPartBudget);
    }

    public event Action<TransferJob>? Changed;
    public event Action<Guid>? Removed;
    public event Action<Guid>? BatchRemoved;
    public event Action<CrossTransferBatch>? BatchChanged;
    public event Action<Exception>? BackgroundFaulted;

    public IReadOnlyList<CrossTransferBatch> Batches
    {
        get
        {
            lock (_gate)
                return _batches.Values.OrderByDescending(batch => batch.UpdatedAt).ToList();
        }
    }

    public Task<CrossBatchProgress> GetBatchProgressAsync(Guid batchId,
        CancellationToken cancellationToken = default) =>
        _journal.GetBatchProgressAsync(batchId, cancellationToken);

    public bool HasActiveTransfers
    {
        get
        {
            lock (_gate)
                return _activeBatchScans > 0 || _jobs.Values.Any(job => job.BlocksUpdates);
        }
    }

    public int UnfinishedCount
    {
        get
        {
            lock (_gate)
                return _activeBatchScans + _jobs.Values.Count(job => job.BlocksShutdown);
        }
    }

    public IReadOnlyList<TransferJob> Jobs
    {
        get
        {
            lock (_gate)
                return _jobs.Values.OrderBy(job => job.CreatedAt).ToList();
        }
    }

    public TransferJob? TryCurrent(Guid id)
    {
        lock (_gate)
            return _jobs.GetValueOrDefault(id);
    }

    public void SetMaxConcurrent(int max) => _slots.SetLimit(Math.Clamp(max, 1, 8));

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _started, 1) == 1)
            return;

        if (_options.IsolatedRun)
        {
            if ((await _journal.LoadRecentAsync(1, cancellationToken)).Count > 0)
                throw new InvalidOperationException("An isolated run requires a fresh transfer journal.");
            _pump = Task.Run(() => PumpAsync(_stop.Token));
            TrackBackground(_pump);
            return;
        }

        var recent = await _journal.LoadRecentAsync(500, cancellationToken);
        var unfinished = await _journal.LoadUnfinishedAsync(cancellationToken);
        foreach (var loaded in recent.Concat(unfinished).DistinctBy(job => job.Id).OrderBy(job => job.CreatedAt))
        {
            var job = loaded.State switch
            {
                TransferState.Running => loaded with { State = TransferState.Queued, UpdatedAt = _clock.UtcNow },
                TransferState.WaitingRetry => loaded with { State = TransferState.Queued, NextAttemptAt = null, UpdatedAt = _clock.UtcNow },
                _ => loaded
            };
            lock (_gate)
                _jobs[job.Id] = job;
            if (job.State != loaded.State)
                await _journal.UpsertAsync(job, cancellationToken);
            if (job.State == TransferState.Queued)
                _ready.Writer.TryWrite(job.Id);
            RaiseChanged(job);
        }

        var batches = (await _journal.LoadRecentBatchesAsync(100, cancellationToken))
            .Concat(await _journal.LoadUnfinishedBatchesAsync(cancellationToken))
            .DistinctBy(batch => batch.Id).ToList();
        lock (_gate)
        {
            foreach (var batch in batches)
                _batches[batch.Id] = batch;
        }
        foreach (var batch in batches)
            RaiseBatchChanged(batch);
        _pump = Task.Run(() => PumpAsync(_stop.Token));
        TrackBackground(_pump);
        foreach (var pending in _jobs.Values.Where(job => job.CrossTransfer?.CleanupPending == true).ToList())
            TrackBackground(Task.Run(() => RetryMultipartCleanupAsync(pending, _stop.Token)));
        foreach (var batch in batches.Where(batch => batch.State == CrossBatchState.Scanning))
            TrackBackground(Task.Run(() => ScanBatchSafelyAsync(batch, null, _stop.Token)));
    }

    // Only the Ask-on-conflict preference needs a preflight. Short-circuit on the
    // first existing destination; execution still uses conditional writes.
    public async Task<string?> FindUploadConflictAsync(
        StorageConnection connection, string bucket, string keyPrefix, IReadOnlyList<string> paths,
        CancellationToken cancellationToken = default)
    {
        connection = await CurrentConnectionAsync(connection, cancellationToken);
        ObjectAccess.Ensure(connection, bucket, null, write: true);
        var prefix = ObjectLayout.NormalizePrefix(ObjectAccess.CombinePrefix(connection, keyPrefix));
        await using var lease = await _remote.OpenAsync(connection, write: true, cancellationToken);
        foreach (var source in UploadCatalog.Enumerate(prefix, paths))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (source.Error is not null || source.FolderMarker)
                continue;
            if (await lease.HeadAsync(bucket, source.Key, cancellationToken) is not null)
                return Path.GetFileName(source.LocalPath);
        }

        return null;
    }

    public async Task<string?> FindDownloadConflictAsync(
        StorageConnection connection, string bucket, IReadOnlyList<DownloadItem> items,
        string localDirectory, CancellationToken cancellationToken = default)
    {
        connection = await CurrentConnectionAsync(connection, cancellationToken);
        ObjectAccess.Ensure(connection, bucket, null, write: false);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var lease = await _remote.OpenAsync(connection, write: false, cancellationToken);
        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (item.Folder || item.Key.EndsWith('/'))
            {
                var prefix = item.Key.EndsWith('/') ? item.Key : item.Key + "/";
                await foreach (var listed in lease.ListAsync(bucket, prefix, cancellationToken))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (listed.Key.EndsWith('/') && listed.Size == 0)
                        continue;
                    var relative = listed.Key.StartsWith(prefix, StringComparison.Ordinal)
                        ? listed.Key[prefix.Length..] : listed.Key;
                    if (ExistingLocalName(localDirectory, relative, used) is { } name)
                        return name;
                }
            }
            else if (ExistingLocalName(localDirectory, item.Key, used) is { } name)
            {
                return name;
            }
        }

        return null;
    }

    private static string? ExistingLocalName(string root, string relative, ISet<string> used)
    {
        // The same Windows path mapping as QueueDownloadAsync.
        if (!TransferPaths.TryCombine(root, relative, used, out var path, out _))
            return null;
        return File.Exists(path) ? Path.GetFileName(path) : null;
    }

    public async Task<IReadOnlyList<TransferJob>> EnqueueUploadsAsync(
        StorageConnection connection, string bucket, string keyPrefix, IReadOnlyList<string> paths,
        TransferConflictPolicy conflict, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        connection = await CurrentConnectionAsync(connection, cancellationToken);
        ObjectAccess.Ensure(connection, bucket, null, write: true);
        var prefix = ObjectLayout.NormalizePrefix(ObjectAccess.CombinePrefix(connection, keyPrefix));
        var batch = Guid.NewGuid();
        var created = new List<TransferJob>();
        using var scanStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var sources = Channel.CreateBounded<UploadSource>(new BoundedChannelOptions(128)
        {
            SingleReader = true,
            SingleWriter = true
        });
        var scanner = Task.Run(async () =>
        {
            try
            {
                foreach (var source in UploadCatalog.Enumerate(prefix, paths))
                {
                    scanStop.Token.ThrowIfCancellationRequested();
                    await sources.Writer.WriteAsync(source, scanStop.Token);
                }
                sources.Writer.TryComplete();
            }
            catch (Exception exception)
            {
                sources.Writer.TryComplete(exception);
            }
        });
        try
        {
            await foreach (var source in sources.Reader.ReadAllAsync(cancellationToken))
            {
                var job = TransferJob.Create(
                    TransferKind.Upload, connection.Id, bucket, source.Key, source.LocalPath, source.Size,
                    conflict, batch, source.FolderMarker ? null : ContentTypes.FromPath(source.LocalPath), source.FolderMarker, _clock.UtcNow);
                if (source.Error is not null)
                {
                    job = job with
                    {
                        State = TransferState.Failed,
                        Failure = new TransferFailure(TransferFailureKind.InvalidPath, false, source.Error, "The file could not be queued.")
                    };
                }

                created.Add(await AcceptAsync(connection, job, cancellationToken));
            }
        }
        finally
        {
            scanStop.Cancel();
            await scanner;
        }

        return created;
    }

    public async Task<IReadOnlyList<TransferJob>> EnqueueDownloadsAsync(
        StorageConnection connection, string bucket, IReadOnlyList<DownloadItem> items, string localDirectory,
        TransferConflictPolicy conflict, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        connection = await CurrentConnectionAsync(connection, cancellationToken);
        ObjectAccess.Ensure(connection, bucket, null, write: false);
        Directory.CreateDirectory(localDirectory);
        var batch = Guid.NewGuid();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var created = new List<TransferJob>();
        await using var lease = await _remote.OpenAsync(connection, write: false, cancellationToken);
        foreach (var item in items)
        {
            if (item.Folder || item.Key.EndsWith('/'))
            {
                var prefix = item.Key.EndsWith('/') ? item.Key : item.Key + "/";
                await foreach (var listed in lease.ListAsync(bucket, prefix, cancellationToken))
                {
                    if (listed.Key.EndsWith('/') && listed.Size == 0)
                        continue;
                    var relative = listed.Key.StartsWith(prefix, StringComparison.Ordinal) ? listed.Key[prefix.Length..] : listed.Key;
                    created.Add(await QueueDownloadAsync(connection, bucket, listed.Key, relative, listed.Size, localDirectory, conflict, batch, used, cancellationToken));
                }
            }
            else
            {
                created.Add(await QueueDownloadAsync(connection, bucket, item.Key, item.Key, item.Size, localDirectory, conflict, batch, used, cancellationToken));
            }
        }

        return created;
    }

    public async Task<IReadOnlyList<TransferJob>> EnqueueCrossCopiesAsync(
        StorageConnection source, StorageConnection destination, string sourceBucket,
        IReadOnlyList<DownloadItem> items, string destinationBucket, string destinationPrefix,
        TransferConflictPolicy conflict, CancellationToken cancellationToken = default, bool move = false,
        Action<Guid>? batchCreated = null, bool exactTargets = false)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        source = await CurrentConnectionAsync(source, cancellationToken);
        destination = await CurrentConnectionAsync(destination, cancellationToken);
        ObjectAccess.Ensure(source, sourceBucket, null, write: move);
        ObjectAccess.Ensure(destination, destinationBucket, null, write: true);
        var prefix = CrossTransferPlanner.Prefix(destinationPrefix);
        if (exactTargets)
        {
            if (items.Count > _options.MaxCrossMoveManifestObjects)
                throw new Cavdeck.Application.Explorer.ObjectManifestLimitException();
            if (items.Any(item => item.Folder || string.IsNullOrEmpty(item.DestinationKey)) ||
                items.Select(item => item.Key).Distinct(StringComparer.Ordinal).Count() != items.Count ||
                items.Select(item => item.DestinationKey).Distinct(StringComparer.Ordinal).Count() != items.Count)
                throw new ArgumentException("The exact copy manifest must map distinct keys.", nameof(items));
            if (CrossTransferPlanner.SameBackend(source, destination) && sourceBucket == destinationBucket &&
                items.Select(item => item.Key).Intersect(items.Select(item => item.DestinationKey), StringComparer.Ordinal).Any())
                throw new ArgumentException("The copy manifest overlaps the source.", nameof(items));
            if (conflict == TransferConflictPolicy.Rename)
                items = await AllocateExactCrossDestinationsAsync(source, destination, sourceBucket,
                    destinationBucket, items, cancellationToken);
            foreach (var item in items)
            {
                if (!ExplorerPath.FitsKey(item.Key) || !ExplorerPath.FitsKey(item.DestinationKey!))
                    throw new ArgumentException("The copy manifest contains an invalid object key.", nameof(items));
                ObjectAccess.Ensure(source, sourceBucket, item.Key, write: move);
                ObjectAccess.Ensure(destination, destinationBucket, item.DestinationKey, write: true);
            }
        }
        else if (conflict == TransferConflictPolicy.Rename
            || !move && CrossTransferPlanner.SameBackend(source, destination)
                && sourceBucket == destinationBucket)
            items = await AllocateCrossDestinationsAsync(source, destination, sourceBucket,
                items, destinationBucket, prefix, conflict, move, cancellationToken);
        // Do not delete any source object while a folder is being paged.
        // Materialize a bounded, durable manifest before scheduling move jobs.
        if (move && !exactTargets)
            items = await SnapshotMoveItemsAsync(source, destination, sourceBucket, items,
                destinationBucket, prefix, cancellationToken);
        var batch = Guid.NewGuid();
        var created = new List<TransferJob>();
        var scan = new CrossTransferBatch(batch, source.Id, destination.Id, sourceBucket,
            destinationBucket, prefix, items.ToArray(), conflict, 0, null, null, 0,
            CrossBatchState.Scanning, null, _clock.UtcNow, move)
        {
            SourceConfiguration = CrossTransferPlanner.ConfigurationIdentity(source),
            DestinationConfiguration = CrossTransferPlanner.ConfigurationIdentity(destination),
            ExactTargets = exactTargets
        };
        await PublishBatchAsync(scan, cancellationToken);
        batchCreated?.Invoke(batch);
        await ScanBatchSafelyAsync(scan, created, cancellationToken);
        return created;
    }

    private async Task<IReadOnlyList<DownloadItem>> AllocateExactCrossDestinationsAsync(
        StorageConnection source, StorageConnection destination, string sourceBucket, string destinationBucket,
        IReadOnlyList<DownloadItem> items, CancellationToken cancellationToken)
    {
        var reserved = items.Select(item => item.DestinationKey!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (reserved.Count != items.Count)
            throw new ArgumentException("The destination names differ only by case.", nameof(items));
        if (CrossTransferPlanner.SameBackend(source, destination) && sourceBucket == destinationBucket)
            reserved.UnionWith(items.Select(item => item.Key));
        await using var lease = await _remote.OpenAsync(destination, write: true, cancellationToken);
        var selected = new List<DownloadItem>(items.Count);
        foreach (var item in items)
        {
            var original = item.DestinationKey!;
            var target = original;
            if (await lease.HeadAsync(destinationBucket, original, cancellationToken) is not null)
            {
                target = "";
                var marker = original.EndsWith('/');
                for (var index = 1; index <= _options.MaxRenameAttempts; index++)
                {
                    var candidate = ConflictNames.WithSuffix(marker ? original[..^1] : original, index);
                    if (marker)
                        candidate += "/";
                    if (!ExplorerPath.FitsKey(candidate) || reserved.Contains(candidate) ||
                        await lease.HeadAsync(destinationBucket, candidate, cancellationToken) is not null)
                        continue;
                    reserved.Add(candidate);
                    target = candidate;
                    break;
                }
            }
            if (target.Length == 0)
                throw new StorageException(new StorageFailure(StorageFailureKind.PreconditionFailed,
                    "NoFreeDestinationName", null, null, null));
            selected.Add(item with { DestinationKey = target });
        }
        return selected;
    }

    public async Task<string?> FindCrossCopyConflictAsync(
        StorageConnection source, StorageConnection destination, string sourceBucket,
        IReadOnlyList<DownloadItem> items, string destinationBucket,
        string destinationPrefix, CancellationToken cancellationToken = default)
    {
        source = await CurrentConnectionAsync(source, cancellationToken);
        destination = await CurrentConnectionAsync(destination, cancellationToken);
        var prefix = CrossTransferPlanner.Prefix(destinationPrefix);
        await using var lease = await _remote.OpenAsync(destination, write: false, cancellationToken);
        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var destinationKey = prefix + ObjectLayout.DisplayName(item.Key) + (item.Folder ? "/" : "");
            if (CrossTransferPlanner.SameBackend(source, destination)
                && sourceBucket == destinationBucket && item.Key == destinationKey)
                return ObjectLayout.DisplayName(item.Key);
            if (await CrossDestinationExistsAsync(lease, destinationBucket,
                destinationKey, item.Folder, cancellationToken))
                return ObjectLayout.DisplayName(item.Key);
        }
        return null;
    }

    private async Task<IReadOnlyList<DownloadItem>> AllocateCrossDestinationsAsync(
        StorageConnection source, StorageConnection destination, string sourceBucket,
        IReadOnlyList<DownloadItem> items, string destinationBucket,
        string destinationPrefix, TransferConflictPolicy conflict, bool move,
        CancellationToken cancellationToken)
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        var selected = new List<DownloadItem>(items.Count);
        await using var lease = await _remote.OpenAsync(destination, write: false, cancellationToken);
        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sameObject = CrossTransferPlanner.SameBackend(source, destination)
                && sourceBucket == destinationBucket;
            var chosen = "";
            for (var index = 1; index <= _options.MaxRenameAttempts + 1; index++)
            {
                var name = ObjectLayout.SiblingName(ObjectLayout.DisplayName(item.Key), index);
                if (!ObjectLayout.TryJoin(destinationPrefix, name, item.Folder,
                    out var candidate, out _))
                    throw new InvalidOperationException("The destination object key is too long.");
                var isSelf = sameObject && item.Key == candidate;
                if (!move && isSelf)
                    continue;

                // With Skip/Overwrite only a self-copy needs a distinct target.
                // Keep Both allocates a free *root*, preserving the folder layout.
                var rename = conflict == TransferConflictPolicy.Rename || isSelf;
                if (used.Contains(candidate))
                    continue;
                if (rename && await CrossDestinationExistsAsync(lease, destinationBucket,
                    candidate, item.Folder, cancellationToken))
                    continue;
                chosen = candidate;
                break;
            }
            if (chosen.Length == 0)
                throw new InvalidOperationException("No available destination name was found.");
            used.Add(chosen);
            selected.Add(item with { DestinationKey = chosen });
        }
        return selected;
    }

    private static async Task<bool> CrossDestinationExistsAsync(
        IRemoteTransferLease destination, string bucket, string key, bool folder,
        CancellationToken cancellationToken)
    {
        if (!folder)
            return await destination.HeadAsync(bucket, key, cancellationToken) is not null;
        var page = await destination.ListPageAsync(bucket, key, null, cancellationToken);
        return page.Objects.Any(item => item.Key.StartsWith(key, StringComparison.Ordinal));
    }

    private async Task<IReadOnlyList<DownloadItem>> SnapshotMoveItemsAsync(
        StorageConnection source, StorageConnection destination, string sourceBucket,
        IReadOnlyList<DownloadItem> selected, string destinationBucket,
        string destinationPrefix, CancellationToken cancellationToken)
    {
        var snapshot = new List<DownloadItem>();
        var targets = new Dictionary<string, string>(StringComparer.Ordinal);
        var sources = new Dictionary<string, string>(StringComparer.Ordinal);

        void Add(string sourceKey, long? size, string destinationKey)
        {
            if (System.Text.Encoding.UTF8.GetByteCount(destinationKey) > ObjectLayout.MaxKeyBytes)
                throw new ArgumentException("Destination object key is too long.", nameof(destinationPrefix));
            ObjectAccess.Ensure(source, sourceBucket, sourceKey, write: true);
            ObjectAccess.Ensure(destination, destinationBucket, destinationKey, write: true);
            if (CrossTransferPlanner.SameBackend(source, destination)
                && sourceBucket == destinationBucket && sourceKey == destinationKey)
                throw new InvalidOperationException("The source and destination refer to the same object.");
            if (sources.TryGetValue(sourceKey, out var earlierTarget)
                && !string.Equals(earlierTarget, destinationKey, StringComparison.Ordinal))
                throw new InvalidOperationException("One source object cannot be moved to multiple destinations.");
            if (targets.TryGetValue(destinationKey, out var previous))
            {
                if (!string.Equals(previous, sourceKey, StringComparison.Ordinal))
                    throw new InvalidOperationException("Different source objects have the same destination key.");
                return;
            }
            if (snapshot.Count >= _options.MaxCrossMoveManifestObjects)
                throw new InvalidOperationException("This folder move exceeds the safe object manifest limit.");
            sources.Add(sourceKey, destinationKey);
            targets.Add(destinationKey, sourceKey);
            snapshot.Add(new DownloadItem(sourceKey, false, size, destinationKey));
        }

        if (selected.All(item => !item.Folder))
        {
            foreach (var item in selected)
                Add(item.Key, item.Size, item.DestinationKey
                    ?? destinationPrefix + ObjectLayout.DisplayName(item.Key));
            return snapshot;
        }

        await using var lease = await _remote.OpenAsync(source, write: false, cancellationToken);
        foreach (var item in selected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = item.DestinationKey is null
                ? destinationPrefix + ObjectLayout.DisplayName(item.Key)
                : item.DestinationKey.TrimEnd('/');
            if (!item.Folder)
            {
                Add(item.Key, item.Size, target);
                continue;
            }

            var sourcePrefix = CrossTransferPlanner.Prefix(item.Key);
            var targetPrefix = target + "/";
            if (CrossTransferPlanner.SameBackend(source, destination)
                && sourceBucket == destinationBucket
                && targetPrefix.StartsWith(sourcePrefix, StringComparison.Ordinal))
                throw new InvalidOperationException("A folder cannot be moved into itself.");

            string? continuation = null;
            var seenTokens = new HashSet<string>(StringComparer.Ordinal);
            var found = false;
            do
            {
                var page = await lease.ListPageAsync(sourceBucket, sourcePrefix,
                    continuation, cancellationToken);
                foreach (var listed in page.Objects)
                {
                    if (!listed.Key.StartsWith(sourcePrefix, StringComparison.Ordinal))
                        throw new InvalidOperationException("A source listing returned an object outside the folder.");
                    found = true;
                    Add(listed.Key, listed.Size,
                        CrossTransferPlanner.MapKey(listed.Key, sourcePrefix, targetPrefix));
                }
                continuation = page.ContinuationToken;
                if (continuation is not null && !seenTokens.Add(continuation))
                    throw new InvalidOperationException("The source repeated a listing continuation token.");
            } while (continuation is not null);

            // S3 has no native empty directories. An empty folder marker is
            // a zero-byte object ending in '/', and is captured by ListPageAsync.
            if (!found)
                throw new InvalidOperationException("The selected source folder no longer exists.");
        }
        return snapshot;
    }

    public async Task RetryBatchAsync(Guid batchId, CancellationToken cancellationToken = default)
    {
        CrossTransferBatch? batch;
        lock (_gate)
            _batches.TryGetValue(batchId, out batch);
        if (batch is null)
            return;
        string? cursor = null;
        while (true)
        {
            var failed = await _journal.LoadFailedBatchJobsAsync(batchId, cursor, 128, cancellationToken);
            if (failed.Count == 0)
                break;
            foreach (var job in failed)
            {
                await WaitForCrossCapacityAsync(cancellationToken);
                lock (_gate)
                    _jobs.TryAdd(job.Id, job);
                await RetryAsync(job.Id, cancellationToken);
            }
            cursor = failed[^1].Id.ToString("N");
            if (failed.Count < 128)
                break;
        }
        if (batch.State != CrossBatchState.Failed)
            return;
        batch = batch with { State = CrossBatchState.Scanning, Error = null, UpdatedAt = _clock.UtcNow };
        await PublishBatchAsync(batch, cancellationToken);
        TrackBackground(Task.Run(() => ScanBatchSafelyAsync(batch, null, _stop.Token)));
    }

    private async Task PublishBatchAsync(CrossTransferBatch batch, CancellationToken cancellationToken)
    {
        await _journal.UpsertBatchAsync(batch, cancellationToken);
        lock (_gate)
            _batches[batch.Id] = batch;
        RaiseBatchChanged(batch);
    }

    private async Task ScanBatchSafelyAsync(CrossTransferBatch batch,
        List<TransferJob>? created, CancellationToken cancellationToken)
    {
        using var scan = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _scanControls[batch.Id] = scan;
        cancellationToken = scan.Token;
        var entered = false;
        try
        {
            await _batchScanner.WaitAsync(cancellationToken);
            entered = true;
            Interlocked.Increment(ref _activeBatchScans);
            await ScanBatchAsync(batch, created, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            CrossTransferBatch latest;
            lock (_gate)
                latest = _batches.GetValueOrDefault(batch.Id) ?? batch;
            await PublishBatchAsync(latest with
            {
                State = CrossBatchState.Failed,
                Error = nameof(OperationCanceledException),
                Retryable = false,
                UpdatedAt = _clock.UtcNow
            }, CancellationToken.None);
            throw;
        }
        catch (Exception exception)
        {
            CrossTransferBatch latest;
            lock (_gate)
                latest = _batches.TryGetValue(batch.Id, out var current) ? current : batch;
            await PublishBatchAsync(latest with
            {
                State = CrossBatchState.Failed,
                Error = exception is StorageException storage
                    ? storage.Failure.Kind.ToString()
                    : exception.GetType().Name,
                Retryable = exception is StorageException failure && TransferFailures.FromStorage(failure).Retryable,
                UpdatedAt = _clock.UtcNow
            }, CancellationToken.None);
            throw;
        }
        finally
        {
            _scanControls.TryRemove(batch.Id, out _);
            if (entered)
            {
                Interlocked.Decrement(ref _activeBatchScans);
                _batchScanner.Release();
            }
        }
    }

    private async Task ScanBatchAsync(CrossTransferBatch batch,
        List<TransferJob>? created, CancellationToken cancellationToken)
    {
        var source = await ResolveAsync(batch.SourceConnectionId, cancellationToken)
            ?? throw new InvalidOperationException("The source connection is no longer available.");
        var destination = await ResolveAsync(batch.DestinationConnectionId, cancellationToken)
            ?? throw new InvalidOperationException("The destination connection is no longer available.");
        if (!CrossTransferPlanner.MatchesConfiguration(batch.SourceConfiguration, source) ||
            !CrossTransferPlanner.MatchesConfiguration(batch.DestinationConfiguration, destination))
            throw new StorageException(new StorageFailure(StorageFailureKind.InvalidConfiguration,
                "ConnectionConfigurationChanged", null, null, null));
        ObjectAccess.Ensure(source, batch.SourceBucket, null, write: batch.Move);
        ObjectAccess.Ensure(destination, batch.DestinationBucket, null, write: true);
        await using var lease = await _remote.OpenAsync(source, write: false, cancellationToken);
        while (batch.ScanIndex < batch.Items.Count)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = batch.Items[batch.ScanIndex];
            var name = ObjectLayout.DisplayName(item.Key);
            if (item.Folder)
            {
                var sourcePrefix = CrossTransferPlanner.Prefix(item.Key);
                var targetPrefix = batch.Items[batch.ScanIndex].DestinationKey
                    ?? batch.DestinationPrefix + name + "/";
                if (CrossTransferPlanner.SameBackend(source, destination)
                    && batch.SourceBucket == batch.DestinationBucket
                    && targetPrefix.StartsWith(sourcePrefix, StringComparison.Ordinal))
                    throw new InvalidOperationException("A folder cannot be copied into itself.");
                var page = await lease.ListPageAsync(batch.SourceBucket, sourcePrefix,
                    batch.ContinuationToken, cancellationToken);
                if (page.ContinuationToken == batch.ContinuationToken && page.ContinuationToken is not null)
                    throw new InvalidOperationException("The provider repeated a listing continuation token.");
                foreach (var listed in page.Objects)
                {
                    if (!listed.Key.StartsWith(sourcePrefix, StringComparison.Ordinal))
                        throw new InvalidOperationException("The provider listed an object outside the requested folder.");
                    var targetKey = CrossTransferPlanner.MapKey(listed.Key, sourcePrefix, targetPrefix);
                    if (await QueueCrossCopyAsync(source, destination, batch.SourceBucket, listed.Key,
                        batch.DestinationBucket, targetKey, listed.Size, batch.Conflict,
                        batch.Id, created, cancellationToken, batch.Move, listed.ETag))
                        batch = batch with { ObjectsQueued = batch.ObjectsQueued + 1 };
                }
                batch = batch with
                {
                    ContinuationToken = page.ContinuationToken,
                    LastKey = page.Objects.LastOrDefault()?.Key,
                    ScanIndex = page.ContinuationToken is null ? batch.ScanIndex + 1 : batch.ScanIndex,
                    UpdatedAt = _clock.UtcNow
                };
            }
            else
            {
                if (await QueueCrossCopyAsync(source, destination, batch.SourceBucket, item.Key,
                    batch.DestinationBucket, item.DestinationKey ?? batch.DestinationPrefix + name, item.Size,
                    batch.ExactTargets && batch.Conflict == TransferConflictPolicy.Rename ? TransferConflictPolicy.Fail : batch.Conflict,
                    batch.Id, created, cancellationToken, batch.Move, item.SourceETag))
                    batch = batch with { ObjectsQueued = batch.ObjectsQueued + 1 };
                batch = batch with { ScanIndex = batch.ScanIndex + 1, ContinuationToken = null,
                    LastKey = item.Key, UpdatedAt = _clock.UtcNow };
            }
            await PublishBatchAsync(batch, cancellationToken);
        }
        await PublishBatchAsync(batch with
        {
            State = CrossBatchState.Completed,
            ObjectsQueued = (await _journal.GetBatchProgressAsync(batch.Id, cancellationToken)).Queued,
            UpdatedAt = _clock.UtcNow
        }, cancellationToken);
    }

    private async Task<bool> QueueCrossCopyAsync(StorageConnection source, StorageConnection destination,
        string sourceBucket, string sourceKey, string destinationBucket, string destinationKey,
        long? size, TransferConflictPolicy conflict, Guid batch,
        List<TransferJob>? created, CancellationToken cancellationToken, bool move, string? sourceETag)
    {
        if (System.Text.Encoding.UTF8.GetByteCount(destinationKey) > ObjectLayout.MaxKeyBytes)
            throw new ArgumentException("Destination object key is too long.", nameof(destinationKey));
        source = await CurrentConnectionAsync(source, cancellationToken);
        destination = await CurrentConnectionAsync(destination, cancellationToken);
        ObjectAccess.Ensure(source, sourceBucket, sourceKey, write: move);
        ObjectAccess.Ensure(destination, destinationBucket, destinationKey, write: true);
        // The parent root was already reserved for Keep Both. A late collision
        // must not silently scatter children into individually renamed paths.
        var jobConflict = conflict == TransferConflictPolicy.Rename
            ? TransferConflictPolicy.Skip : conflict;
        var job = TransferJob.Create(TransferKind.Copy, destination.Id, destinationBucket,
            destinationKey, "", size, jobConflict, batch,
            folderMarker: sourceKey.EndsWith('/'), createdAt: _clock.UtcNow) with
        {
            DisplayName = ObjectLayout.DisplayName(sourceKey),
            CrossTransfer = new CrossTransferSpec(
                new StorageObjectRef(source.Id, sourceBucket, sourceKey),
                new StorageObjectRef(destination.Id, destinationBucket, destinationKey), jobConflict,
                SourceConfiguration: CrossTransferPlanner.ConfigurationIdentity(source),
                DestinationConfiguration: CrossTransferPlanner.ConfigurationIdentity(destination), Move: move)
            { SourceETag = sourceETag }
        };
        await WaitForCrossCapacityAsync(cancellationToken);
        if (!await _journal.TryCreateCrossJobAsync(job, cancellationToken))
            return false;
        lock (_gate)
        {
            _jobs[job.Id] = job;
        }
        RaiseChanged(job);
        _ready.Writer.TryWrite(job.Id);
        if (created is { Count: < 128 })
            created.Add(job);
        return true;
    }

    private async Task WaitForCrossCapacityAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            Task wait;
            lock (_gate)
            {
                if (_jobs.Values.Count(job => job.Kind == TransferKind.Copy && job.IsOpen) < 128)
                    return;
                wait = _capacityChanged.Task;
            }
            await wait.WaitAsync(cancellationToken);
        }
    }

    private void SignalCrossCapacity()
    {
        TaskCompletionSource<bool> old;
        lock (_gate)
        {
            old = _capacityChanged;
            _capacityChanged = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        old.TrySetResult(true);
    }

    private void TrackBackground(Task task)
    {
        _running.TryAdd(task, 0);
        _ = task.ContinueWith(completed =>
            {
                if (completed.IsFaulted && completed.Exception is { } exception)
                    RaiseBackgroundFault(exception.Flatten());
                _running.TryRemove(completed, out _);
            },
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private void RaiseBackgroundFault(Exception exception)
    {
        var handlers = BackgroundFaulted;
        if (handlers is null)
            return;

        foreach (Action<Exception> handler in handlers.GetInvocationList())
        {
            try { handler(exception); }
            catch { /* Diagnostics subscribers must never destabilize a transfer worker. */ }
        }
    }

    private void RaiseChanged(TransferJob job)
    {
        var handlers = Changed;
        if (handlers is null)
            return;

        foreach (Action<TransferJob> handler in handlers.GetInvocationList())
        {
            try { handler(job); }
            catch (Exception exception) { RaiseBackgroundFault(exception); }
        }
    }

    private void RaiseBatchChanged(CrossTransferBatch batch)
    {
        var handlers = BatchChanged;
        if (handlers is null)
            return;

        foreach (Action<CrossTransferBatch> handler in handlers.GetInvocationList())
        {
            try { handler(batch); }
            catch (Exception exception) { RaiseBackgroundFault(exception); }
        }
    }

    private void RaiseRemoved(Guid id)
    {
        var handlers = Removed;
        if (handlers is null)
            return;

        foreach (Action<Guid> handler in handlers.GetInvocationList())
        {
            try { handler(id); }
            catch (Exception exception) { RaiseBackgroundFault(exception); }
        }
    }

    private void RaiseBatchRemoved(Guid id)
    {
        var handlers = BatchRemoved;
        if (handlers is null)
            return;

        foreach (Action<Guid> handler in handlers.GetInvocationList())
        {
            try { handler(id); }
            catch (Exception exception) { RaiseBackgroundFault(exception); }
        }
    }

    private async Task RecoverBackgroundFailureAsync(
        Guid id, int attempt, string code, string message)
    {
        await _persist.WaitAsync(CancellationToken.None);
        try
        {
            TransferJob current;
            lock (_gate)
            {
                if (!_jobs.TryGetValue(id, out current!)
                    || current.Attempt != attempt
                    || current.State is not (TransferState.Running or TransferState.WaitingRetry or TransferState.Queued))
                    return;
            }

            var failed = Fail(current, new TransferFailure(
                TransferFailureKind.Unknown, false, code, message));

            try
            {
                await _journal.UpsertAsync(failed, CancellationToken.None);
            }
            catch (Exception persistenceException)
            {
                // Keep the in-memory UI state actionable even if the journal is
                // unavailable. On restart, a still-Running journal record is
                // normalized back to Queued by StartAsync.
                RaiseBackgroundFault(persistenceException);
            }

            lock (_gate)
            {
                if (!_jobs.TryGetValue(id, out var latest)
                    || latest.Attempt != attempt
                    || latest.State != current.State)
                    return;
                _jobs[id] = failed;
            }

            RaiseChanged(failed);
            if (failed.Kind == TransferKind.Copy && !failed.IsOpen)
                SignalCrossCapacity();
            PruneTerminalHistory();
        }
        finally
        {
            _persist.Release();
        }
    }

    public Task PauseAsync(Guid id, CancellationToken cancellationToken = default) =>
        TransitionAsync(id, job => job.State is TransferState.Queued or TransferState.Running or TransferState.WaitingRetry
            ? job with { State = TransferState.Paused, NextAttemptAt = null }
            : null, cancel: true, cancellationToken);

    public Task ResumeAsync(Guid id, CancellationToken cancellationToken = default) =>
        TransitionAsync(id, job => job.State is TransferState.Paused or TransferState.Failed
            ? job with { State = TransferState.Queued, Failure = null, Attempt = job.State == TransferState.Failed ? 0 : job.Attempt }
            : null, cancel: false, cancellationToken);

    public async Task CancelAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await TransitionAsync(id, job => job.IsOpen
            ? job with { State = TransferState.Cancelled, Failure = null, NextAttemptAt = null }
            : null, cancel: true, cancellationToken);
        if (CurrentOrNull(id) is not { State: TransferState.Cancelled } cancelled)
            return;
        if (cancelled is { Kind: TransferKind.Copy, UploadId: not null, CrossTransfer: not null })
        {
            try
            {
                var destination = await ResolveAsync(cancelled.ConnectionId, cancellationToken);
                if (destination is null)
                    throw new InvalidOperationException("The destination connection is no longer available for multipart cleanup.");
                await using var lease = await _remote.OpenAsync(destination, write: true, cancellationToken);
                await lease.AbortMultipartAsync(cancelled.CrossTransfer.Destination.Bucket,
                    cancelled.CrossTransfer.Destination.Key, cancelled.UploadId, cancellationToken);
            }
            catch (StorageException error) when (error.Failure.ErrorCode == "NoSuchUpload")
            {
                // Completion may have won the race with cancellation. Never delete the target.
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                await TransitionAsync(id, job => job.State == TransferState.Cancelled
                    ? job with { CrossTransfer = job.CrossTransfer! with { CleanupPending = true },
                        Failure = new TransferFailure(TransferFailureKind.Unknown, false,
                        "AbortFailed", "The multipart session could not be cleaned up: " + error.GetType().Name) }
                    : null, cancel: false, cancellationToken);
                return;
            }
            await TransitionAsync(id, job => job.State == TransferState.Cancelled
                ? job with { UploadId = null, CrossTransfer = job.CrossTransfer! with { CleanupPending = false } }
                : null, cancel: false, cancellationToken);
        }
        await _journal.ClearPartsAsync(id, cancellationToken);
        if (cancelled.Kind == TransferKind.Download)
        {
            // Paused downloads have no active coordinator to remove their
            // (potentially preallocated) temporary file. Active downloads
            // also clean up when cancellation releases the file handle.
            try
            {
                TransferFiles.DeleteIfExists(TransferPaths.Partial(cancelled.LocalPath, id));
                TransferFiles.DeleteIfExists(TransferPaths.Commit(cancelled.LocalPath, id));
            }
            catch (IOException)
            {
                // A running download may still have its temporary file open.
                // Its cancellation handler performs the same cleanup.
            }
        }
        PruneTerminalHistory();
    }

    private async Task RetryMultipartCleanupAsync(TransferJob pending, CancellationToken cancellationToken)
    {
        if (pending.UploadId is null || pending.CrossTransfer is null)
            return;
        try
        {
            var connection = await ResolveAsync(pending.ConnectionId, cancellationToken);
            if (connection is null)
                return;
            await using var lease = await _remote.OpenAsync(connection, write: true, cancellationToken);
            try
            {
                await lease.AbortMultipartAsync(pending.CrossTransfer.Destination.Bucket,
                    pending.CrossTransfer.Destination.Key, pending.UploadId, cancellationToken);
            }
            catch (StorageException error) when (error.Failure.ErrorCode == "NoSuchUpload")
            {
                // The upload was already completed or removed. Never remove a final object.
            }
            await _journal.ClearPartsAsync(pending.Id, cancellationToken);
            await TransitionAsync(pending.Id, job => job.CrossTransfer?.CleanupPending == true
                ? job with { UploadId = null,
                    CrossTransfer = job.CrossTransfer with { CleanupPending = false } }
                : null, cancel: false, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            // Keep CleanupPending in the journal for a later launch, but do
            // not silently discard the failure that prevented cleanup.
            RaiseBackgroundFault(exception);
        }
    }

    public Task RetryAsync(Guid id, CancellationToken cancellationToken = default) => ResumeAsync(id, cancellationToken);

    public Task<TransferHistoryRemoval> RemoveTerminalJobAsync(Guid id, CancellationToken cancellationToken = default) =>
        RemoveHistoryAsync(TransferHistoryScope.Single, id, cancellationToken);

    public Task<TransferHistoryRemoval> ClearFailedAsync(CancellationToken cancellationToken = default) =>
        RemoveHistoryAsync(TransferHistoryScope.Failed, null, cancellationToken);

    public Task<TransferHistoryRemoval> ClearFinishedAsync(CancellationToken cancellationToken = default) =>
        RemoveHistoryAsync(TransferHistoryScope.Finished, null, cancellationToken);

    private async Task<TransferHistoryRemoval> RemoveHistoryAsync(
        TransferHistoryScope scope, Guid? id, CancellationToken cancellationToken)
    {
        await _persist.WaitAsync(cancellationToken);
        try
        {
            // Persist first: a failed disk transaction must leave the UI and in-memory queue intact.
            // The journal rechecks terminal state, active scans and pending multipart cleanup.
            var removal = await _journal.RemoveTerminalAsync(scope, id, cancellationToken);
            lock (_gate)
            {
                foreach (var removedId in removal.JobIds)
                    _jobs.Remove(removedId);
                foreach (var removedId in removal.BatchIds)
                    _batches.Remove(removedId);
            }
            foreach (var removedId in removal.JobIds)
                RaiseRemoved(removedId);
            foreach (var removedId in removal.BatchIds)
                RaiseBatchRemoved(removedId);
            return removal;
        }
        finally
        {
            _persist.Release();
        }
    }

    public TransferJob Current(Guid id)
    {
        lock (_gate)
            return _jobs[id];
    }

    public async Task PublishAsync(TransferJob job, CancellationToken cancellationToken)
    {
        await _persist.WaitAsync(cancellationToken);
        try
        {
            lock (_gate)
            {
                if (!_jobs.TryGetValue(job.Id, out var current)
                    || current.State != TransferState.Running
                    || current.Attempt != job.Attempt)
                    return;
            }

            // Persist first. A journal failure must not publish a state transition
            // to the in-memory model that the durable journal has not accepted.
            await _journal.UpsertAsync(job, cancellationToken);

            TransferJob published = job;
            lock (_gate)
            {
                if (!_jobs.TryGetValue(job.Id, out var current)
                    || current.State != TransferState.Running
                    || current.Attempt != job.Attempt)
                    return;

                // Progress notifications are transient and may arrive while the
                // journal write is in flight. Preserve the latest UI progress for
                // Running updates, but use the coordinator's durable checkpoint
                // when transitioning out of Running.
                if (job.State == TransferState.Running)
                    published = job with { BytesTransferred = current.BytesTransferred };
                _jobs[job.Id] = published;
            }

            RaiseChanged(published);
            if (published.Kind == TransferKind.Copy && !published.IsOpen)
                SignalCrossCapacity();
        }
        finally
        {
            _persist.Release();
        }
    }

    public void ReportProgress(TransferJob job) => UpdateProgress(job, allowDecrease: false);

    public void ResetProgress(TransferJob job) => UpdateProgress(job, allowDecrease: true);

    private void UpdateProgress(TransferJob job, bool allowDecrease)
    {
        TransferJob updated;
        lock (_gate)
        {
            if (!_jobs.TryGetValue(job.Id, out var current)
                || current.State != TransferState.Running
                || current.Attempt != job.Attempt)
                return;

            var bytes = ClampProgress(job.BytesTransferred, current.TotalBytes ?? job.TotalBytes);
            if (!allowDecrease)
                bytes = Math.Max(bytes, current.BytesTransferred);
            if (bytes == current.BytesTransferred)
                return;

            updated = current with { BytesTransferred = bytes };
            _jobs[job.Id] = updated;
        }

        RaiseChanged(updated);
    }

    public async Task PersistCheckpointAsync(TransferJob job, CancellationToken cancellationToken)
    {
        await _persist.WaitAsync(cancellationToken);
        try
        {
            lock (_gate)
            {
                if (!_jobs.TryGetValue(job.Id, out var current)
                    || current.State != TransferState.Running
                    || current.Attempt != job.Attempt)
                    return;
            }

            var checkpoint = job with
            {
                BytesTransferred = ClampProgress(job.BytesTransferred, job.TotalBytes)
            };
            await _journal.UpsertAsync(checkpoint, cancellationToken);
        }
        finally
        {
            _persist.Release();
        }
    }

    private static long ClampProgress(long bytes, long? total)
    {
        var clamped = Math.Max(0, bytes);
        return total is long maximum ? Math.Min(clamped, Math.Max(0, maximum)) : clamped;
    }

    public Task<IReadOnlyList<TransferPart>> GetPartsAsync(Guid id, CancellationToken cancellationToken) =>
        _journal.GetPartsAsync(id, cancellationToken);

    public Task SavePartAsync(Guid id, TransferPart part, CancellationToken cancellationToken) =>
        _journal.UpsertPartAsync(id, part, cancellationToken);

    public Task ClearPartsAsync(Guid id, CancellationToken cancellationToken) =>
        _journal.ClearPartsAsync(id, cancellationToken);

    // Entries in the concurrent control dictionaries can finish and dispose their
    // token sources between enumeration/lookup and Cancel(). A disposed source
    // already belongs to a completed worker and needs no further cancellation.
    private static void CancelIfActive(CancellationTokenSource control)
    {
        try
        {
            control.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The worker completed its cleanup concurrently.
        }
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;
        _stopping = true;
        _stop.Cancel();
        _ready.Writer.TryComplete();
        foreach (var control in _controls.Values)
            CancelIfActive(control);
        if (_pump is not null)
        {
            try
            {
                // Dispose() is called synchronously by the WPF service container on exit.
                // Never resume this await on the blocked UI synchronization context.
                if (_options.IsolatedRun)
                {
                    try { await _pump.ConfigureAwait(false); }
                    catch (Exception exception) { RaiseBackgroundFault(exception); }
                    await Task.WhenAll(_running.Keys).ConfigureAwait(false);
                }
                else
                {
                    await _pump.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                    await Task.WhenAll(_running.Keys).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                }
            }
            catch (Exception exception)
            {
                if (_options.IsolatedRun)
                    RaiseBackgroundFault(exception);
            }
        }
    }

    public Task EnqueueExactUploadsAsync(StorageConnection connection, string bucket, string key,
        string localPath, bool directory, TransferConflictPolicy conflict, CancellationToken cancellationToken)
    {
        var sources = directory
            ? UploadCatalog.Enumerate(key, [localPath], exact: true, cancellationToken)
            : [new UploadSource(localPath, key, null, false, null)];
        return EnqueueExactUploadsCoreAsync(connection, bucket, sources, conflict, cancellationToken);
    }

    private async Task EnqueueExactUploadsCoreAsync(StorageConnection connection, string bucket,
        IEnumerable<UploadSource> sources, TransferConflictPolicy conflict, CancellationToken cancellationToken)
    {
        await SetIsolatedConnectionAsync(connection, bucket, write: true, cancellationToken);
        var batch = Guid.NewGuid();
        foreach (var source in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var job = TransferJob.Create(TransferKind.Upload, connection.Id, bucket, source.Key,
                source.LocalPath, source.Size, conflict, batch,
                source.FolderMarker ? null : ContentTypes.FromPath(source.LocalPath), source.FolderMarker, _clock.UtcNow)
                with { ConnectionConfiguration = CrossTransferPlanner.ConfigurationIdentity(connection) };
            var error = source.Error;
            if (source.Key.Length == 0 || source.Key.Any(char.IsControl) ||
                System.Text.Encoding.UTF8.GetByteCount(source.Key) > ObjectLayout.MaxKeyBytes)
                error ??= "InvalidKey";
            if (error is not null)
            {
                job = job with { State = TransferState.Failed, Failure = new TransferFailure(
                    TransferFailureKind.InvalidPath, false, error, "The upload source could not be queued.") };
            }
            await AcceptAsync(connection, job, cancellationToken);
        }
    }

    public async Task EnqueueExactDownloadsAsync(StorageConnection connection, string bucket,
        IAsyncEnumerable<DownloadItem> items, string localDirectory, TransferConflictPolicy conflict,
        CancellationToken cancellationToken)
    {
        await SetIsolatedConnectionAsync(connection, bucket, write: false, cancellationToken);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var batch = Guid.NewGuid();
        await foreach (var item in items.WithCancellation(cancellationToken))
        {
            var relative = item.DestinationKey ?? "";
            var path = localDirectory;
            string? error = null;
            if (!ExactLocalPaths.IsValidRelative(relative))
                error = "Invalid";
            else
            {
                path = Path.Combine(localDirectory, relative.Replace('/', Path.DirectorySeparatorChar));
                if (!used.Add(path))
                    error = "Collision";
            }

            var job = TransferJob.Create(TransferKind.Download, connection.Id, bucket, item.Key,
                path, item.Size, conflict, batch, null, false, _clock.UtcNow)
                with { ConnectionConfiguration = CrossTransferPlanner.ConfigurationIdentity(connection) };
            if (error is not null)
            {
                job = job with { State = TransferState.Failed, Failure = new TransferFailure(
                    TransferFailureKind.InvalidPath, false, error, "The download path could not be queued.") };
            }
            await AcceptAsync(connection, job, cancellationToken);
        }
    }

    private async Task SetIsolatedConnectionAsync(StorageConnection connection, string bucket, bool write,
        CancellationToken cancellationToken)
    {
        if (!_options.IsolatedRun || _isolatedConnection is not null)
            throw new InvalidOperationException("An isolated engine accepts one transfer batch.");
        var current = await CurrentConnectionAsync(connection, cancellationToken);
        ObjectAccess.Ensure(current, bucket, null, write);
        _isolatedConnection = current;
    }

    // Stop the pump and join all workers before cleaning up or disposing their dependencies.
    public async Task<IReadOnlyList<TransferJob>> FinishIsolatedRunAsync(bool preserveRetryableState = false)
    {
        if (!_options.IsolatedRun)
            throw new InvalidOperationException("The transfer engine is not isolated.");

        await DisposeAsync().ConfigureAwait(false);
        foreach (var initial in Jobs)
        {
            var job = initial;
            if (job.IsOpen)
            {
                await TransitionAsync(job.Id, current => current with
                {
                    State = TransferState.Cancelled, Failure = null, NextAttemptAt = null
                }, cancel: false, CancellationToken.None).ConfigureAwait(false);
                job = CurrentOr(job.Id, job);
            }

            if (job.State != TransferState.Completed &&
                !(preserveRetryableState && job.State == TransferState.Failed && job.Failure?.Retryable == true))
                await CleanupIsolatedJobAsync(job).ConfigureAwait(false);
        }
        return Jobs;
    }

    private async Task CleanupIsolatedJobAsync(TransferJob job)
    {
        try
        {
            if (job.UploadId is { } uploadId && job.Kind is TransferKind.Upload or TransferKind.Copy)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                var connection = _isolatedConnection is { } snapshot
                    ? await CurrentConnectionAsync(snapshot, timeout.Token)
                    : await ResolveAsync(job.ConnectionId, timeout.Token);
                if (connection is null || !CrossTransferPlanner.MatchesConfiguration(
                        job.CrossTransfer?.DestinationConfiguration ?? job.ConnectionConfiguration, connection))
                    throw new IOException("The connection for multipart cleanup changed.");
                await using var lease = await _remote.OpenAsync(connection, write: true, timeout.Token);
                try { await lease.AbortMultipartAsync(job.Bucket, job.Key, uploadId, timeout.Token); }
                catch (StorageException exception) when (exception.Failure.ErrorCode == "NoSuchUpload") { }
                await TransitionAsync(job.Id, current => current with { UploadId = null },
                    cancel: false, CancellationToken.None);
            }
            if (job.Kind == TransferKind.Download && job.Remote is not null)
            {
                var partial = TransferPaths.Partial(job.LocalPath, job.Id, isolated: true);
                var commit = TransferPaths.Commit(job.LocalPath, job.Id, isolated: true);
                using var pin = ExactLocalPaths.Pin(job.LocalPath, includeLeaf: false);
                TransferFiles.DeleteIfExists(partial);
                TransferFiles.DeleteIfExists(commit);
            }
            await _journal.ClearPartsAsync(job.Id, CancellationToken.None);
        }
        catch (Exception exception)
        {
            await TransitionAsync(job.Id, current => current with
            {
                Failure = current.Failure is { } failure
                    ? failure with { CleanupPending = true }
                    : new TransferFailure(TransferFailureKind.Unknown, false, "CleanupPending",
                        null) { CleanupPending = true }
            }, cancel: false, CancellationToken.None);
            RaiseBackgroundFault(new IOException("Transfer cleanup could not be confirmed.", exception));
        }
    }

    private async Task<TransferJob> QueueDownloadAsync(
        StorageConnection connection, string bucket, string key, string relative, long? size, string root,
        TransferConflictPolicy conflict, Guid batch, ISet<string> used, CancellationToken cancellationToken)
    {
        var job = TransferJob.Create(
            TransferKind.Download, connection.Id, bucket, key, root, size, conflict, batch, null, false, _clock.UtcNow);
        if (!TransferPaths.TryCombine(root, relative, used, out var path, out var error))
        {
            job = job with
            {
                State = TransferState.Failed,
                Failure = new TransferFailure(TransferFailureKind.InvalidPath, false, error, "The download path is not valid.")
            };
        }
        else
        {
            job = job with { LocalPath = path, DisplayName = Path.GetFileName(path) };
        }

        return await AcceptAsync(connection, job, cancellationToken);
    }

    private async Task<TransferJob> AcceptAsync(StorageConnection connection, TransferJob job, CancellationToken cancellationToken)
    {
        connection = await CurrentConnectionAsync(connection, cancellationToken);
        ObjectAccess.Ensure(connection, job.Bucket, job.Key, write: job.Kind != TransferKind.Download);

        // Do not expose a queued task until its initial journal record is durable.
        await _journal.UpsertAsync(job, cancellationToken);
        lock (_gate)
            _jobs[job.Id] = job;

        RaiseChanged(job);
        if (job.State == TransferState.Queued)
            _ready.Writer.TryWrite(job.Id);
        return job;
    }

    private async Task TransitionAsync(
        Guid id, Func<TransferJob, TransferJob?> change, bool cancel, CancellationToken cancellationToken)
    {
        await _persist.WaitAsync(cancellationToken);
        try
        {
            TransferJob current;
            TransferJob? changed;
            lock (_gate)
            {
                if (!_jobs.TryGetValue(id, out var job))
                    return;
                current = job;
                changed = change(job);
                if (changed is null)
                    return;
            }

            var updated = changed!;
            if (current.State == TransferState.Running
                && updated.State != TransferState.Running
                && (current.Kind == TransferKind.Upload
                    || current.Kind == TransferKind.Download && current.Resume == ResumeMode.ParallelRange))
            {
                // Transient UI progress is never a durable checkpoint.
                long committedBytes;
                try
                {
                    committedBytes = current.Kind == TransferKind.Upload
                        ? await GetCommittedUploadBytesAsync(current, cancellationToken)
                        : await GetCommittedDownloadBytesAsync(current, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception)
                {
                    committedBytes = 0;
                }
                updated = updated with { BytesTransferred = committedBytes };
            }

            updated = updated with { UpdatedAt = _clock.UtcNow };
            lock (_gate)
            {
                if (!_jobs.TryGetValue(id, out var latest)
                    || latest.State != current.State
                    || latest.Attempt != current.Attempt)
                    return;
            }

            // Commit the journal record before changing the in-memory state or
            // cancelling the active request. A failed write leaves the previous
            // state intact and lets the caller retry the transition.
            await _journal.UpsertAsync(updated, cancellationToken);

            lock (_gate)
            {
                if (!_jobs.TryGetValue(id, out var latest)
                    || latest.State != current.State
                    || latest.Attempt != current.Attempt)
                    return;
                _jobs[id] = updated;
            }

            if (cancel && _controls.TryGetValue(id, out var control))
                CancelIfActive(control);
            RaiseChanged(updated);
            if (updated.Kind == TransferKind.Copy && !updated.IsOpen)
                SignalCrossCapacity();
            if (updated.State == TransferState.Queued)
                _ready.Writer.TryWrite(id);
        }
        finally
        {
            _persist.Release();
        }
    }

    private Task<long> GetCommittedUploadBytesAsync(
        TransferJob job, CancellationToken cancellationToken) =>
        string.IsNullOrEmpty(job.UploadId)
            ? Task.FromResult(0L)
            : GetCommittedPartBytesAsync(job, cancellationToken);

    private Task<long> GetCommittedDownloadBytesAsync(
        TransferJob job, CancellationToken cancellationToken) =>
        !File.Exists(TransferPaths.Partial(job.LocalPath, job.Id, _options.IsolatedRun))
            ? Task.FromResult(0L)
            : GetCommittedPartBytesAsync(job, cancellationToken);

    private async Task<long> GetCommittedPartBytesAsync(
        TransferJob job, CancellationToken cancellationToken)
    {
        var limit = job.TotalBytes ?? long.MaxValue;
        var total = 0L;
        foreach (var part in await _journal.GetPartsAsync(job.Id, cancellationToken))
        {
            var remaining = limit - total;
            if (remaining <= 0)
                break;
            total += Math.Min(Math.Max(0, part.Length), remaining);
        }

        return total;
    }

    private async Task PumpAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var id in _ready.Reader.ReadAllAsync(cancellationToken))
            {
                await _slots.AcquireAsync(cancellationToken);
                Task task;
                try
                {
                    task = Task.Run(() => RunAsync(id));
                }
                catch
                {
                    // A slot is acquired before scheduling the worker. Return it
                    // if scheduling itself fails, otherwise the pump can stall.
                    _slots.Release();
                    throw;
                }
                TrackBackground(task);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task RunAsync(Guid id)
    {
        try
        {
            await RunCoreAsync(id);
        }
        catch (Exception exception)
        {
            var current = CurrentOrNull(id);
            if (current is not null)
            {
                try
                {
                    await RecoverBackgroundFailureAsync(
                        id, current.Attempt, "WorkerFault",
                        "The transfer worker stopped unexpectedly. Retry the operation from Transfers.");
                }
                catch (Exception recoveryException)
                {
                    throw new AggregateException(
                        "The transfer worker failed and its state could not be recovered.",
                        exception, recoveryException);
                }
            }

            // TrackBackground observes and reports the original worker exception.
            throw;
        }
        finally
        {
            _slots.Release();
        }
    }

    private async Task RunCoreAsync(Guid id)
    {
        TransferJob job;
        lock (_gate)
        {
            if (!_jobs.TryGetValue(id, out job!))
                return;
            if (job.State is not (TransferState.Queued or TransferState.WaitingRetry))
                return;
            job = job with
            {
                State = TransferState.Running,
                Attempt = job.Attempt + 1,
                Failure = null,
                NextAttemptAt = null,
                UpdatedAt = _clock.UtcNow
            };
            _jobs[id] = job;
        }

        var control = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        _controls[id] = control;
        // Cancellation may arrive between the state transition above and the
        // token registration. The state check closes that window.
        var activeBeforePublish = CurrentOrNull(id);
        if (activeBeforePublish is null || activeBeforePublish.State != TransferState.Running
            || activeBeforePublish.Attempt != job.Attempt)
            CancelIfActive(control);

        try
        {
            await PublishAsync(job, CancellationToken.None);

            var current = CurrentOrNull(id);
            if (current is null || current.State != TransferState.Running || current.Attempt != job.Attempt)
            {
                if (IsExpectedCancellation(id, control))
                    await HandleCancelAsync(id);
                return;
            }

            control.Token.ThrowIfCancellationRequested();

            var connection = _isolatedConnection is { } snapshot
                ? await CurrentConnectionAsync(snapshot, control.Token)
                : await ResolveAsync(job.ConnectionId, control.Token);
            if (connection is null)
            {
                await PublishAsync(Fail(job, new TransferFailure(
                    TransferFailureKind.ConnectionMissing, false, null, "The connection is no longer available.")), CancellationToken.None);
                return;
            }

            ObjectAccess.Ensure(connection, job.Bucket, job.Key, write: job.Kind != TransferKind.Download);
            if (_options.IsolatedRun && !CrossTransferPlanner.MatchesConfiguration(job.ConnectionConfiguration, connection))
            {
                await PublishAsync(Fail(job, new TransferFailure(TransferFailureKind.ConnectionMissing,
                    false, "ConnectionConfigurationChanged", null)), CancellationToken.None);
                return;
            }
            TransferJob result;
            if (job.Kind == TransferKind.Copy && job.CrossTransfer is { } cross)
            {
                var sourceConnection = await ResolveAsync(cross.Source.ConnectionId, control.Token);
                result = sourceConnection is null
                    ? Fail(job, new TransferFailure(TransferFailureKind.ConnectionMissing, false, null,
                        "The source connection is no longer available."))
                    : await _crossCopies.RunAsync(job, sourceConnection, connection, control.Token);
            }
            else
            {
                var write = job.Kind == TransferKind.Upload;
                await using var lease = await _remote.OpenAsync(connection, write, control.Token);
                result = job.Kind switch
                {
                    TransferKind.Upload => await _uploads.RunAsync(job, lease, control.Token),
                    TransferKind.Download => await _downloads.RunAsync(job, lease, control.Token),
                    _ => Fail(job, new TransferFailure(TransferFailureKind.Unsupported, false, null, "This transfer is not available."))
                };
            }
            await SettleAsync(result);
        }
        catch (Exception exception) when (IsCancellationException(exception))
        {
            if (IsExpectedCancellation(id, control))
            {
                await HandleCancelAsync(id);
            }
            else
            {
                // Some SDKs surface request timeouts as TaskCanceledException
                // even though the caller's token was never cancelled. Treat
                // those as transient transport failures, not user cancellation.
                var failure = new TransferFailure(
                    TransferFailureKind.Network, true, "UnexpectedCancellation",
                    "The storage operation ended unexpectedly before the transfer completed.");
                await SettleAsync(Fail(CurrentOr(id, job), failure));
            }
        }
        catch (StorageException exception)
        {
            await SettleAsync(Fail(CurrentOr(id, job), TransferFailures.FromStorage(exception, _options.IsolatedRun)));
        }
        catch (Exception exception)
        {
            await SettleAsync(Fail(CurrentOr(id, job), TransferFailures.FromLocal(exception)));
        }
        finally
        {
            if (_controls.TryRemove(id, out var removed))
                removed.Dispose();
        }
    }

    private bool IsExpectedCancellation(Guid id, CancellationTokenSource control)
    {
        if (_stopping || control.IsCancellationRequested)
            return true;

        return CurrentOrNull(id)?.State is TransferState.Paused or TransferState.Cancelled;
    }

    private static bool IsCancellationException(Exception exception)
    {
        if (exception is OperationCanceledException)
            return true;
        if (exception is not AggregateException aggregate)
            return false;

        var inner = aggregate.Flatten().InnerExceptions;
        return inner.Count > 0 && inner.All(item => item is OperationCanceledException);
    }

    private async Task SettleAsync(TransferJob result)
    {
        if (_options.IsolatedRun && result.Failure is { } raw)
            result = result with { Failure = raw with { Detail = null } };

        if (result.State == TransferState.Failed
            && result.Failure is { Retryable: true } failure
            && RetrySchedule.ShouldRetry(failure, result.Attempt, _options.MaxAttempts))
        {
            var delay = RetrySchedule.Delay(result.Attempt) + TimeSpan.FromMilliseconds(Random.Shared.Next(0, 250));
            var waiting = result with
            {
                State = TransferState.WaitingRetry,
                NextAttemptAt = _clock.UtcNow + delay,
                UpdatedAt = _clock.UtcNow
            };
            await PublishAsync(waiting, CancellationToken.None);
            TrackBackground(DelayAndRequeueAsync(waiting, delay));
            return;
        }

        await PublishAsync(result, CancellationToken.None);
        if (result.State == TransferState.Completed && CurrentOrNull(result.Id)?.State == TransferState.Completed)
            await _journal.ClearPartsAsync(result.Id, CancellationToken.None);
        if (result.State is TransferState.Completed or TransferState.Failed or TransferState.Cancelled)
            PruneTerminalHistory();
    }

    private void PruneTerminalHistory()
    {
        if (_options.IsolatedRun)
            return;
        if (Interlocked.Increment(ref _terminalSincePrune) < 100)
            return;
        Interlocked.Exchange(ref _terminalSincePrune, 0);
        List<Guid> removed;
        lock (_gate)
        {
            if (_jobs.Count <= 2000)
                return;
            var terminal = _jobs.Values
                .Where(job => job.State is TransferState.Completed or TransferState.Failed or TransferState.Cancelled)
                .OrderByDescending(job => job.UpdatedAt).ThenByDescending(job => job.Id)
                .Skip(2000).Select(job => job.Id).ToList();
            foreach (var id in terminal)
                _jobs.Remove(id);
            removed = terminal;
        }
        foreach (var id in removed)
            RaiseRemoved(id);
    }

    private async Task DelayAndRequeueAsync(TransferJob job, TimeSpan delay)
    {
        try
        {
            await _clock.DelayAsync(delay, _stop.Token);
            await TransitionAsync(job.Id, current =>
                current.State == TransferState.WaitingRetry && current.Attempt == job.Attempt
                    ? current with { State = TransferState.Queued, NextAttemptAt = null }
                    : null, cancel: false, CancellationToken.None);
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
            // The next process start converts WaitingRetry back to Queued.
        }
        catch (Exception)
        {
            await RecoverBackgroundFailureAsync(
                job.Id, job.Attempt, "RetryScheduleFailed",
                "The retry could not be scheduled. Retry the operation manually from Transfers.");
            throw;
        }
    }

    private async Task HandleCancelAsync(Guid id)
    {
        await _persist.WaitAsync(CancellationToken.None);
        try
        {
            TransferJob current;
            lock (_gate)
            {
                if (!_jobs.TryGetValue(id, out current!))
                    return;

                // Pause and Cancel transitions are already durable before their
                // cancellation token is signalled. Only application shutdown
                // needs the worker to convert Running back to Queued.
                if (!_stopping || current.State != TransferState.Running)
                    return;
            }

            long? committedBytes = null;
            if (current.Kind == TransferKind.Upload
                || current.Kind == TransferKind.Download && current.Resume == ResumeMode.ParallelRange)
            {
                try
                {
                    committedBytes = current.Kind == TransferKind.Upload
                        ? await GetCommittedUploadBytesAsync(current, CancellationToken.None)
                        : await GetCommittedDownloadBytesAsync(current, CancellationToken.None);
                }
                catch (Exception)
                {
                    // On shutdown, prefer a conservative checkpoint. Multipart parts
                    // can be reconciled from the service on the next attempt.
                    committedBytes = 0;
                }
            }

            var queued = current with
            {
                State = _options.IsolatedRun ? TransferState.Cancelled : TransferState.Queued,
                BytesTransferred = committedBytes ?? current.BytesTransferred,
                UpdatedAt = _clock.UtcNow
            };

            await _journal.UpsertAsync(queued, CancellationToken.None);
            lock (_gate)
            {
                if (!_jobs.TryGetValue(id, out var latest)
                    || latest.State != TransferState.Running
                    || latest.Attempt != current.Attempt)
                    return;
                _jobs[id] = queued;
            }

            RaiseChanged(queued);
            if (queued.Kind == TransferKind.Copy)
                SignalCrossCapacity();
        }
        finally
        {
            _persist.Release();
        }
    }

    // The catalog is authoritative: cached connections must never outlive an edit or deletion.
    private Task<StorageConnection?> ResolveAsync(Guid id, CancellationToken cancellationToken) =>
        _connections.FindAsync(id, cancellationToken);

    private async Task<StorageConnection> CurrentConnectionAsync(
        StorageConnection snapshot, CancellationToken cancellationToken)
    {
        var current = await ResolveAsync(snapshot.Id, cancellationToken);
        if (current is null)
            throw new StorageException(new StorageFailure(
                StorageFailureKind.CredentialMissing, null, null, null, null));
        if (new Cavdeck.Application.Connections.ConnectionChange(snapshot, current).RequiresReconnect)
            throw new StorageException(new StorageFailure(
                StorageFailureKind.InvalidConfiguration, null, null, null, null));
        return current;
    }

    private static bool UsesConnection(TransferJob job, Guid id) =>
        job.ConnectionId == id || job.CrossTransfer?.Source.ConnectionId == id
            || job.CrossTransfer?.Destination.ConnectionId == id;

    public int ConnectionWorkCount(Guid id, bool writesOnly = false) =>
        Jobs.Count(job => job.IsOpen && UsesConnection(job, id)
            && (!writesOnly || job.ConnectionId == id && job.Kind != TransferKind.Download))
        + Batches.Count(batch => batch.State == CrossBatchState.Scanning
            && (batch.DestinationConnectionId == id || !writesOnly && batch.SourceConnectionId == id));

    public async Task CancelConnectionAsync(Guid id, bool writesOnly = false)
    {
        foreach (var batch in Batches.Where(batch => batch.State == CrossBatchState.Scanning
            && (batch.DestinationConnectionId == id || !writesOnly && batch.SourceConnectionId == id)))
            if (_scanControls.TryGetValue(batch.Id, out var scan))
                CancelIfActive(scan);
        foreach (var job in Jobs.Where(job => job.IsOpen && UsesConnection(job, id)
            && (!writesOnly || job.ConnectionId == id && job.Kind != TransferKind.Download)))
            await CancelAsync(job.Id);
    }

    private TransferJob CurrentOr(Guid id, TransferJob fallback)
    {
        lock (_gate)
            return _jobs.TryGetValue(id, out var job) ? job : fallback;
    }

    private TransferJob? CurrentOrNull(Guid id)
    {
        lock (_gate)
            return _jobs.GetValueOrDefault(id);
    }

    private TransferJob Fail(TransferJob job, TransferFailure failure) =>
        job with { State = TransferState.Failed, Failure = failure, UpdatedAt = _clock.UtcNow };
}
