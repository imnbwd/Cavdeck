using Cavdeck.Domain.Storage;
using Cavdeck.Domain.Transfers;

namespace Cavdeck.Application.Transfers;

public interface ITransferClock
{
    DateTimeOffset UtcNow { get; }
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

public sealed class SystemTransferClock : ITransferClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
        Task.Delay(delay, cancellationToken);
}

public interface ITransferConnections
{
    Task<StorageConnection?> FindAsync(Guid id, CancellationToken cancellationToken);
    Task<IReadOnlyList<StorageConnection>> ListAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<StorageConnection>>([]);
}

public enum TransferHistoryScope { Single, Failed, Finished }

public sealed record TransferHistoryRemoval(
    IReadOnlyList<Guid> JobIds, IReadOnlyList<Guid> BatchIds);

public static class TransferHistoryRules
{
    public static bool CanRemove(TransferJob job, TransferHistoryScope scope, Guid? id = null,
        bool batchScanning = false) =>
        (job.State is TransferState.Completed or TransferState.Cancelled or TransferState.Failed)
        && job.UploadId is null
        && job.CrossTransfer?.CleanupPending != true
        && !batchScanning
        && (scope switch
        {
            TransferHistoryScope.Single => id == job.Id,
            TransferHistoryScope.Failed => job.State == TransferState.Failed,
            TransferHistoryScope.Finished => job.State is TransferState.Completed or TransferState.Cancelled,
            _ => false
        });
}

public interface ITransferJournal
{
    // Atomic, durable removal of terminal records only. Never deletes local or remote objects.
    Task<TransferHistoryRemoval> RemoveTerminalAsync(TransferHistoryScope scope, Guid? id,
        CancellationToken cancellationToken);
    Task UpsertAsync(TransferJob job, CancellationToken cancellationToken);
    Task<IReadOnlyList<TransferJob>> LoadRecentAsync(int limit, CancellationToken cancellationToken);
    Task<IReadOnlyList<TransferJob>> LoadUnfinishedAsync(CancellationToken cancellationToken);
    Task UpsertPartAsync(Guid jobId, TransferPart part, CancellationToken cancellationToken);
    Task ClearPartsAsync(Guid jobId, CancellationToken cancellationToken);
    Task<IReadOnlyList<TransferPart>> GetPartsAsync(Guid jobId, CancellationToken cancellationToken);
    Task UpsertBatchAsync(CrossTransferBatch batch, CancellationToken cancellationToken);
    Task<IReadOnlyList<CrossTransferBatch>> LoadUnfinishedBatchesAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<CrossTransferBatch>> LoadRecentBatchesAsync(int limit, CancellationToken cancellationToken);
    Task<bool> TryCreateCrossJobAsync(TransferJob job, CancellationToken cancellationToken);
    Task<CrossBatchProgress> GetBatchProgressAsync(Guid batchId, CancellationToken cancellationToken);
    Task<IReadOnlyList<TransferJob>> LoadFailedBatchJobsAsync(Guid batchId, string? afterId,
        int limit, CancellationToken cancellationToken);
}

public sealed record ListedObject(string Key, long Size, string? ETag);
public sealed record ListedPage(IReadOnlyList<ListedObject> Objects, string? ContinuationToken);

public sealed record DownloadItem(string Key, bool Folder, long? Size, string? DestinationKey = null);

public sealed class DownloadBody : IAsyncDisposable
{
    public required Stream Content { get; init; }
    public required RemoteIdentity Identity { get; init; }
    public required bool RangeHonored { get; init; }

    public async ValueTask DisposeAsync() => await Content.DisposeAsync();
}

public interface IRemoteTransferLease : IAsyncDisposable
{
    void EnsureConditionalMultipartSupported();

    Task<RemoteIdentity?> HeadAsync(string bucket, string key, CancellationToken cancellationToken);

    Task<RemoteIdentity> PutAsync(
        string bucket, string key, Stream content, long length, string? contentType,
        string contentMd5, IProgress<long>? progress, CancellationToken cancellationToken);

    Task<RemoteIdentity> PutConditionalAsync(
        string bucket, string key, Stream content, long length, string? contentType,
        string contentMd5, string? expectedETag, bool requireMissing,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException("Conditional writes are required for cross-connection copies.");

    Task<RemoteIdentity> PutConditionalAsync(
        string bucket, string key, Stream content, long length, string? contentType,
        string contentMd5, string? expectedETag, bool requireMissing,
        IProgress<long>? progress, CancellationToken cancellationToken) =>
        PutConditionalAsync(bucket, key, content, length, contentType, contentMd5,
            expectedETag, requireMissing, cancellationToken);

    Task<string> InitiateMultipartAsync(
        string bucket, string key, string? contentType, CancellationToken cancellationToken);

    Task<string> UploadPartAsync(
        string bucket, string key, string uploadId, int partNumber, Stream content, long length,
        string contentMd5, IProgress<long>? progress, CancellationToken cancellationToken);

    Task CompleteMultipartAsync(
        string bucket, string key, string uploadId, IReadOnlyList<TransferPart> parts,
        CancellationToken cancellationToken);

    Task CompleteMultipartConditionalAsync(
        string bucket, string key, string uploadId, IReadOnlyList<TransferPart> parts,
        string? expectedETag, bool requireMissing, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Conditional multipart completion is required for cross-connection copies.");

    Task AbortMultipartAsync(
        string bucket, string key, string uploadId, CancellationToken cancellationToken);

    Task<IReadOnlyList<TransferPart>> ListPartsAsync(
        string bucket, string key, string uploadId, CancellationToken cancellationToken);

    Task<DownloadBody> OpenDownloadAsync(
        string bucket, string key, long? rangeStart, CancellationToken cancellationToken);

    Task<DownloadBody> OpenRangeAsync(
        string bucket, string key, long offset, long length, string expectedETag,
        string? versionId, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Fixed range reads are required for cross-connection copies.");

    IAsyncEnumerable<ListedObject> ListAsync(string bucket, string prefix, CancellationToken cancellationToken);

    Task<ListedPage> ListPageAsync(string bucket, string prefix, string? continuationToken,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException("Paged listings are required for cross-connection folder copies.");

    Task DeleteAsync(string bucket, string key, CancellationToken cancellationToken);

    Task DeleteIfMatchAsync(string bucket, string key, string expectedETag,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException("Conditional deletion is required for safe cross-connection moves.");
}

public interface IRemoteObjectTransfer
{
    Task<IRemoteTransferLease> OpenAsync(
        StorageConnection connection, bool write, CancellationToken cancellationToken);
}

public sealed class TransferEngineOptions
{
    public int MaxConcurrentTransfers { get; init; } = 3;
    public int MaxConcurrentParts { get; init; } = 4;
    public long MultipartThresholdBytes { get; init; } = 8 * 1024 * 1024;
    public long PartSizeBytes { get; init; } = 8 * 1024 * 1024;
    public long MinimumPartBytes { get; init; } = TransferPlanner.S3MinimumPartSize;
    public int MaxAttempts { get; init; } = 5;
    public int MaxRenameAttempts { get; init; } = 50;
    public int MaxCrossMoveManifestObjects { get; init; } = 20_000;
    public long DownloadThresholdBytes { get; init; } = 128L * 1024 * 1024;
    public long DownloadPartSizeBytes { get; init; } = 16L * 1024 * 1024;
    public long CrossCopyPartSizeBytes { get; init; } = 32L * 1024 * 1024;
    public long CrossCopyMemoryBudgetBytes { get; init; } = 256L * 1024 * 1024;
    public long CrossCopyTempBudgetBytes { get; init; } = 1024L * 1024 * 1024;

    public TransferEngineOptions Clamp() => new()
    {
        MaxConcurrentTransfers = Math.Clamp(MaxConcurrentTransfers, 1, 8),
        MaxConcurrentParts = Math.Clamp(MaxConcurrentParts, 1, 8),
        MultipartThresholdBytes = Math.Max(0, MultipartThresholdBytes),
        PartSizeBytes = Math.Max(1, PartSizeBytes),
        MinimumPartBytes = Math.Max(1, MinimumPartBytes),
        MaxAttempts = Math.Clamp(MaxAttempts, 1, 20),
        MaxRenameAttempts = Math.Clamp(MaxRenameAttempts, 1, 200),
        MaxCrossMoveManifestObjects = Math.Clamp(MaxCrossMoveManifestObjects, 1, 200_000),
        DownloadThresholdBytes = Math.Max(1, DownloadThresholdBytes),
        DownloadPartSizeBytes = Math.Max(1, DownloadPartSizeBytes),
        CrossCopyPartSizeBytes = Math.Clamp(CrossCopyPartSizeBytes, MinimumPartBytes, 5L * 1024 * 1024 * 1024),
        CrossCopyMemoryBudgetBytes = Math.Max(CrossCopyMemoryBudgetBytes, MinimumPartBytes),
        CrossCopyTempBudgetBytes = Math.Max(CrossCopyTempBudgetBytes, MinimumPartBytes)
    };
}
