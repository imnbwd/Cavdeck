using Cavdeck.Domain.Storage;
using Cavdeck.Domain.Transfers;

namespace Cavdeck.Application.Transfers;

public sealed record ResumeRejection(Guid Id, string Reason, bool IsBatch = false);

public sealed partial class TransferEngine
{
    public async Task ResumeIsolatedRunAsync(IReadOnlyList<TransferJob> candidates,
        IReadOnlyList<CrossTransferBatch> batches, ICollection<ResumeRejection> rejections,
        Action starting, CancellationToken cancellationToken)
    {
        if (!_options.IsolatedRun || Interlocked.Exchange(ref _started, 1) == 1)
            throw new InvalidOperationException("An isolated engine can start only one run.");

        var accepted = new List<TransferJob>();
        foreach (var job in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var reason = await ResumeRejectionAsync(job, cancellationToken);
            if (reason is null)
                accepted.Add(job);
            else
                rejections.Add(new ResumeRejection(job.Id, reason));
        }
        var pending = new List<CrossTransferBatch>();
        foreach (var batch in batches)
        {
            if (batch.State != CrossBatchState.Scanning &&
                (batch.State != CrossBatchState.Failed || !batch.Retryable))
                continue;
            var source = await ResolveAsync(batch.SourceConnectionId, cancellationToken);
            var destination = await ResolveAsync(batch.DestinationConnectionId, cancellationToken);
            if (!batch.ExactTargets || batch.SourceConfiguration is null || batch.DestinationConfiguration is null ||
                source is null || destination is null ||
                !CrossTransferPlanner.MatchesConfiguration(batch.SourceConfiguration, source) ||
                !CrossTransferPlanner.MatchesConfiguration(batch.DestinationConfiguration, destination))
            {
                rejections.Add(new ResumeRejection(batch.Id, "BatchConfigurationUnavailable", IsBatch: true));
                continue;
            }
            pending.Add(batch);
        }

        if (accepted.Count == 0 && pending.Count == 0)
            return;

        starting();
        foreach (var job in accepted)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var queued = job with
            {
                State = TransferState.Queued,
                Attempt = 0,
                Failure = null,
                NextAttemptAt = null,
                UpdatedAt = _clock.UtcNow
            };
            await _journal.UpsertAsync(queued, cancellationToken);
            lock (_gate)
                _jobs.Add(queued.Id, queued);
            RaiseChanged(queued);
            _ready.Writer.TryWrite(queued.Id);
        }
        _pump = Task.Run(() => PumpAsync(_stop.Token));
        TrackBackground(_pump);
        foreach (var batch in pending)
        {
            var scanning = batch with { State = CrossBatchState.Scanning, Error = null, Retryable = false, UpdatedAt = _clock.UtcNow };
            await PublishBatchAsync(scanning, cancellationToken);
            await ScanBatchSafelyAsync(scanning, null, cancellationToken);
        }
    }

    private async Task<string?> ResumeRejectionAsync(TransferJob job, CancellationToken token)
    {
        if (job.State is TransferState.Completed or TransferState.Cancelled)
            return "TerminalJob";
        if (job.State == TransferState.Failed && job.Failure?.Retryable != true)
            return "FailureNotRetryable";
        if (!job.IsOpen && job.State != TransferState.Failed)
            return "UnknownJobState";
        if (job.Failure?.CleanupPending == true || job.CrossTransfer?.CleanupPending == true)
            return "CleanupPending";

        var destination = await ResolveAsync(job.ConnectionId, token);
        if (destination is null)
            return "ConnectionMissing";
        var configuration = job.CrossTransfer?.DestinationConfiguration ?? job.ConnectionConfiguration;
        if (configuration is null || !CrossTransferPlanner.MatchesConfiguration(configuration, destination))
            return "ConnectionConfigurationChanged";
        try
        {
            ObjectAccess.Ensure(destination, job.Bucket, job.Key, write: job.Kind != TransferKind.Download);
            if (job.CrossTransfer is { } cross)
            {
                var source = await ResolveAsync(cross.Source.ConnectionId, token);
                if (source is null)
                    return "ConnectionMissing";
                if (cross.SourceConfiguration is null ||
                    !CrossTransferPlanner.MatchesConfiguration(cross.SourceConfiguration, source))
                    return "ConnectionConfigurationChanged";
                ObjectAccess.Ensure(source, cross.Source.Bucket, cross.Source.Key, write: cross.Move);
            }
            else if (job.Kind is not (TransferKind.Upload or TransferKind.Download))
                return "UnsupportedTransfer";
        }
        catch (StorageException)
        {
            return "ConnectionAccessChanged";
        }
        return null;
    }
}
