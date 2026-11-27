using Amazon.S3;
using Amazon.S3.Model;
using Cavdeck.Domain.Storage;

namespace Cavdeck.Infrastructure.S3;

internal sealed partial class AwsS3Gateway
{
    public Task<ObjectMetadataSnapshot> ReadMetadataAsync(S3Session session, StorageConnection connection,
        string bucket, string key, CancellationToken token) =>
        Call(session, async client => await ReadMetadataCoreAsync(client, connection, bucket, key, token));

    public Task<ObjectMetadataSnapshot> UpdateMetadataAsync(S3Session session, StorageConnection connection,
        ObjectMetadataSnapshot expected, ObjectMetadataValues desired, CancellationToken token) =>
        Call(session, async client =>
        {
            var bucket = expected.Head.Bucket;
            var key = expected.Head.Key;
            ObjectAccess.Ensure(connection, bucket, key, true);
            if (ObjectMetadataRules.Validate(desired) != MetadataValidation.None)
                throw new ArgumentException("Invalid metadata.", nameof(desired));
            var current = await ReadMetadataCoreAsync(client, connection, bucket, key, token);
            if (!expected.SameRevision(current)) throw MetadataFailure(StorageFailureKind.PreconditionFailed, "MetadataChanged");
            if (current.EditBlock != MetadataEditBlock.None)
                throw MetadataFailure(StorageFailureKind.Unsupported, "MetadataEditBlocked");
            if (current.Values.SameAs(desired)) return current;

            var request = CreateMetadataCopy(connection.Provider, current, desired);
            token.ThrowIfCancellationRequested();
            try { await client.CopyObjectAsync(request, token); }
            catch (AmazonS3Exception ex) when ((int)ex.StatusCode == 409)
            { throw MetadataFailure(StorageFailureKind.PreconditionFailed, "MetadataChanged"); }

            // Never repeat a potentially committed copy automatically at application level.
            var updated = await ReadMetadataCoreAsync(client, connection, bucket, key, token);
            if (!updated.Values.SameAs(desired) || updated.Head.SizeBytes != current.Head.SizeBytes ||
                updated.StorageClass != current.StorageClass || updated.Encryption != current.Encryption ||
                updated.WebsiteRedirect != current.WebsiteRedirect)
                throw MetadataFailure(StorageFailureKind.Unknown, "MetadataVerificationFailed");
            return updated;
        });

    private static async Task<ObjectMetadataSnapshot> ReadMetadataCoreAsync(AmazonS3Client client,
        StorageConnection connection, string bucket, string key, CancellationToken token)
    {
        var source = await client.GetObjectMetadataAsync(new GetObjectMetadataRequest
            { BucketName = bucket, Key = key }, token);
        var custom = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in source.Metadata.Keys)
        {
            var shortName = name.StartsWith("x-amz-meta-", StringComparison.OrdinalIgnoreCase) ? name[11..] : name;
            custom[shortName] = source.Metadata[name];
        }
        var http = new ObjectHttpMetadata(source.Headers.ContentType, source.Headers.CacheControl,
            source.Headers.ContentDisposition, source.Headers.ContentEncoding,
            source.Headers.ContentLanguage, source.ExpiresString ?? source.Headers.Expires?.ToUniversalTime().ToString("r"));
        var encryption = source.ServerSideEncryptionMethod?.Value;
        var storage = source.StorageClass?.Value;
        if (string.IsNullOrEmpty(storage) && connection.Provider != StorageProviderKind.S3Compatible)
            storage = "STANDARD";
        var block = source.MissingMeta.GetValueOrDefault() > 0 ? MetadataEditBlock.Incomplete :
            !string.IsNullOrEmpty(source.ServerSideEncryptionCustomerProvidedKeyMD5) ||
            !string.IsNullOrEmpty(source.ServerSideEncryptionKeyManagementServiceKeyId) ||
            (encryption is not null && encryption != "AES256" && encryption != "None") ||
            source.ObjectLockMode is not null || source.ObjectLockRetainUntilDate is not null ||
            source.ObjectLockLegalHoldStatus?.Value == "ON" ||
            storage is not ("STANDARD" or "STANDARD_IA" or "ONEZONE_IA" or "REDUCED_REDUNDANCY")
                ? MetadataEditBlock.ProtectedObject : MetadataEditBlock.None;
        if (connection.Provider == StorageProviderKind.CloudflareR2 && !string.IsNullOrEmpty(source.WebsiteRedirectLocation))
            block = MetadataEditBlock.ProtectedObject;
        var snapshot = new ObjectMetadataSnapshot(
            ToHead(bucket, key, source.Headers.ContentLength, source.ETag, source.Headers.ContentType),
            source.LastModified is { } date ? new DateTimeOffset(date.ToUniversalTime()) : null,
            storage, encryption, source.VersionId, source.WebsiteRedirectLocation,
            new ObjectMetadataValues(http, custom), block);
        block = ObjectMetadataRules.BasicBlock(connection, snapshot);
        if (block == MetadataEditBlock.None)
            block = await CheckMetadataPolicyAsync(client, connection, bucket, encryption, token);
        return snapshot with { EditBlock = block };
    }

    private static async Task<MetadataEditBlock> CheckMetadataPolicyAsync(AmazonS3Client client,
        StorageConnection connection, string bucket, string? encryption, CancellationToken token)
    {
        if (connection.Provider == StorageProviderKind.CloudflareR2) return MetadataEditBlock.None;
        try
        {
            if (connection.Provider == StorageProviderKind.AwsS3)
            {
                // A self-copy resets ACLs. Permit only buckets where ACLs are disabled.
                var ownership = await client.GetBucketOwnershipControlsAsync(
                    new GetBucketOwnershipControlsRequest { BucketName = bucket }, token);
                if (ownership.OwnershipControls?.Rules?.Any(rule =>
                    rule.ObjectOwnership?.Value == "BucketOwnerEnforced") != true || encryption != "AES256")
                    return MetadataEditBlock.ProtectedObject;
            }
            try
            {
                var locking = await client.GetObjectLockConfigurationAsync(
                    new GetObjectLockConfigurationRequest { BucketName = bucket }, token);
                if (locking.ObjectLockConfiguration?.ObjectLockEnabled?.Value == "Enabled")
                    return MetadataEditBlock.ProtectedObject;
            }
            catch (AmazonS3Exception ex) when (ex.ErrorCode == "ObjectLockConfigurationNotFoundError") { }

            if (connection.Provider == StorageProviderKind.Minio && encryption != "AES256")
            {
                // Do not let a bucket's default encryption change an unencrypted object.
                try
                {
                    await client.GetBucketEncryptionAsync(new GetBucketEncryptionRequest { BucketName = bucket }, token);
                    return MetadataEditBlock.ProtectedObject;
                }
                catch (AmazonS3Exception ex) when (ex.ErrorCode == "ServerSideEncryptionConfigurationNotFoundError") { }
            }
            return MetadataEditBlock.None;
        }
        catch (AmazonS3Exception)
        {
            // Listing and HEAD rights alone are insufficient to establish safe replacement.
            return MetadataEditBlock.UnverifiedPolicy;
        }
    }

    internal static CopyObjectRequest CreateMetadataCopy(StorageProviderKind provider,
        ObjectMetadataSnapshot current, ObjectMetadataValues desired)
    {
        var request = new CopyObjectRequest
        {
            SourceBucket = current.Head.Bucket, SourceKey = current.Head.Key,
            DestinationBucket = current.Head.Bucket, DestinationKey = current.Head.Key,
            MetadataDirective = S3MetadataDirective.REPLACE,
            ETagToMatch = current.Head.ETag,
            StorageClass = current.StorageClass,
            WebsiteRedirectLocation = current.WebsiteRedirect,
            ContentType = desired.Http.ContentType,
            Expires = desired.Http.Expires
        };
        if (provider == StorageProviderKind.CloudflareR2)
        {
            // R2 uses separate destination conditions; not the AWS If-Match header.
            request.Headers["cf-copy-destination-if-match"] = current.Head.ETag;
            request.Headers["cf-copy-destination-if-unmodified-since"] = current.LastModified?.UtcDateTime.ToString("r");
        }
        else
        {
            request.IfMatch = current.Head.ETag;
            request.TaggingDirective = TaggingDirective.COPY;
            if (current.Encryption == "AES256")
                request.ServerSideEncryptionMethod = ServerSideEncryptionMethod.AES256;
        }
        request.Headers.CacheControl = desired.Http.CacheControl;
        request.Headers.ContentDisposition = desired.Http.ContentDisposition;
        request.Headers.ContentEncoding = desired.Http.ContentEncoding;
        request.Headers.ContentLanguage = desired.Http.ContentLanguage;
        foreach (var pair in desired.Custom)
            request.Metadata.Add(pair.Key, pair.Value);
        return request;
    }

    private static StorageException MetadataFailure(StorageFailureKind kind, string code) =>
        new(new StorageFailure(kind, code, null, null, null));
}
