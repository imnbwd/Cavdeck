using System.Diagnostics;
using Cavdeck.Domain.Storage;
using Cavdeck.Domain.Transfers;

namespace Cavdeck.Application.Transfers;

internal sealed class UploadCoordinator(ITransferSink sink, TransferEngineOptions options, ITransferClock clock, ConcurrencyGate parts)
{
    public async Task<TransferJob> RunAsync(TransferJob job, IRemoteTransferLease lease, CancellationToken cancellationToken)
    {
        try
        {
            using var sourcePin = options.IsolatedRun
                ? ExactLocalPaths.Pin(job.LocalPath, includeLeaf: true) : null;
            if (options.IsolatedRun)
                lease.EnsureVerifiedConditionalPut();
            if (!job.FolderMarker && !File.Exists(job.LocalPath))
                return Fail(job, new TransferFailure(TransferFailureKind.LocalMissing, false, "NotFound", "The local file is missing."));

            var source = job.FolderMarker ? null : new FileInfo(job.LocalPath);
            var length = source?.Length ?? 0;
            DateTimeOffset? modifiedAt = source is null ? null : new DateTimeOffset(source.LastWriteTimeUtc);
            if (options.IsolatedRun && job.BaselineCaptured &&
                (job.TotalBytes != length || job.SourceModifiedAt != modifiedAt))
                return Fail(job, new TransferFailure(TransferFailureKind.Integrity, false, "LocalSourceChanged", null));
            if (!string.IsNullOrEmpty(job.UploadId)
                && (job.TotalBytes != length || job.SourceModifiedAt != modifiedAt))
            {
                try
                {
                    await lease.AbortMultipartAsync(job.Bucket, job.Key, job.UploadId, cancellationToken);
                }
                catch (StorageException exception) when (exception.Failure.ErrorCode == "NoSuchUpload")
                {
                }
                await sink.ClearPartsAsync(job.Id, cancellationToken);
                job = job with { UploadId = null, Resume = ResumeMode.Reconcile, BytesTransferred = 0 };
            }
            job = job with
            {
                TotalBytes = length,
                BytesTransferred = string.IsNullOrEmpty(job.UploadId) ? 0 : job.BytesTransferred,
                SourceModifiedAt = modifiedAt,
                ContentType = job.ContentType ?? ContentTypes.FromPath(job.LocalPath)
            };
            if (string.IsNullOrEmpty(job.UploadId))
                sink.ResetProgress(job);
            var remote = await lease.HeadAsync(job.Bucket, job.Key, cancellationToken);
            if (remote is not null && job.Conflict == TransferConflictPolicy.Fail)
                return Fail(job, new TransferFailure(TransferFailureKind.Conflict, false,
                    "TargetExists", "The destination already exists."));
            if (remote is not null && job.Conflict == TransferConflictPolicy.Skip && string.IsNullOrEmpty(job.UploadId)
                && (!options.IsolatedRun || !job.BaselineCaptured))
                return job with { State = TransferState.Completed, Skipped = true, UpdatedAt = clock.UtcNow, Failure = null };

            if (remote is not null && job.Conflict == TransferConflictPolicy.Rename && string.IsNullOrEmpty(job.UploadId)
                && (!options.IsolatedRun || !job.BaselineCaptured))
            {
                var renamed = await RenameAsync(job, lease, cancellationToken);
                if (renamed.State == TransferState.Failed)
                    return renamed;
                job = renamed;
                remote = null;
            }

            // Old journals lack BaselineCaptured; attempts > 1 already carry
            // the first attempt's Baseline and must not silently adopt a new one.
            if (!job.BaselineCaptured && job.Attempt <= 1)
                job = job with { Baseline = remote, BaselineCaptured = true };
            else if (job.Baseline is null ? remote is not null : !job.Baseline.SameContent(remote))
                return Fail(job, new TransferFailure(TransferFailureKind.Conflict, false,
                    "DestinationChanged", "The destination changed since this upload was started."));
            // Without an ETag we cannot enforce a conditional overwrite.
            if (job.Baseline is { } baseline && string.IsNullOrWhiteSpace(baseline.ETag))
                return Fail(job, new TransferFailure(TransferFailureKind.Unsupported, false,
                    "DestinationHasNoETag", "The destination does not support safe conditional overwrite."));
            await sink.PublishAsync(job, cancellationToken);

            if (job.FolderMarker)
            {
                using var empty = new MemoryStream(Array.Empty<byte>());
                return await PutAsync(job, lease, empty, 0, null, cancellationToken);
            }

            var multipart = TransferPlanner.UseMultipart(length, options.MultipartThresholdBytes);
            if (options.IsolatedRun && !lease.SupportsVerifiedConditionalMultipart)
            {
                // Providers without a verified conditional multipart completion can
                // still use a conditional PutObject within its single-request limit.
                if (length > 5L * 1024 * 1024 * 1024)
                    return Fail(job, new TransferFailure(TransferFailureKind.Unsupported, false,
                        "ConditionalMultipartUnsupported", null));
                multipart = false;
            }
            if (!string.IsNullOrEmpty(job.UploadId) || multipart)
                return await MultipartAsync(job, lease, length, cancellationToken);
            await using var file = new FileStream(job.LocalPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 81_920, FileOptions.SequentialScan);
            return await PutAsync(job, lease, file, length, job.ContentType, cancellationToken);
        }
        catch (Exception exception) when (IsCancellation(exception))
        {
            if (!options.IsolatedRun)
                await AbortIfCancelledAsync(job, lease);
            throw;
        }
        catch (StorageException exception)
        {
            return Fail(sink.Current(job.Id), TransferFailures.FromStorage(exception, options.IsolatedRun));
        }
        catch (Exception exception)
        {
            return Fail(sink.Current(job.Id), TransferFailures.FromLocal(exception));
        }
    }

    private async Task<TransferJob> RenameAsync(TransferJob job, IRemoteTransferLease lease, CancellationToken cancellationToken)
    {
        var taken = new HashSet<string>(StringComparer.Ordinal) { job.Key };
        for (var suffix = 1; suffix <= options.MaxRenameAttempts; suffix++)
        {
            var candidate = ConflictNames.WithSuffix(job.Key, suffix);
            if (!taken.Add(candidate))
                continue;
            if (await lease.HeadAsync(job.Bucket, candidate, cancellationToken) is null)
                return job with { Key = candidate, Baseline = null, DisplayName = TransferJob.DisplayNameOf(job.LocalPath, candidate, false) };
        }

        return Fail(job, new TransferFailure(TransferFailureKind.Conflict, false, "Name", "No free object name was available."));
    }

    private async Task<TransferJob> PutAsync(
        TransferJob job, IRemoteTransferLease lease, Stream content, long length, string? contentType,
        CancellationToken cancellationToken)
    {
        job = job with { BytesTransferred = 0 };
        sink.ResetProgress(job);
        string? hash = null;
        try
        {
            var contentMd5 = await TransferFiles.Md5DigestAsync(content, length, cancellationToken);
            hash = Convert.ToHexString(Convert.FromBase64String(contentMd5));
            var progress = new UploadProgressReporter(sink, job, length);
            var identity = await lease.PutConditionalAsync(job.Bucket, job.Key, content, length, contentType,
                contentMd5, job.Baseline?.ETag, job.Baseline is null, progress.ForSingle(), cancellationToken);
            progress.CompleteSingle();
            if (!SourceUnchanged(job, length))
                return Fail(job, new TransferFailure(TransferFailureKind.Integrity, false, "Changed", "The local file changed during the upload."));
            return await AcceptPutAsync(job, lease, length, hash, identity, cancellationToken);
        }
        catch (StorageException exception)
        {
            var failure = TransferFailures.FromStorage(exception, options.IsolatedRun);
            if (options.IsolatedRun || !failure.Retryable || hash is null)
                return Fail(job, failure);

            var after = await lease.HeadAsync(job.Bucket, job.Key, cancellationToken);
            if (after is not null && IntegrityRules.Verify(length, after.Size,
                    hash, after.ETag, after.ETagIsMd5) == IntegrityCheck.Match)
                return Complete(job, length, after);

            if (after is not null && (job.Baseline is null || !job.Baseline.SameContent(after)))
                return Fail(job, new TransferFailure(TransferFailureKind.Conflict, false, "RemoteChanged", "The object changed before this upload could be retried."));
            return Fail(job, failure);
        }
    }

    private async Task<TransferJob> AcceptPutAsync(
        TransferJob job, IRemoteTransferLease lease, long length, string? hash, RemoteIdentity identity,
        CancellationToken cancellationToken)
    {
        var head = await lease.HeadAsync(job.Bucket, job.Key, cancellationToken);
        if (head is null)
            return Fail(job, new TransferFailure(TransferFailureKind.Integrity, true, "Missing", "The upload did not leave an object."));
        if (!string.IsNullOrEmpty(identity.ETag) && !string.IsNullOrEmpty(head.ETag)
            && !string.Equals(RemoteIdentity.Normalize(identity.ETag), RemoteIdentity.Normalize(head.ETag), StringComparison.OrdinalIgnoreCase))
            return Fail(job, new TransferFailure(TransferFailureKind.Conflict, false, "RemoteChanged", "The object changed after the upload."));

        var check = IntegrityRules.Verify(length, head.Size, hash, head.ETag, head.ETagIsMd5);

        if (check == IntegrityCheck.Mismatch)
        {
            if (!options.IsolatedRun && job.Baseline is null)
                await TryDeleteAsync(lease, job, cancellationToken);
            return Fail(job, new TransferFailure(TransferFailureKind.Integrity, false, "Checksum", "The uploaded object did not match the local file."));
        }

        return Complete(job, length, head);
    }

    private async Task<TransferJob> MultipartAsync(
        TransferJob job, IRemoteTransferLease lease, long length, CancellationToken cancellationToken)
    {
        lease.EnsureConditionalMultipartSupported();
        var plan = TransferPlanner.Plan(length, options.PartSizeBytes, options.MinimumPartBytes, TransferPlanner.S3MaximumParts);
        var uploadId = job.UploadId;
        IReadOnlyList<TransferPart> server;
        if (string.IsNullOrEmpty(uploadId))
        {
            uploadId = await lease.InitiateMultipartAsync(job.Bucket, job.Key, job.ContentType, cancellationToken);
            job = job with { UploadId = uploadId, Resume = ResumeMode.Multipart, BytesTransferred = 0 };
            await PublishNewUploadAsync(job, lease, cancellationToken);
            server = [];
        }
        else
        {
            try
            {
                server = await lease.ListPartsAsync(job.Bucket, job.Key, uploadId, cancellationToken);
            }
            catch (StorageException exception) when (exception.Failure.ErrorCode == "NoSuchUpload")
            {
                uploadId = await lease.InitiateMultipartAsync(job.Bucket, job.Key, job.ContentType, cancellationToken);
                job = job with { UploadId = uploadId, BytesTransferred = 0 };
                await PublishNewUploadAsync(job, lease, cancellationToken);
                await sink.ClearPartsAsync(job.Id, cancellationToken);
                server = [];
            }
        }

        if (server.Any(part => plan.FirstOrDefault(item => item.Number == part.Number) is { } planned && planned.Length != part.Length))
        {
            await lease.AbortMultipartAsync(job.Bucket, job.Key, uploadId, CancellationToken.None);
            uploadId = await lease.InitiateMultipartAsync(job.Bucket, job.Key, job.ContentType, cancellationToken);
            job = job with { UploadId = uploadId, BytesTransferred = 0 };
            await PublishNewUploadAsync(job, lease, cancellationToken);
            await sink.ClearPartsAsync(job.Id, cancellationToken);
            server = [];
        }

        var done = new Dictionary<int, TransferPart>();
        foreach (var part in plan)
        {
            var existing = server.FirstOrDefault(item => item.Number == part.Number && item.Length == part.Length && !string.IsNullOrEmpty(item.ETag));
            if (existing is not null)
                done[part.Number] = existing with { Offset = part.Offset };
        }

        var recordedParts = await sink.GetPartsAsync(job.Id, cancellationToken);
        var recordedByNumber = recordedParts.ToDictionary(part => part.Number);
        var hasStaleParts = recordedParts.Any(recorded =>
            !done.TryGetValue(recorded.Number, out var serverPart) || !SamePart(recorded, serverPart));
        if (hasStaleParts)
        {
            await sink.ClearPartsAsync(job.Id, cancellationToken);
            recordedByNumber.Clear();
        }

        // The service is authoritative. Add server-confirmed parts missing from
        // the journal, without rewriting the full checkpoint on every resume.
        foreach (var part in done.Values.OrderBy(part => part.Number))
        {
            if (!recordedByNumber.TryGetValue(part.Number, out var recorded) || !SamePart(recorded, part))
                await sink.SavePartAsync(job.Id, part, cancellationToken);
        }

        var pending = plan.Where(part => !done.ContainsKey(part.Number)).ToArray();
        using var progressGate = new SemaphoreSlim(1, 1);
        var transferred = done.Values.Sum(part => part.Length);
        var progress = new UploadProgressReporter(sink, job, length, transferred);
        sink.ResetProgress(job with { BytesTransferred = transferred });
        await sink.PersistCheckpointAsync(job with
        {
            BytesTransferred = transferred,
            UpdatedAt = clock.UtcNow
        }, cancellationToken);
        job = job with { BytesTransferred = transferred };
        Exception? failure = null;
        await Parallel.ForEachAsync(pending, new ParallelOptions
        {
            MaxDegreeOfParallelism = options.MaxConcurrentParts,
            CancellationToken = cancellationToken
        }, async (part, token) =>
        {
            if (Volatile.Read(ref failure) is not null)
                return;
            var acquired = false;
            try
            {
                await parts.AcquireAsync(token);
                acquired = true;
                if (Volatile.Read(ref failure) is not null)
                    return;
                await using var window = new FileWindowStream(job.LocalPath, part.Offset, part.Length);
                var contentMd5 = await TransferFiles.Md5DigestAsync(window, part.Length, token);
                var etag = await lease.UploadPartAsync(
                    job.Bucket, job.Key, uploadId, part.Number, window, part.Length, contentMd5,
                    progress.ForPart(part.Number, part.Length), token);
                var stored = part with { ETag = etag };
                await progressGate.WaitAsync(token);
                try
                {
                    done[part.Number] = stored;
                    transferred += part.Length;
                    progress.CompletePart(part.Number, part.Length);
                    await sink.SavePartAsync(job.Id, stored, token);
                    await sink.PersistCheckpointAsync(job with
                    {
                        BytesTransferred = transferred,
                        State = TransferState.Running,
                        UpdatedAt = clock.UtcNow
                    }, token);
                }
                finally
                {
                    progressGate.Release();
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                progress.RemovePart(part.Number);
                Interlocked.CompareExchange(ref failure, exception, null);
            }
            finally
            {
                if (acquired)
                    parts.Release();
            }
        });

        if (failure is not null)
        {
            var failedJob = job with { BytesTransferred = transferred };
            sink.ResetProgress(failedJob);
            return failure is StorageException storage
                ? Fail(failedJob, TransferFailures.FromStorage(storage, options.IsolatedRun))
                : Fail(failedJob, TransferFailures.FromLocal(failure));
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (!SourceUnchanged(job, length))
        {
            await lease.AbortMultipartAsync(job.Bucket, job.Key, uploadId, CancellationToken.None);
            return Fail(job, new TransferFailure(TransferFailureKind.Integrity, false, "Changed", "The local file changed during the upload."));
        }

        var ordered = plan.Select(part => done[part.Number]).ToArray();
        await lease.CompleteMultipartConditionalAsync(job.Bucket, job.Key, uploadId, ordered,
            job.Baseline?.ETag, job.Baseline is null, cancellationToken);
        var head = await lease.HeadAsync(job.Bucket, job.Key, cancellationToken);
        if (head is null || head.Size != length)
        {
            if (!options.IsolatedRun && job.Baseline is null)
                await TryDeleteAsync(lease, job, cancellationToken);
            return Fail(job, new TransferFailure(TransferFailureKind.Integrity, false, "Size", "The completed upload size does not match the local file."));
        }

        return Complete(job, length, head);
    }

    private async Task PublishNewUploadAsync(TransferJob job, IRemoteTransferLease lease, CancellationToken cancellationToken)
    {
        try
        {
            if (!options.IsolatedRun)
                cancellationToken.ThrowIfCancellationRequested();
            await sink.PublishAsync(job, options.IsolatedRun ? CancellationToken.None : cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (sink.Current(job.Id).State != TransferState.Running)
                throw new OperationCanceledException(cancellationToken);
        }
        catch (Exception exception) when (options.IsolatedRun || exception is OperationCanceledException)
        {
            // The server may have created the upload before pause, cancellation,
            // or shutdown, while the journal still has no ID for resuming or cleanup.
            var current = sink.Current(job.Id);
            if (current.UploadId != job.UploadId)
            {
                try
                {
                    await lease.AbortMultipartAsync(job.Bucket, job.Key, job.UploadId!, CancellationToken.None);
                }
                catch (StorageException)
                {
                }
            }

            throw;
        }
    }

    private static bool IsCancellation(Exception exception) =>
        exception is OperationCanceledException
        || exception is AggregateException aggregate
            && aggregate.Flatten().InnerExceptions.All(item => item is OperationCanceledException);

    private async Task AbortIfCancelledAsync(TransferJob job, IRemoteTransferLease lease)
    {
        var current = sink.Current(job.Id);
        if (current.State != TransferState.Cancelled)
            return;
        var uploadId = current.UploadId ?? job.UploadId;
        if (string.IsNullOrEmpty(uploadId))
            return;
        try
        {
            await lease.AbortMultipartAsync(job.Bucket, current.Key, uploadId, CancellationToken.None);
        }
        catch (StorageException)
        {
        }
    }

    private static async Task TryDeleteAsync(IRemoteTransferLease lease, TransferJob job, CancellationToken cancellationToken)
    {
        try
        {
            await lease.DeleteAsync(job.Bucket, job.Key, cancellationToken);
        }
        catch (StorageException)
        {
        }
    }

    private static bool SamePart(TransferPart left, TransferPart right) =>
        left.Offset == right.Offset
        && left.Length == right.Length
        && string.Equals(left.ETag, right.ETag, StringComparison.Ordinal);

    private static bool SourceUnchanged(TransferJob job, long length)
    {
        if (job.FolderMarker)
            return true;
        var source = new FileInfo(job.LocalPath);
        return source.Exists && source.Length == length
            && new DateTimeOffset(source.LastWriteTimeUtc) == job.SourceModifiedAt;
    }

    private TransferJob Fail(TransferJob job, TransferFailure failure) =>
        job with { State = TransferState.Failed, Failure = failure, UpdatedAt = clock.UtcNow };

    private TransferJob Complete(TransferJob job, long length, RemoteIdentity identity) =>
        job with
        {
            State = TransferState.Completed,
            Failure = null,
            Skipped = false,
            BytesTransferred = length,
            TotalBytes = length,
            Remote = identity,
            UploadId = options.IsolatedRun ? null : job.UploadId,
            UpdatedAt = clock.UtcNow
        };

    private sealed class UploadProgressReporter
    {
        private static readonly TimeSpan ReportInterval = TimeSpan.FromMilliseconds(200);
        private readonly object _gate = new();
        private readonly ITransferSink _sink;
        private readonly TransferJob _job;
        private readonly long _totalBytes;
        private readonly Dictionary<int, long> _inFlight = [];
        private readonly HashSet<int> _completedParts = [];
        private long _completedBytes;
        private long _lastReportedBytes;
        private long _lastReportTimestamp;

        public UploadProgressReporter(
            ITransferSink sink, TransferJob job, long totalBytes, long completedBytes = 0)
        {
            _sink = sink;
            _job = job;
            _totalBytes = Math.Max(0, totalBytes);
            _completedBytes = Math.Clamp(completedBytes, 0, _totalBytes);
            _lastReportedBytes = _completedBytes;
        }

        public IProgress<long> ForSingle() =>
            new InlineProgress<long>(bytes => ReportSingle(bytes));

        public IProgress<long> ForPart(int partNumber, long partLength) =>
            new InlineProgress<long>(bytes => ReportPart(partNumber, partLength, bytes));

        public void CompleteSingle() => ReportSingle(_totalBytes, force: true);

        public void CompletePart(int partNumber, long partLength)
        {
            long aggregate;
            lock (_gate)
            {
                if (!_completedParts.Add(partNumber))
                    return;
                _inFlight.Remove(partNumber);
                var increment = Math.Max(0, partLength);
                _completedBytes = increment >= _totalBytes - _completedBytes
                    ? _totalBytes : _completedBytes + increment;
                aggregate = AggregateLocked();
            }

            Publish(aggregate, force: true);
        }

        public void RemovePart(int partNumber)
        {
            lock (_gate)
                _inFlight.Remove(partNumber);
        }

        private void ReportSingle(long bytes, bool force = false) =>
            Publish(Math.Clamp(bytes, 0, _totalBytes), force);

        private void ReportPart(int partNumber, long partLength, long bytes)
        {
            long aggregate;
            lock (_gate)
            {
                if (_completedParts.Contains(partNumber))
                    return;
                var bounded = Math.Clamp(bytes, 0, Math.Max(0, partLength));
                _inFlight[partNumber] = Math.Max(_inFlight.GetValueOrDefault(partNumber), bounded);
                aggregate = AggregateLocked();
            }

            Publish(aggregate, force: false);
        }

        private long AggregateLocked()
        {
            var inFlightBytes = 0L;
            foreach (var bytes in _inFlight.Values)
            {
                var remaining = _totalBytes - _completedBytes - inFlightBytes;
                if (remaining <= 0)
                    break;
                inFlightBytes += Math.Min(bytes, remaining);
            }

            return _completedBytes + inFlightBytes;
        }

        private void Publish(long bytes, bool force)
        {
            TransferJob update;
            lock (_gate)
            {
                bytes = Math.Clamp(bytes, 0, _totalBytes);
                if (bytes <= _lastReportedBytes)
                    return;

                var now = Stopwatch.GetTimestamp();
                if (!force && _lastReportTimestamp != 0
                    && Stopwatch.GetElapsedTime(_lastReportTimestamp, now) < ReportInterval)
                    return;

                _lastReportedBytes = bytes;
                _lastReportTimestamp = now;
                update = _job with { BytesTransferred = bytes };
            }

            _sink.ReportProgress(update);
        }
    }
}
