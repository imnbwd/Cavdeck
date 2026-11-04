using Cavdeck.Domain.Transfers;

namespace Cavdeck.App.Features.Explorer;

// The visible object list can represent either side of a cross-connection move.
// A completed move refreshes both tabs without retargeting navigation.
public static class ExplorerTransferRefreshRules
{
    public static bool DestinationAffected(TransferJob job, Guid connectionId, string bucket,
        string prefix) =>
        IsCompletedChange(job)
        && job.ConnectionId == connectionId && job.Bucket == bucket
        && ContainsKey(prefix, job.Key);

    public static bool SourceAffected(TransferJob job, Guid connectionId, string bucket,
        string prefix) =>
        IsCompletedChange(job)
        && job.CrossTransfer is { Move: true } cross
        && cross.Source.ConnectionId == connectionId && cross.Source.Bucket == bucket
        && ContainsKey(prefix, cross.Source.Key);

    private static bool IsCompletedChange(TransferJob job) =>
        job.State == TransferState.Completed && !job.Skipped;

    private static bool ContainsKey(string prefix, string key) =>
        key.StartsWith(prefix, StringComparison.Ordinal) && key.Length > prefix.Length;
}
