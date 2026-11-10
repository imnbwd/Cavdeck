namespace Cavdeck.Domain.Storage;

public enum StorageProviderKind { AwsS3, CloudflareR2, Minio, S3Compatible }

// Endpoint is empty when Amazon's regional endpoint should be used.
// CredentialReference is a Credential Manager target, never a key.
public sealed record StorageConnection(
    Guid Id,
    string Name,
    StorageProviderKind Provider,
    string Endpoint,
    string Region,
    string CredentialReference,
    bool IsReadOnly = false,
    string? RestrictedBucket = null,
    string? KeyPrefix = null,
    bool ForcePathStyle = false,
    string? AccountId = null)
{
    // Changes when credentials or settings used to create an S3 client change.
    public long ClientRevision { get; init; }
}

public sealed record StorageBucket(string Name, string? Region);

public sealed record ObjectHead(
    string Bucket,
    string Key,
    long SizeBytes,
    string? ETag,
    string? ContentType);

public sealed record ObjectBody(ObjectHead Head, byte[] Content, bool Truncated);

public sealed record StorageObject(
    string Bucket,
    string Key,
    long SizeBytes,
    DateTimeOffset LastModified,
    string? ETag,
    string? ContentType);

public sealed record StoragePage<T>(IReadOnlyList<T> Items, string? ContinuationToken);

public sealed record ProviderCapabilities(
    bool CanListBuckets,
    bool CanPresignDownload,
    bool CanReadObjectVersions,
    bool CanEditObjectMetadata,
    bool CanUseServerSideCopy,
    bool CanManageBucketSettings);
