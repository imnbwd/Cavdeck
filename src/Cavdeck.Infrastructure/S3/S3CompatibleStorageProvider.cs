using Cavdeck.Application.Abstractions;
using Cavdeck.Domain.Storage;

namespace Cavdeck.Infrastructure.S3;

public sealed class S3CompatibleStorageProvider : IObjectStorageProvider
{
    private readonly ICredentialVault _vault;
    private readonly IS3Gateway _gateway;

    internal S3CompatibleStorageProvider(
        StorageProviderKind kind, ICredentialVault vault, IS3Gateway gateway)
    {
        Kind = kind;
        _vault = vault;
        _gateway = gateway;
        Capabilities = ConnectionPlanner.Capabilities(kind);
    }

    public StorageProviderKind Kind { get; }
    public ProviderCapabilities Capabilities { get; }

    public async Task<IReadOnlyList<StorageBucket>> ListBucketsAsync(
        StorageConnection connection, CancellationToken cancellationToken)
    {
        EnsureKind(connection);
        if (!string.IsNullOrEmpty(connection.RestrictedBucket))
            return [new StorageBucket(connection.RestrictedBucket, connection.Region)];

        var session = await OpenAsync(connection, cancellationToken);
        var names = await _gateway.ListBucketNamesAsync(session, cancellationToken);
        return names.Select(name => new StorageBucket(name, null)).ToList();
    }

    public async Task<StoragePage<StorageObject>> ListObjectsAsync(
        StorageConnection connection, string bucket, string prefix,
        string? continuationToken, int pageSize, CancellationToken cancellationToken)
    {
        EnsureKind(connection);
        ObjectAccess.Ensure(connection, bucket, null, write: false);
        var effectivePrefix = ObjectAccess.CombinePrefix(connection, prefix);
        var session = await OpenAsync(connection, cancellationToken);
        return await _gateway.ListObjectsAsync(
            session, bucket, effectivePrefix, continuationToken,
            Math.Clamp(pageSize, 1, 1000), cancellationToken);
    }

    public async Task<PrefixPage> ListPrefixAsync(
        StorageConnection connection, string bucket, string prefix,
        string? continuationToken, int pageSize, CancellationToken cancellationToken)
    {
        EnsureKind(connection);
        ObjectAccess.Ensure(connection, bucket, null, write: false);
        var effectivePrefix = ObjectLayout.NormalizePrefix(ObjectAccess.CombinePrefix(connection, prefix));
        var session = await OpenAsync(connection, cancellationToken);
        return await _gateway.ListPrefixAsync(
            session, bucket, effectivePrefix, continuationToken,
            Math.Clamp(pageSize, 1, 1000), cancellationToken);
    }

    public async Task<bool> ObjectExistsAsync(
        StorageConnection connection, string bucket, string key, CancellationToken cancellationToken)
    {
        EnsureObject(connection, bucket, key, write: false);
        var session = await OpenAsync(connection, cancellationToken);
        return await _gateway.ObjectExistsAsync(session, bucket, key, cancellationToken);
    }

    public async Task CopyObjectAsync(
        StorageConnection connection,
        string sourceBucket, string sourceKey,
        string destinationBucket, string destinationKey,
        CancellationToken cancellationToken)
    {
        EnsureKind(connection);
        EnsureObject(connection, destinationBucket, destinationKey, write: true);
        EnsureObject(connection, sourceBucket, sourceKey, write: false);
        var session = await OpenAsync(connection, cancellationToken);
        await _gateway.CopyObjectAsync(
            session, sourceBucket, sourceKey, destinationBucket, destinationKey, cancellationToken);
    }

    public async Task UploadAsync(
        StorageConnection connection, string bucket, string key, Stream content, string? contentType,
        IProgress<long>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        EnsureObject(connection, bucket, key, write: true);
        var session = await OpenAsync(connection, cancellationToken);
        await _gateway.UploadAsync(session, bucket, key, content, contentType, progress, cancellationToken);
    }

    public async Task DownloadAsync(
        StorageConnection connection, string bucket, string key, Stream destination,
        IProgress<long>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(destination);
        EnsureObject(connection, bucket, key, write: false);
        var session = await OpenAsync(connection, cancellationToken);
        await _gateway.DownloadAsync(session, bucket, key, destination, progress, cancellationToken);
    }

    public async Task DeleteAsync(
        StorageConnection connection, string bucket, string key, CancellationToken cancellationToken)
    {
        EnsureObject(connection, bucket, key, write: true);
        var session = await OpenAsync(connection, cancellationToken);
        await _gateway.DeleteObjectAsync(session, bucket, key, cancellationToken);
    }

    public async Task DeleteIfMatchAsync(
        StorageConnection connection, string bucket, string key, string expectedETag,
        CancellationToken cancellationToken)
    {
        EnsureObject(connection, bucket, key, write: true);
        if (string.IsNullOrWhiteSpace(expectedETag))
            throw new ArgumentException("An ETag is required.", nameof(expectedETag));
        var session = await OpenAsync(connection, cancellationToken);
        await _gateway.DeleteObjectIfMatchAsync(session, bucket, key, expectedETag, cancellationToken);
    }

    public async Task<ObjectHead> HeadObjectAsync(
        StorageConnection connection, string bucket, string key, CancellationToken cancellationToken)
    {
        EnsureObject(connection, bucket, key, write: false);
        var session = await OpenAsync(connection, cancellationToken);
        return await _gateway.HeadObjectAsync(session, bucket, key, cancellationToken);
    }

    public async Task<ObjectBody> ReadObjectAsync(
        StorageConnection connection, string bucket, string key, int maxBytes, CancellationToken cancellationToken)
    {
        EnsureObject(connection, bucket, key, write: false);
        var session = await OpenAsync(connection, cancellationToken);
        return await _gateway.ReadObjectAsync(session, bucket, key, maxBytes, cancellationToken);
    }

    public async Task<ObjectHead> ReplaceObjectAsync(
        StorageConnection connection, string bucket, string key, Stream content, string? contentType,
        string? ifMatchETag, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        EnsureObject(connection, bucket, key, write: true);
        var session = await OpenAsync(connection, cancellationToken);
        return await _gateway.ReplaceObjectAsync(
            session, bucket, key, content, contentType, ifMatchETag, cancellationToken);
    }

    private async Task<S3Session> OpenAsync(StorageConnection connection, CancellationToken cancellationToken)
    {
        var stored = await _vault.ReadAsync(connection.CredentialReference, cancellationToken);
        if (stored is null || string.IsNullOrEmpty(stored.Value.SecretAccessKey))
            throw new StorageException(new StorageFailure(
                StorageFailureKind.CredentialMissing, null, null, null, null));

        return new S3Session
        {
            ConnectionId = connection.Id,
            AccessKeyId = stored.Value.AccessKeyId,
            SecretAccessKey = stored.Value.SecretAccessKey,
            Region = connection.Region,
            ServiceUrl = string.IsNullOrEmpty(connection.Endpoint) ? null : connection.Endpoint,
            ForcePathStyle = connection.ForcePathStyle
        };
    }

    private void EnsureKind(StorageConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (connection.Provider != Kind)
            throw new StorageException(new StorageFailure(
                StorageFailureKind.InvalidConfiguration, "Provider", null, null, null));
    }

    private void EnsureObject(StorageConnection connection, string bucket, string key, bool write)
    {
        EnsureKind(connection);
        if (string.IsNullOrWhiteSpace(key))
            throw new StorageException(new StorageFailure(
                StorageFailureKind.InvalidConfiguration, "Key", null, null, null));
        ObjectAccess.Ensure(connection, bucket, key, write);
    }
}
