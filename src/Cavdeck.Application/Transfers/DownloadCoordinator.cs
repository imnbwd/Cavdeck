using Cavdeck.Domain.Storage;
using Cavdeck.Domain.Transfers;

namespace Cavdeck.Application.Transfers;

internal sealed class DownloadCoordinator(ITransferSink sink, ITransferClock clock, TransferEngineOptions options, ConcurrencyGate parts)
{
    private readonly ParallelDownloadCoordinator _parallel = new(sink, clock, options, parts);

    public async Task<TransferJob> RunAsync(TransferJob job, IRemoteTransferLease lease, CancellationToken cancellationToken)
    {
        var partial = TransferPaths.Partial(job.LocalPath, job.Id);
        try
        {
            var remote = await lease.HeadAsync(job.Bucket, job.Key, cancellationToken);
            if (remote is null)
                return Fail(job, new TransferFailure(TransferFailureKind.NotFound, false, "NoSuchKey", "The object does not exist."));

            var destinationAtStart = TransferFiles.Snapshot(job.LocalPath);
            if (destinationAtStart is not null && job.Conflict == TransferConflictPolicy.Skip)
                return job with { State = TransferState.Completed, Skipped = true, Failure = null, TotalBytes = remote.Size, UpdatedAt = clock.UtcNow };

            if (destinationAtStart is not null && job.Conflict == TransferConflictPolicy.Rename)
            {
                var renamed = Rename(job.LocalPath);
                if (renamed is null)
                    return Fail(job, new TransferFailure(TransferFailureKind.Conflict, false, "Name", "No free local name was available."));
                job = job with { LocalPath = renamed, DisplayName = Path.GetFileName(renamed) };
                partial = TransferPaths.Partial(renamed, job.Id);
                destinationAtStart = null;
            }

            var recorded = job.Remote;
            if (recorded is not null && !recorded.SameContent(remote)
                || job.Resume == ResumeMode.ParallelRange
                    && (recorded is null || string.IsNullOrWhiteSpace(recorded.ETag)))
            {
                // Never reuse a parallel checkpoint without a recorded source
                // identity, even if the preallocated file and hashes look valid.
                TransferFiles.DeleteIfExists(partial);
                await sink.ClearPartsAsync(job.Id, cancellationToken);
                job = job with { Remote = remote, TotalBytes = remote.Size, BytesTransferred = 0 };
            }
            else
            {
                job = job with { Remote = remote, TotalBytes = remote.Size };
            }

            await sink.PublishAsync(job, cancellationToken);
            var partialLength = File.Exists(partial) ? new FileInfo(partial).Length : 0;
            var sequentialPartial = job.Resume is ResumeMode.Range or ResumeMode.SequentialOnly
                && partialLength > 0 && recorded is not null && recorded.SameContent(remote);
            var useParallel = !string.IsNullOrWhiteSpace(remote.ETag)
                && (job.Resume == ResumeMode.ParallelRange
                    || job.Resume != ResumeMode.SequentialOnly
                        && !sequentialPartial && remote.Size >= options.DownloadThresholdBytes);

            if (useParallel)
            {
                if (job.Resume != ResumeMode.ParallelRange)
                {
                    TransferFiles.DeleteIfExists(partial);
                    await sink.ClearPartsAsync(job.Id, cancellationToken);
                    job = job with { BytesTransferred = 0 };
                }

                job = job with { Resume = ResumeMode.ParallelRange };
                await sink.PublishAsync(job, cancellationToken);
                try
                {
                    job = await _parallel.RunAsync(job, lease, partial, remote, cancellationToken);
                    return await FinishAsync(job, lease, partial, remote, destinationAtStart, cancellationToken);
                }
                catch (ParallelDownloadUnavailableException)
                {
                    // Do not retry parallel ranges on every attempt against an
                    // incompatible endpoint. Restart once with the existing flow.
                    TransferFiles.DeleteIfExists(partial);
                    await sink.ClearPartsAsync(job.Id, cancellationToken);
                    job = job with { Resume = ResumeMode.SequentialOnly, BytesTransferred = 0 };
                    sink.ResetProgress(job);
                    await sink.PublishAsync(job, cancellationToken);
                    partialLength = 0;
                }
            }
            else if (job.Resume == ResumeMode.ParallelRange)
            {
                // A previously resumable parallel job can no longer use fixed
                // ranges (for example the source no longer returns an ETag).
                TransferFiles.DeleteIfExists(partial);
                await sink.ClearPartsAsync(job.Id, cancellationToken);
                job = job with { Resume = ResumeMode.SequentialOnly, BytesTransferred = 0 };
                sink.ResetProgress(job);
                await sink.PublishAsync(job, cancellationToken);
                partialLength = 0;
            }

            var action = ResumeRules.ForDownload(remote, recorded, partialLength);
            if (action == ResumeAction.Restart)
            {
                TransferFiles.DeleteIfExists(partial);
                partialLength = 0;
            }

            if (action == ResumeAction.CommitPartial)
                return await FinishAsync(job, lease, partial, remote, destinationAtStart, cancellationToken);

            var start = action == ResumeAction.ContinueRange ? partialLength : 0L;
            await using var body = await lease.OpenDownloadAsync(job.Bucket, job.Key, start == 0 ? null : start, cancellationToken);
            if (!body.Identity.SameContent(remote))
            {
                TransferFiles.DeleteIfExists(partial);
                return Fail(job, new TransferFailure(TransferFailureKind.Conflict, false, "RemoteChanged", "The object changed while it was being downloaded."));
            }

            if (start > 0 && !body.RangeHonored)
            {
                TransferFiles.DeleteIfExists(partial);
                start = 0;
            }

            var parent = Path.GetDirectoryName(partial);
            if (!string.IsNullOrEmpty(parent))
                Directory.CreateDirectory(parent);
            await using (var target = new FileStream(
                partial,
                start == 0 ? FileMode.Create : FileMode.Open,
                FileAccess.Write,
                FileShare.None,
                81_920,
                FileOptions.Asynchronous))
            {
                if (start > 0)
                {
                    target.Seek(start, SeekOrigin.Begin);
                    target.SetLength(start);
                }

                var lastReport = clock.UtcNow;
                var copied = await TransferFiles.CopyAsync(body.Content, target, async written =>
                {
                    var now = clock.UtcNow;
                    if (now - lastReport < TimeSpan.FromMilliseconds(250))
                        return;
                    lastReport = now;
                    await sink.PublishAsync(job with
                    {
                        BytesTransferred = start + written,
                        State = TransferState.Running,
                        UpdatedAt = now
                    }, cancellationToken);
                }, cancellationToken);
                await sink.PublishAsync(job with
                {
                    BytesTransferred = start + copied,
                    State = TransferState.Running,
                    UpdatedAt = clock.UtcNow
                }, cancellationToken);
                await target.FlushAsync(cancellationToken);
            }

            return await FinishAsync(job, lease, partial, remote, destinationAtStart, cancellationToken);
        }
        catch (Exception exception) when (exception is OperationCanceledException
            || exception is AggregateException aggregate
                && aggregate.Flatten().InnerExceptions.All(item => item is OperationCanceledException))
        {
            if (sink.Current(job.Id).State == TransferState.Cancelled)
            {
                TransferFiles.DeleteIfExists(partial);
                TransferFiles.DeleteIfExists(TransferPaths.Commit(job.LocalPath, job.Id));
                await sink.ClearPartsAsync(job.Id, CancellationToken.None);
            }

            throw;
        }
        catch (DestinationChangedException)
        {
            return Fail(sink.Current(job.Id), new TransferFailure(TransferFailureKind.Conflict, false,
                "LocalDestinationChanged", "The local destination changed during the download."));
        }
        catch (DownloadRangeIncompleteException)
        {
            return Fail(sink.Current(job.Id), new TransferFailure(TransferFailureKind.Integrity, true,
                "ShortRange", "A download range ended early; incomplete parts will be retried."));
        }
        catch (StorageException exception) when (exception.Failure.ErrorCode == "InvalidRangeResponse")
        {
            TransferFiles.DeleteIfExists(partial);
            return Fail(sink.Current(job.Id) with { BytesTransferred = 0 },
                new TransferFailure(TransferFailureKind.Integrity, true, "InvalidRangeResponse",
                    "The server returned an unexpected byte range; the download will restart."));
        }
        catch (StorageException exception) when (exception.Failure.ErrorCode == "SourceChanged")
        {
            // Ranged requests are identity-pinned. If the source changes, all
            // of the old parts become unusable and must not consume disk space.
            TransferFiles.DeleteIfExists(partial);
            await sink.ClearPartsAsync(job.Id, CancellationToken.None);
            return Fail(sink.Current(job.Id) with { BytesTransferred = 0 },
                TransferFailures.FromStorage(exception));
        }
        catch (StorageException exception)
        {
            return Fail(sink.Current(job.Id), TransferFailures.FromStorage(exception));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Fail(sink.Current(job.Id), TransferFailures.FromLocal(exception));
        }
    }

    private async Task<TransferJob> FinishAsync(
        TransferJob job, IRemoteTransferLease lease, string partial, RemoteIdentity started,
        LocalFileStamp? destinationAtStart, CancellationToken cancellationToken)
    {
        var after = await lease.HeadAsync(job.Bucket, job.Key, cancellationToken);
        if (after is null || !after.SameContent(started))
        {
            TransferFiles.DeleteIfExists(partial);
            return Fail(job, new TransferFailure(TransferFailureKind.Conflict, false, "RemoteChanged", "The object changed while it was being downloaded."));
        }

        var length = File.Exists(partial) ? new FileInfo(partial).Length : 0;
        if (length != after.Size)
            return Fail(job, new TransferFailure(TransferFailureKind.Integrity, true, "Short", "The download ended before the object was complete."));

        if (after.ETagIsMd5 && IntegrityRules.IsWholeObjectMd5(after.ETag))
        {
            var hash = await TransferFiles.HashFileAsync(partial, cancellationToken);
            if (IntegrityRules.Verify(length, after.Size, hash, after.ETag, after.ETagIsMd5) == IntegrityCheck.Mismatch)
            {
                TransferFiles.DeleteIfExists(partial);
                return Fail(job, new TransferFailure(TransferFailureKind.Integrity, false, "Checksum", "The downloaded file did not match the object."));
            }
        }

        TransferFiles.Commit(partial, job.LocalPath, job.Id, destinationAtStart);
        return job with
        {
            State = TransferState.Completed,
            Failure = null,
            Skipped = false,
            BytesTransferred = length,
            TotalBytes = length,
            Remote = after,
            UpdatedAt = clock.UtcNow
        };
    }

    private static string? Rename(string path)
    {
        var directory = Path.GetDirectoryName(path) ?? "";
        var name = Path.GetFileName(path);
        for (var suffix = 1; suffix <= 50; suffix++)
        {
            var candidate = Path.Combine(directory, ConflictNames.WithSuffix(name, suffix));
            if (!File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    private TransferJob Fail(TransferJob job, TransferFailure failure) =>
        job with { State = TransferState.Failed, Failure = failure, UpdatedAt = clock.UtcNow };
}
