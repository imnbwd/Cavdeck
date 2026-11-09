using System.Security.Cryptography;
using Cavdeck.Domain.Storage;
using Cavdeck.Domain.Transfers;

namespace Cavdeck.Application.Transfers;

internal sealed class CrossTransferCoordinator(
    ITransferSink sink, ITransferJournal journal, IRemoteObjectTransfer remote,
    TransferEngineOptions options, ITransferClock clock, CrossPartBudget tempBudget)
{
    public async Task<TransferJob> RunAsync(
        TransferJob job, StorageConnection sourceConnection, StorageConnection destinationConnection,
        CancellationToken cancellationToken)
    {
        var spec = job.CrossTransfer ?? throw new InvalidOperationException("Cross-copy payload is missing.");
        ObjectAccess.Ensure(sourceConnection, spec.Source.Bucket, spec.Source.Key, write: spec.Move);
        ObjectAccess.Ensure(destinationConnection, spec.Destination.Bucket, spec.Destination.Key, write: true);
        if (CrossTransferPlanner.SameBackend(sourceConnection, destinationConnection)
            && spec.Source.Bucket == spec.Destination.Bucket
            && spec.Source.Key == spec.Destination.Key)
            return Failure(job, TransferFailureKind.Conflict, "SourceAndDestinationMatch");

        var sourceConfig = CrossTransferPlanner.ConfigurationIdentity(sourceConnection);
        var destinationConfig = CrossTransferPlanner.ConfigurationIdentity(destinationConnection);
        if (!CrossTransferPlanner.MatchesConfiguration(spec.SourceConfiguration, sourceConnection)
            || !CrossTransferPlanner.MatchesConfiguration(spec.DestinationConfiguration, destinationConnection))
            return Failure(job, TransferFailureKind.ConnectionMissing, "ConnectionConfigurationChanged");

        await using var source = await remote.OpenAsync(sourceConnection, write: spec.Move, cancellationToken);
        await using var destination = await remote.OpenAsync(destinationConnection, write: true, cancellationToken);
        if (options.IsolatedRun && spec.Move)
            source.EnsureVerifiedConditionalDelete();
        if (spec.Move && spec.Stage == CrossTransferStage.Removing)
            return await CompleteMoveAsync(job, source, destination, cancellationToken);
        if (options.IsolatedRun)
            destination.EnsureVerifiedConditionalPut();

        var identity = await source.HeadAsync(spec.Source.Bucket, spec.Source.Key, cancellationToken);
        if (identity is null)
            return Failure(job, TransferFailureKind.NotFound, "SourceMissing");
        if (string.IsNullOrWhiteSpace(identity.ETag))
            return Failure(job, TransferFailureKind.Unsupported, "SourceHasNoETag");
        if ((spec.SourceETag is not null && !string.Equals(RemoteIdentity.Normalize(spec.SourceETag),
                RemoteIdentity.Normalize(identity.ETag), StringComparison.Ordinal)) ||
            (spec.SourceIdentity is not null && !SameIdentity(spec.SourceIdentity, identity)))
            return Failure(job, TransferFailureKind.Integrity, "SourceChanged");

        var originalDestination = await destination.HeadAsync(spec.Destination.Bucket, spec.Destination.Key, cancellationToken);
        if ((job.UploadId is not null || spec.SinglePut) && spec.Stage == CrossTransferStage.Verifying
            && originalDestination is not null
            && !SameIdentity(spec.DestinationBaseline, originalDestination))
        {
            var verificationPlan = VerificationPlan(identity.Size, spec);
            var savedParts = (await journal.GetPartsAsync(job.Id, cancellationToken))
                .Where(part => part.Sha256 is not null).ToDictionary(part => part.Number);
            if (verificationPlan.Count == savedParts.Count)
                return await FinishCopyAsync(await VerifyAsync(job, destination, verificationPlan, savedParts, cancellationToken),
                    source, destination, cancellationToken);
            return Failure(job, TransferFailureKind.Integrity, "MissingVerificationHashes");
        }
        if (spec.DestinationBaseline is not null && !SameIdentity(spec.DestinationBaseline, originalDestination))
            return Failure(job, TransferFailureKind.Conflict, "DestinationChanged");
        if (spec.Stage != CrossTransferStage.Preparing && spec.DestinationBaseline is null && originalDestination is not null)
            return Failure(job, TransferFailureKind.Conflict, "DestinationAppearedDuringResume");

        if (spec.Stage == CrossTransferStage.Preparing && originalDestination is not null)
        {
            if (options.IsolatedRun && spec.Move && spec.Source.Bucket == spec.Destination.Bucket &&
                spec.Source.Key == spec.Destination.Key && SameIdentity(identity, originalDestination))
                return Failure(job, TransferFailureKind.Conflict, "SourceAndDestinationMatch");
            if (spec.Conflict == TransferConflictPolicy.Fail)
                return Failure(job, TransferFailureKind.Conflict, "TargetExists");
            if (spec.Conflict == TransferConflictPolicy.Skip)
                return job with { State = TransferState.Completed, Skipped = true, UpdatedAt = clock.UtcNow };
            if (spec.Conflict == TransferConflictPolicy.Rename)
            {
                var candidate = await FindFreeNameAsync(destination, spec.Destination, cancellationToken);
                if (candidate is null)
                    return Failure(job, TransferFailureKind.Conflict, "NoFreeDestinationName");
                if (System.Text.Encoding.UTF8.GetByteCount(candidate) > ObjectLayout.MaxKeyBytes)
                    return Failure(job, TransferFailureKind.InvalidPath, "DestinationKeyTooLong");
                ObjectAccess.Ensure(destinationConnection, spec.Destination.Bucket, candidate, write: true);
                spec = spec with { Destination = spec.Destination with { Key = candidate } };
                originalDestination = null;
            }
            else if (string.IsNullOrWhiteSpace(originalDestination.ETag))
                return Failure(job, TransferFailureKind.Unsupported, "DestinationHasNoETag");
        }

        if (spec.Stage == CrossTransferStage.Preparing)
        {
            spec = spec with
            {
                SourceIdentity = identity,
                Source = spec.Source with { VersionId = identity.VersionId },
                DestinationBaseline = originalDestination,
                SourceConfiguration = sourceConfig,
                DestinationConfiguration = destinationConfig,
                Stage = CrossTransferStage.Transferring,
                SinglePut = identity.Size == 0 || options.IsolatedRun && !destination.SupportsVerifiedConditionalMultipart
            };
            job = job with { CrossTransfer = spec, Key = spec.Destination.Key,
                ContentType = identity.ContentType,
                TotalBytes = identity.Size, UpdatedAt = clock.UtcNow };
            await sink.PublishAsync(job, cancellationToken);
        }

        var plan = VerificationPlan(identity.Size, spec);
        if (plan.Any(part => part.Length > tempBudget.Limit))
            return Failure(job, TransferFailureKind.Unsupported, "PartExceedsTemporaryStorageBudget");
        if (spec.SinglePut || plan.Count == 0)
            return await PutSingleAsync(job, source, destination, identity, plan, cancellationToken);

        destination.EnsureConditionalMultipartSupported();

        var multipart = await PrepareMultipartAsync(job, spec, destination, plan, cancellationToken);
        job = multipart.Job;
        var completed = multipart.Completed;
        try
        {
            foreach (var part in plan)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (completed.ContainsKey(part.Number))
                    continue;
                await using var buffer = await ReadPartAsync(source, spec.Source, identity.ETag!, part,
                    options.CrossCopyMemoryBudgetBytes / 8, tempBudget, cancellationToken);
                var etag = await destination.UploadPartAsync(spec.Destination.Bucket, spec.Destination.Key,
                    job.UploadId!, part.Number, buffer.Content, part.Length, buffer.Md5, null, cancellationToken);
                var saved = part with { ETag = etag, Sha256 = buffer.Sha256 };
                await sink.SavePartAsync(job.Id, saved, cancellationToken);
                completed[part.Number] = saved;
                job = job with { BytesTransferred = completed.Values.Sum(value => value.Length), UpdatedAt = clock.UtcNow };
                await sink.PublishAsync(job, cancellationToken);
            }
            var latestSource = await source.HeadAsync(spec.Source.Bucket, spec.Source.Key, cancellationToken);
            if (!SameIdentity(identity, latestSource))
                return await AbortFailedAsync(job, destination, spec, TransferFailureKind.Integrity,
                    "SourceChanged");
            var latestDestination = await destination.HeadAsync(spec.Destination.Bucket, spec.Destination.Key, cancellationToken);
            if (!SameIdentity(originalDestination, latestDestination))
                return await AbortFailedAsync(job, destination, spec, TransferFailureKind.Conflict,
                    "DestinationChanged");
            job = job with { CrossTransfer = spec with { Stage = CrossTransferStage.Verifying }, UpdatedAt = clock.UtcNow };
            await sink.PublishAsync(job, cancellationToken);
            await destination.CompleteMultipartConditionalAsync(spec.Destination.Bucket, spec.Destination.Key,
                job.UploadId!, completed.Values.OrderBy(item => item.Number).ToList(),
                originalDestination?.ETag, originalDestination is null, cancellationToken);
            return await FinishCopyAsync(await VerifyAsync(job, destination, plan, completed, cancellationToken),
                source, destination, cancellationToken);
        }
        catch (OperationCanceledException) when (sink.Current(job.Id).State == TransferState.Cancelled)
        {
            try
            {
                await destination.AbortMultipartAsync(spec.Destination.Bucket,
                    spec.Destination.Key, job.UploadId!, CancellationToken.None);
            }
            finally
            {
                await sink.ClearPartsAsync(job.Id, CancellationToken.None);
            }
            throw;
        }
        catch (StorageException error) when (!TransferFailures.FromStorage(error).Retryable
            && sink.Current(job.Id).CrossTransfer?.Stage != CrossTransferStage.Removing)
        {
            return await AbortFailedAsync(job, destination, spec, TransferFailures.FromStorage(error).Kind,
                error.Failure.ErrorCode ?? "StorageFailure");
        }
        catch (Exception error) when (error is not (OperationCanceledException or StorageException)
            && !TransferFailures.FromLocal(error).Retryable
            && sink.Current(job.Id).CrossTransfer?.Stage != CrossTransferStage.Removing)
        {
            var failure = TransferFailures.FromLocal(error);
            return await AbortFailedAsync(job, destination, spec, failure.Kind,
                failure.Code ?? "TransferFailure");
        }
    }

    private IReadOnlyList<TransferPart> VerificationPlan(long size, CrossTransferSpec spec) =>
        spec.SinglePut && size > 0 ? [new TransferPart(1, 0, size, null)] : CrossTransferPlanner.Plan(size, options);

    private async Task<TransferJob> PutSingleAsync(TransferJob job, IRemoteTransferLease source,
        IRemoteTransferLease destination, RemoteIdentity identity, IReadOnlyList<TransferPart> plan,
        CancellationToken cancellationToken)
    {
        var spec = job.CrossTransfer!;
        if (identity.Size > 5L * 1024 * 1024 * 1024)
            return Failure(job, TransferFailureKind.Unsupported, "ConditionalMultipartUnsupported");
        using var empty = new MemoryStream([]);
        await using var buffer = plan.Count == 0 ? null : await ReadPartAsync(source, spec.Source, identity.ETag!,
            plan[0], options.CrossCopyMemoryBudgetBytes / 8, tempBudget, cancellationToken);
        var saved = new Dictionary<int, TransferPart>();
        if (buffer is not null)
        {
            saved.Add(1, plan[0] with { Sha256 = buffer.Sha256 });
            await sink.SavePartAsync(job.Id, saved[1], cancellationToken);
        }
        if (!SameIdentity(identity, await source.HeadAsync(spec.Source.Bucket, spec.Source.Key, cancellationToken)))
            return Failure(job, TransferFailureKind.Integrity, "SourceChanged");
        job = job with { CrossTransfer = spec with { Stage = CrossTransferStage.Verifying }, UpdatedAt = clock.UtcNow };
        await sink.PublishAsync(job, cancellationToken);
        await destination.PutConditionalAsync(spec.Destination.Bucket, spec.Destination.Key, buffer?.Content ?? empty,
            identity.Size, job.ContentType, buffer?.Md5 ?? "1B2M2Y8AsgTpgAmY7PhCfg==",
            spec.DestinationBaseline?.ETag, spec.DestinationBaseline is null, cancellationToken);
        return await FinishCopyAsync(await VerifyAsync(job, destination, plan, saved, cancellationToken),
            source, destination, cancellationToken);
    }

    private async Task<TransferJob> FinishCopyAsync(TransferJob verified,
        IRemoteTransferLease source, IRemoteTransferLease destination, CancellationToken cancellationToken)
    {
        if (!verified.CrossTransfer!.Move || verified.State != TransferState.Completed)
            return verified;

        // Persist the post-copy phase before touching the source. A crash after
        // conditional deletion must never restart an upload or delete another version.
        var removing = verified with
        {
            State = TransferState.Running,
            CrossTransfer = verified.CrossTransfer with { Stage = CrossTransferStage.Removing },
            UpdatedAt = clock.UtcNow
        };
        await sink.PublishAsync(removing, cancellationToken);
        return await CompleteMoveAsync(removing, source, destination, cancellationToken);
    }

    private async Task<TransferJob> CompleteMoveAsync(TransferJob job,
        IRemoteTransferLease source, IRemoteTransferLease destination, CancellationToken cancellationToken)
    {
        var spec = job.CrossTransfer!;
        if (spec.SourceIdentity is not { ETag: { Length: > 0 } } original)
            return Failure(job, TransferFailureKind.Integrity, "MissingMoveSourceIdentity");

        // Reverify on restart, including when the source was deleted immediately
        // before a crash. Never trust a size-only check for a completed move.
        var plan = VerificationPlan(original.Size, spec);
        var saved = (await journal.GetPartsAsync(job.Id, cancellationToken))
            .Where(part => part.Sha256 is not null).ToDictionary(part => part.Number);
        if (plan.Count != saved.Count)
            return Failure(job, TransferFailureKind.Integrity, "MissingVerificationHashes");
        var verified = await VerifyAsync(job, destination, plan, saved, cancellationToken);
        if (verified.State != TransferState.Completed)
            return verified;

        var latest = await source.HeadAsync(spec.Source.Bucket, spec.Source.Key, cancellationToken);
        if (latest is not null)
        {
            if (!SameIdentity(original, latest))
                return Failure(job, TransferFailureKind.Conflict, "SourceChangedBeforeDelete");
            await source.DeleteIfMatchAsync(spec.Source.Bucket, spec.Source.Key,
                original.ETag!, cancellationToken);
        }
        return verified with { State = TransferState.Completed, UpdatedAt = clock.UtcNow };
    }

    private async Task<(TransferJob Job, Dictionary<int, TransferPart> Completed)> PrepareMultipartAsync(
        TransferJob job, CrossTransferSpec spec, IRemoteTransferLease destination,
        IReadOnlyList<TransferPart> plan, CancellationToken cancellationToken)
    {
        var completed = new Dictionary<int, TransferPart>();
        if (job.UploadId is not null)
        {
            var local = await journal.GetPartsAsync(job.Id, cancellationToken);
            IReadOnlyList<TransferPart> remoteParts;
            try
            {
                remoteParts = await destination.ListPartsAsync(spec.Destination.Bucket,
                    spec.Destination.Key, job.UploadId, cancellationToken);
            }
            catch (StorageException error) when (error.Failure.ErrorCode == "NoSuchUpload")
            {
                await sink.ClearPartsAsync(job.Id, cancellationToken);
                var replacement = await destination.InitiateMultipartAsync(spec.Destination.Bucket,
                    spec.Destination.Key, job.ContentType, cancellationToken);
                job = job with { UploadId = replacement, BytesTransferred = 0, UpdatedAt = clock.UtcNow };
                await sink.PublishAsync(job, cancellationToken);
                remoteParts = [];
            }
            foreach (var part in plan)
            {
                var saved = local.FirstOrDefault(item => item.Number == part.Number);
                var remotePart = remoteParts.FirstOrDefault(item => item.Number == part.Number);
                if (saved is { Sha256: not null, ETag: not null }
                    && remotePart?.ETag == saved.ETag && remotePart.Length == part.Length
                    && saved.Offset == part.Offset && saved.Length == part.Length)
                    completed[part.Number] = saved;
            }
        }
        else
        {
            var uploadId = await destination.InitiateMultipartAsync(spec.Destination.Bucket,
                spec.Destination.Key, job.ContentType, cancellationToken);
            job = job with { UploadId = uploadId, UpdatedAt = clock.UtcNow };
            await sink.PublishAsync(job, cancellationToken);
        }

        return (job, completed);
    }

    private async Task<TransferJob> AbortFailedAsync(TransferJob job,
        IRemoteTransferLease destination, CrossTransferSpec spec, TransferFailureKind kind, string code)
    {
        try
        {
            await destination.AbortMultipartAsync(spec.Destination.Bucket, spec.Destination.Key,
                job.UploadId!, CancellationToken.None);
            await sink.ClearPartsAsync(job.Id, CancellationToken.None);
            return Failure(job with { UploadId = null }, kind, code);
        }
        catch (StorageException error) when (error.Failure.ErrorCode == "NoSuchUpload")
        {
            await sink.ClearPartsAsync(job.Id, CancellationToken.None);
            return Failure(job with { UploadId = null }, kind, code);
        }
        catch (Exception error)
        {
            return Failure(job, kind, code) with
            {
                CrossTransfer = spec with { CleanupPending = true },
                Failure = new TransferFailure(kind, false, code,
                    code + "; multipart cleanup pending: " + error.GetType().Name)
            };
        }
    }

    private async Task<TransferJob> VerifyAsync(TransferJob job, IRemoteTransferLease destination,
        IReadOnlyList<TransferPart> plan, IReadOnlyDictionary<int, TransferPart> completed,
        CancellationToken cancellationToken)
    {
        var spec = job.CrossTransfer!;
        var head = await destination.HeadAsync(spec.Destination.Bucket, spec.Destination.Key, cancellationToken);
        if (head is null || head.Size != job.TotalBytes || string.IsNullOrWhiteSpace(head.ETag))
            return Failure(job, TransferFailureKind.Integrity, "DestinationSizeMismatch");
        foreach (var part in plan)
        {
            await using var body = await destination.OpenRangeAsync(spec.Destination.Bucket,
                spec.Destination.Key, part.Offset, part.Length, head.ETag, null, cancellationToken);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[81_920];
            long received = 0;
            while (received < part.Length)
            {
                var read = await body.Content.ReadAsync(buffer.AsMemory(0,
                    (int)Math.Min(buffer.Length, part.Length - received)), cancellationToken);
                if (read == 0)
                    return Failure(job, TransferFailureKind.Integrity, "DestinationTruncated");
                hash.AppendData(buffer.AsSpan(0, read));
                received += read;
            }
            if (!string.Equals(Convert.ToHexString(hash.GetHashAndReset()),
                completed[part.Number].Sha256, StringComparison.OrdinalIgnoreCase))
                return Failure(job, TransferFailureKind.Integrity, "DestinationChecksumMismatch");
        }
        if (!SameIdentity(head, await destination.HeadAsync(spec.Destination.Bucket, spec.Destination.Key, cancellationToken)))
            return Failure(job, TransferFailureKind.Conflict, "DestinationChanged");
        return job with { State = TransferState.Completed, BytesTransferred = head.Size,
            UploadId = null, CrossTransfer = spec with { Verification = "SHA256" }, UpdatedAt = clock.UtcNow };
    }

    private static async Task<CrossPartBuffer> ReadPartAsync(IRemoteTransferLease source,
        StorageObjectRef reference, string etag, TransferPart part,
        long memoryLimit, CrossPartBudget tempBudget, CancellationToken cancellationToken)
    {
        await using var body = await source.OpenRangeAsync(reference.Bucket, reference.Key,
            part.Offset, part.Length, etag, reference.VersionId, cancellationToken);
        return await CrossPartBuffer.ReadAsync(body.Content, part.Length,
            memoryLimit, tempBudget, cancellationToken);
    }

    private async Task<string?> FindFreeNameAsync(IRemoteTransferLease destination,
        StorageObjectRef reference, CancellationToken cancellationToken)
    {
        for (var index = 1; index <= options.MaxRenameAttempts; index++)
        {
            var candidate = ConflictNames.WithSuffix(reference.Key, index);
            if (await destination.HeadAsync(reference.Bucket, candidate, cancellationToken) is null)
                return candidate;
        }
        return null;
    }

    private static bool SameIdentity(RemoteIdentity? left, RemoteIdentity? right) =>
        left is null ? right is null : right is not null && left.Size == right.Size
            && (left.VersionId is null || left.VersionId == right.VersionId)
            && !string.IsNullOrWhiteSpace(left.ETag)
            && string.Equals(RemoteIdentity.Normalize(left.ETag),
                RemoteIdentity.Normalize(right.ETag ?? ""), StringComparison.OrdinalIgnoreCase);

    private static TransferJob Failure(TransferJob job, TransferFailureKind kind, string code) =>
        job with { State = TransferState.Failed,
            Failure = new TransferFailure(kind, false, code, code), UpdatedAt = DateTimeOffset.UtcNow };
}
