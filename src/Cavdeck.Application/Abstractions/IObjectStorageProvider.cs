using Cavdeck.Domain.Storage;

namespace Cavdeck.Application.Abstractions;

// Infrastructure adapters implement these S3 object operations.
// Streams allow bounded-memory transfers; no UI or SDK types cross this boundary.
public interface IObjectStorageProvider
{
    StorageProviderKind Kind { get; }
    ProviderCapabilities Capabilities { get; }

    Task<IReadOnlyList<StorageBucket>> ListBucketsAsync(
        StorageConnection connection, CancellationToken cancellationToken);

    Task<StoragePage<StorageObject>> ListObjectsAsync(
        StorageConnection connection, string bucket, string prefix,
        string? continuationToken, int pageSize,
        CancellationToken cancellationToken);

    // One level of a prefix. Common prefixes are the folders; objects are the files.
    Task<PrefixPage> ListPrefixAsync(
        StorageConnection connection, string bucket, string prefix,
        string? continuationToken, int pageSize,
        CancellationToken cancellationToken);

    Task<PrefixPage> ListExactPageAsync(StorageConnection connection, string bucket, string prefix,
        bool recursive, string? continuationToken, int pageSize, CancellationToken cancellationToken) =>
        throw new NotSupportedException("The provider does not support exact prefix listings.");

    Task<bool> ObjectExistsAsync(
        StorageConnection connection, string bucket, string key,
        CancellationToken cancellationToken);

    Task CopyObjectAsync(
        StorageConnection connection,
        string sourceBucket, string sourceKey,
        string destinationBucket, string destinationKey,
        CancellationToken cancellationToken);

    Task UploadAsync(
        StorageConnection connection, string bucket, string key,
        Stream content, string? contentType,
        IProgress<long>? progress, CancellationToken cancellationToken);

    Task DownloadAsync(
        StorageConnection connection, string bucket, string key,
        Stream destination, IProgress<long>? progress,
        CancellationToken cancellationToken);

    Task DeleteAsync(
        StorageConnection connection, string bucket, string key,
        CancellationToken cancellationToken);

    // Fail closed if a provider cannot atomically enforce an ETag precondition.
    // Needed when deleting sources after a move or confirmed preview.
    Task DeleteIfMatchAsync(StorageConnection connection, string bucket, string key,
        string expectedETag, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Conditional deletion is required for this operation.");

    Task<ObjectHead> HeadObjectAsync(
        StorageConnection connection, string bucket, string key,
        CancellationToken cancellationToken);

    // Reads at most maxBytes. Truncated is true when the object continues past that limit.
    Task<ObjectBody> ReadObjectAsync(
        StorageConnection connection, string bucket, string key, int maxBytes,
        CancellationToken cancellationToken);

    // ifMatchETag is the value captured when the object was opened. A mismatch must fail the write.
    Task<ObjectHead> ReplaceObjectAsync(
        StorageConnection connection, string bucket, string key, Stream content, string? contentType,
        string? ifMatchETag, CancellationToken cancellationToken);
}
