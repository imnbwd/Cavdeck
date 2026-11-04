using Cavdeck.Domain.Transfers;

namespace Cavdeck.Application.Transfers;

public sealed class MemoryTransferJournal : ITransferJournal
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, TransferJob> _jobs = [];
    private readonly Dictionary<Guid, TransferPart[]> _parts = [];
    private readonly Dictionary<Guid, CrossTransferBatch> _batches = [];
    private readonly Dictionary<(Guid BatchId, string DestinationKey), string> _crossMappings = [];

    public Task<TransferHistoryRemoval> RemoveTerminalAsync(
        TransferHistoryScope scope, Guid? id, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var removed = _jobs.Values.Where(job => TransferHistoryRules.CanRemove(job, scope, id,
                job.CrossTransfer is not null && _batches.TryGetValue(job.BatchId, out var batch)
                    && batch.State == CrossBatchState.Scanning)).Select(job => job.Id).ToArray();
            var affectedBatches = new HashSet<Guid>();
            foreach (var jobId in removed)
            {
                var job = _jobs[jobId];
                _jobs.Remove(jobId);
                _parts.Remove(jobId);
                if (job.CrossTransfer is not null)
                {
                    _crossMappings.Remove((job.BatchId, job.CrossTransfer.Destination.Key));
                    affectedBatches.Add(job.BatchId);
                }
            }
            var removedBatches = new List<Guid>();
            foreach (var batchId in affectedBatches)
            {
                if (_batches.TryGetValue(batchId, out var batch)
                    && batch.State != CrossBatchState.Scanning
                    && !_crossMappings.Keys.Any(key => key.BatchId == batchId)
                    && !_jobs.Values.Any(job => job.BatchId == batchId))
                {
                    _batches.Remove(batchId);
                    removedBatches.Add(batchId);
                }
            }
            return Task.FromResult(new TransferHistoryRemoval(removed, removedBatches));
        }
    }

    public Task UpsertBatchAsync(CrossTransferBatch batch, CancellationToken cancellationToken)
    {
        lock (_gate)
            _batches[batch.Id] = batch;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<CrossTransferBatch>> LoadUnfinishedBatchesAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
            return Task.FromResult<IReadOnlyList<CrossTransferBatch>>(
                _batches.Values.Where(batch => batch.State == CrossBatchState.Scanning).ToList());
    }

    public Task<IReadOnlyList<CrossTransferBatch>> LoadRecentBatchesAsync(int limit, CancellationToken cancellationToken)
    {
        lock (_gate)
            return Task.FromResult<IReadOnlyList<CrossTransferBatch>>(_batches.Values
                .OrderByDescending(batch => batch.UpdatedAt).Take(limit).ToList());
    }

    public Task<bool> TryCreateCrossJobAsync(TransferJob job, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var target = (job.BatchId, job.CrossTransfer!.Destination.Key);
            if (_crossMappings.TryGetValue(target, out var previous))
            {
                if (!string.Equals(previous, job.CrossTransfer.Source.Key, StringComparison.Ordinal))
                    throw new InvalidOperationException("Two source objects map to the same destination key.");
                return Task.FromResult(false);
            }
            _crossMappings[target] = job.CrossTransfer.Source.Key;
            _jobs[job.Id] = job;
            return Task.FromResult(true);
        }
    }

    public Task<CrossBatchProgress> GetBatchProgressAsync(Guid batchId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var jobs = _jobs.Values.Where(job => job.BatchId == batchId && job.Kind == TransferKind.Copy).ToList();
            return Task.FromResult(new CrossBatchProgress(jobs.Count,
                jobs.Count(job => job.State == TransferState.Completed && !job.Skipped),
                jobs.Count(job => job.State == TransferState.Completed && job.Skipped),
                jobs.Count(job => job.State == TransferState.Failed),
                jobs.Count(job => job.State == TransferState.Cancelled),
                jobs.Count(job => job.IsOpen)));
        }
    }

    public Task<IReadOnlyList<TransferJob>> LoadFailedBatchJobsAsync(Guid batchId, string? afterId,
        int limit, CancellationToken cancellationToken)
    {
        lock (_gate)
            return Task.FromResult<IReadOnlyList<TransferJob>>(_jobs.Values
                .Where(job => job.BatchId == batchId && job.Kind == TransferKind.Copy
                    && job.State == TransferState.Failed
                    && (afterId is null || string.CompareOrdinal(job.Id.ToString("N"), afterId) > 0))
                .OrderBy(job => job.Id.ToString("N"), StringComparer.Ordinal)
                .Take(limit).ToList());
    }

    public Task UpsertAsync(TransferJob job, CancellationToken cancellationToken)
    {
        lock (_gate)
            _jobs[job.Id] = job;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<TransferJob>> LoadRecentAsync(int limit, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            IReadOnlyList<TransferJob> jobs = _jobs.Values
                .OrderByDescending(job => job.UpdatedAt)
                .Take(Math.Max(1, limit))
                .ToList();
            return Task.FromResult(jobs);
        }
    }

    public Task<IReadOnlyList<TransferJob>> LoadUnfinishedAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            IReadOnlyList<TransferJob> jobs = _jobs.Values
                .Where(job => job.IsOpen || job.CrossTransfer?.CleanupPending == true).ToList();
            return Task.FromResult(jobs);
        }
    }

    public Task UpsertPartAsync(Guid jobId, TransferPart part, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var parts = _parts.TryGetValue(jobId, out var stored)
                ? stored.ToDictionary(item => item.Number)
                : new Dictionary<int, TransferPart>();
            parts[part.Number] = part;
            _parts[jobId] = parts.Values.OrderBy(item => item.Number).ToArray();
        }
        return Task.CompletedTask;
    }

    public Task ClearPartsAsync(Guid jobId, CancellationToken cancellationToken)
    {
        lock (_gate)
            _parts.Remove(jobId);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<TransferPart>> GetPartsAsync(Guid jobId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            IReadOnlyList<TransferPart> parts = _parts.TryGetValue(jobId, out var stored) ? stored : [];
            return Task.FromResult(parts);
        }
    }
}
