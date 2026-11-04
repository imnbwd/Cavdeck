using Cavdeck.Domain.Storage;

namespace Cavdeck.Application.Abstractions;

// In-memory probe input. ToString omits keys so a log line cannot leak them.
public sealed class StorageProbeRequest
{
    public required string Region { get; init; }
    public string? ServiceUrl { get; init; }
    public bool ForcePathStyle { get; init; }
    public required string AccessKeyId { get; init; }
    public required string SecretAccessKey { get; init; }
    public string? Bucket { get; init; }
    public string? KeyPrefix { get; init; }

    public override string ToString() =>
        $"probe region={Region} url={ServiceUrl ?? "aws"} bucket={Bucket ?? "*"}";
}

public sealed record StorageProbeResult(
    bool Succeeded,
    StorageFailure? Failure,
    int? BucketCount,
    string? ConfirmedBucket)
{
    public static StorageProbeResult Success(int? bucketCount, string? bucket) =>
        new(true, null, bucketCount, bucket);

    public static StorageProbeResult Failed(StorageFailure failure) =>
        new(false, failure, null, null);
}

public interface IStorageProbe
{
    Task<StorageProbeResult> ProbeAsync(
        StorageProbeRequest request, CancellationToken cancellationToken = default);
}
