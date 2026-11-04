using Cavdeck.Domain.Transfers;

namespace Cavdeck.Application.Transfers;

public enum CrossBatchState { Scanning, Completed, Failed }

public sealed record CrossBatchProgress(long Queued, long Completed, long Skipped,
    long Failed, long Cancelled, long Open)
{
    public long Finished => Math.Min(Queued, Completed + Skipped + Failed + Cancelled);
    public double Percent => Queued <= 0 ? 0 : 100d * Finished / Queued;
}

public sealed record CrossTransferBatch(
    Guid Id,
    Guid SourceConnectionId,
    Guid DestinationConnectionId,
    string SourceBucket,
    string DestinationBucket,
    string DestinationPrefix,
    IReadOnlyList<DownloadItem> Items,
    TransferConflictPolicy Conflict,
    int ScanIndex,
    string? ContinuationToken,
    string? LastKey,
    long ObjectsQueued,
    CrossBatchState State,
    string? Error,
    DateTimeOffset UpdatedAt,
    bool Move = false);
