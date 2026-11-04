using System.Buffers;
using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Cavdeck.Domain.Storage;

namespace Cavdeck.Infrastructure.S3;

internal interface IS3Gateway
{
    Task<IReadOnlyList<string>> ListBucketNamesAsync(S3Session session, CancellationToken cancellationToken);
    Task HeadBucketAsync(S3Session session, string bucket, CancellationToken cancellationToken);
    Task<StoragePage<StorageObject>> ListObjectsAsync(
        S3Session session, string bucket, string prefix, string? continuationToken, int pageSize,
        CancellationToken cancellationToken);
    Task<PrefixPage> ListPrefixAsync(
        S3Session session, string bucket, string prefix, string? continuationToken, int pageSize,
        CancellationToken cancellationToken);
    Task<bool> ObjectExistsAsync(S3Session session, string bucket, string key, CancellationToken cancellationToken);
    Task CopyObjectAsync(
        S3Session session, string sourceBucket, string sourceKey, string destinationBucket, string destinationKey,
        CancellationToken cancellationToken);
    Task UploadAsync(
        S3Session session, string bucket, string key, Stream content, string? contentType,
        IProgress<long>? progress, CancellationToken cancellationToken);
    Task DownloadAsync(
        S3Session session, string bucket, string key, Stream destination,
        IProgress<long>? progress, CancellationToken cancellationToken);
    Task DeleteObjectAsync(S3Session session, string bucket, string key, CancellationToken cancellationToken);
    Task DeleteObjectIfMatchAsync(S3Session session, string bucket, string key,
        string expectedETag, CancellationToken cancellationToken) =>
        throw new NotSupportedException("This adapter does not implement conditional deletion.");
    Task<ObjectHead> HeadObjectAsync(S3Session session, string bucket, string key, CancellationToken cancellationToken);
    Task<ObjectBody> ReadObjectAsync(
        S3Session session, string bucket, string key, int maxBytes, CancellationToken cancellationToken);
    Task<ObjectHead> ReplaceObjectAsync(
        S3Session session, string bucket, string key, Stream content, string? contentType, string? ifMatchETag,
        CancellationToken cancellationToken);
}

// One S3 client covers AWS, R2 and MinIO. Checksums stay off unless the API
// requires them: the SDK default breaks R2 and MinIO signatures.
internal sealed class AwsS3Gateway : IS3Gateway, IDisposable
{
    private readonly long _singleCopyLimit;
    private readonly S3ClientLeasePool _clients;
    private readonly bool _ownsClients;

    public AwsS3Gateway(long singleCopyLimit = 5L * 1024 * 1024 * 1024)
        : this(new S3ClientLeasePool(), singleCopyLimit, ownsClients: true) { }

    internal AwsS3Gateway(S3ClientLeasePool clients, long singleCopyLimit = 5L * 1024 * 1024 * 1024)
        : this(clients, singleCopyLimit, ownsClients: false) { }

    private AwsS3Gateway(S3ClientLeasePool clients, long singleCopyLimit, bool ownsClients)
    {
        ArgumentNullException.ThrowIfNull(clients);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(singleCopyLimit);
        _clients = clients;
        _ownsClients = ownsClients;
        _singleCopyLimit = singleCopyLimit;
    }

    public void Dispose()
    {
        if (_ownsClients)
            _clients.Dispose();
    }

    public Task<IReadOnlyList<string>> ListBucketNamesAsync(
        S3Session session, CancellationToken cancellationToken) =>
        Call(session, async client =>
        {
            var response = await client.ListBucketsAsync(cancellationToken);
            return (IReadOnlyList<string>)(response.Buckets ?? [])
                .Select(bucket => bucket.BucketName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name!)
                .ToList();
        });

    public Task HeadBucketAsync(S3Session session, string bucket, CancellationToken cancellationToken) =>
        Call(session, client => client.HeadBucketAsync(
            new HeadBucketRequest { BucketName = bucket }, cancellationToken));

    public Task<StoragePage<StorageObject>> ListObjectsAsync(
        S3Session session, string bucket, string prefix, string? continuationToken, int pageSize,
        CancellationToken cancellationToken) =>
        Call(session, async client =>
        {
            var response = await client.ListObjectsV2Async(new ListObjectsV2Request
            {
                BucketName = bucket,
                Prefix = prefix,
                ContinuationToken = continuationToken,
                MaxKeys = pageSize
            }, cancellationToken);

            var items = (response.S3Objects ?? []).Select(item => new StorageObject(
                bucket,
                item.Key ?? "",
                item.Size ?? 0,
                ToOffset(item.LastModified),
                item.ETag,
                null)).ToList();
            var next = response.IsTruncated == true ? response.NextContinuationToken : null;
            return new StoragePage<StorageObject>(items, next);
        });

    public Task<PrefixPage> ListPrefixAsync(
        S3Session session, string bucket, string prefix, string? continuationToken, int pageSize,
        CancellationToken cancellationToken) =>
        Call(session, async client =>
        {
            var response = await client.ListObjectsV2Async(new ListObjectsV2Request
            {
                BucketName = bucket,
                Prefix = prefix,
                Delimiter = "/",
                ContinuationToken = continuationToken,
                MaxKeys = pageSize
            }, cancellationToken);

            var folders = response.CommonPrefixes ?? [];
            var objects = (response.S3Objects ?? []).Select(item => new StorageObject(
                bucket,
                item.Key ?? "",
                item.Size ?? 0,
                ToOffset(item.LastModified),
                item.ETag,
                null)).ToList();
            var next = response.IsTruncated == true ? response.NextContinuationToken : null;
            return new PrefixPage(folders, objects, next);
        });

    public Task<bool> ObjectExistsAsync(
        S3Session session, string bucket, string key, CancellationToken cancellationToken) =>
        Call(session, async client =>
        {
            try
            {
                await client.GetObjectMetadataAsync(new GetObjectMetadataRequest
                {
                    BucketName = bucket,
                    Key = key
                }, cancellationToken);
                return true;
            }
            catch (AmazonS3Exception exception) when (
                (int)exception.StatusCode == 404
                || exception.ErrorCode is "NotFound" or "NoSuchKey")
            {
                return false;
            }
        });

    public Task CopyObjectAsync(
        S3Session session, string sourceBucket, string sourceKey, string destinationBucket, string destinationKey,
        CancellationToken cancellationToken) =>
        Call(session, async client =>
        {
            var source = await client.GetObjectMetadataAsync(new GetObjectMetadataRequest
            {
                BucketName = sourceBucket,
                Key = sourceKey
            }, cancellationToken);
            if (source.Headers.ContentLength <= _singleCopyLimit)
            {
                await client.CopyObjectAsync(new CopyObjectRequest
                {
                    SourceBucket = sourceBucket,
                    SourceKey = sourceKey,
                    DestinationBucket = destinationBucket,
                    DestinationKey = destinationKey,
                    MetadataDirective = S3MetadataDirective.COPY
                }, cancellationToken);
                return;
            }

            // S3 CopyObject has a 5 GiB source limit. Multipart copy keeps data on the server.
            var length = source.Headers.ContentLength;
            var initiate = new InitiateMultipartUploadRequest
            {
                BucketName = destinationBucket,
                Key = destinationKey,
                ContentType = source.Headers.ContentType,
                StorageClass = source.StorageClass,
                WebsiteRedirectLocation = source.WebsiteRedirectLocation
            };
            initiate.Headers.CacheControl = source.Headers.CacheControl;
            initiate.Headers.ContentDisposition = source.Headers.ContentDisposition;
            initiate.Headers.ContentEncoding = source.Headers.ContentEncoding;
            initiate.Headers.ContentLanguage = source.Headers.ContentLanguage;
            initiate.Headers.Expires = source.Headers.Expires;
            foreach (var key in source.Metadata.Keys)
                initiate.Metadata.Add(key, source.Metadata[key]);
            var tagging = await client.GetObjectTaggingAsync(new GetObjectTaggingRequest
            {
                BucketName = sourceBucket,
                Key = sourceKey
            }, cancellationToken);
            if (tagging.Tagging is { Count: > 0 })
                initiate.TagSet = tagging.Tagging;
            var started = await client.InitiateMultipartUploadAsync(initiate, cancellationToken);
            try
            {
                var parts = new List<PartETag>();
                foreach (var (first, last) in MultipartCopyPlan.Ranges(length))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var number = parts.Count + 1;
                    var copied = await client.CopyPartAsync(new CopyPartRequest
                    {
                        SourceBucket = sourceBucket,
                        SourceKey = sourceKey,
                        DestinationBucket = destinationBucket,
                        DestinationKey = destinationKey,
                        UploadId = started.UploadId,
                        PartNumber = number,
                        FirstByte = first,
                        LastByte = last,
                        ETagToMatch = string.IsNullOrEmpty(source.ETag) ? null : [source.ETag]
                    }, cancellationToken);
                    parts.Add(new PartETag(number, copied.ETag));
                }
                await client.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
                {
                    BucketName = destinationBucket,
                    Key = destinationKey,
                    UploadId = started.UploadId,
                    PartETags = parts
                }, cancellationToken);
            }
            catch
            {
                try
                {
                    await client.AbortMultipartUploadAsync(new AbortMultipartUploadRequest
                    {
                        BucketName = destinationBucket,
                        Key = destinationKey,
                        UploadId = started.UploadId
                    }, CancellationToken.None);
                }
                catch { /* Preserve the original copy failure. */ }
                throw;
            }
        });

    public Task UploadAsync(
        S3Session session, string bucket, string key, Stream content, string? contentType,
        IProgress<long>? progress, CancellationToken cancellationToken) =>
        Call(session, client =>
        {
            var request = new PutObjectRequest
            {
                BucketName = bucket,
                Key = key,
                InputStream = content,
                AutoCloseStream = false,
                AutoResetStreamPosition = false,
                ContentType = string.IsNullOrWhiteSpace(contentType) ? null : contentType,
                UseChunkEncoding = content.CanSeek ? false : true
            };
            if (session.ServiceUrl?.StartsWith("https://", StringComparison.OrdinalIgnoreCase) == true)
                request.DisablePayloadSigning = true;
            if (progress is not null)
                request.StreamTransferProgress += (_, args) => progress.Report(args.TransferredBytes);
            return client.PutObjectAsync(request, cancellationToken);
        });

    public Task DownloadAsync(
        S3Session session, string bucket, string key, Stream destination,
        IProgress<long>? progress, CancellationToken cancellationToken) =>
        Call(session, async client =>
        {
            using var response = await client.GetObjectAsync(new GetObjectRequest
            {
                BucketName = bucket,
                Key = key
            }, cancellationToken);

            var buffer = ArrayPool<byte>.Shared.Rent(81_920);
            try
            {
                long total = 0;
                while (true)
                {
                    var read = await response.ResponseStream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
                    if (read == 0)
                        break;
                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    total += read;
                    progress?.Report(total);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        });

    public Task DeleteObjectAsync(
        S3Session session, string bucket, string key, CancellationToken cancellationToken) =>
        Call(session, client => client.DeleteObjectAsync(new DeleteObjectRequest
        {
            BucketName = bucket,
            Key = key
        }, cancellationToken));

    public Task DeleteObjectIfMatchAsync(S3Session session, string bucket, string key,
        string expectedETag, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(expectedETag))
            throw new ArgumentException("Conditional delete requires an ETag.", nameof(expectedETag));
        return Call(session, async client =>
        {
            await client.DeleteObjectAsync(new DeleteObjectRequest
            {
                BucketName = bucket,
                Key = key,
                IfMatch = expectedETag
            }, cancellationToken);
        });
    }

    public Task<ObjectHead> HeadObjectAsync(
        S3Session session, string bucket, string key, CancellationToken cancellationToken) =>
        Call(session, async client =>
        {
            var response = await client.GetObjectMetadataAsync(new GetObjectMetadataRequest
            {
                BucketName = bucket,
                Key = key
            }, cancellationToken);
            return ToHead(
                bucket, key, response.Headers.ContentLength, response.ETag, response.Headers.ContentType);
        });

    public Task<ObjectBody> ReadObjectAsync(
        S3Session session, string bucket, string key, int maxBytes, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxBytes);
        return Call(session, async client =>
        {
            using var response = await client.GetObjectAsync(new GetObjectRequest
            {
                BucketName = bucket,
                Key = key
            }, cancellationToken);
            var head = ToHead(
                bucket, key, response.Headers.ContentLength, response.ETag, response.Headers.ContentType);
            var limit = (long)maxBytes + 1;
            using var buffer = new MemoryStream();
            var rented = ArrayPool<byte>.Shared.Rent(81_920);
            try
            {
                while (buffer.Length < limit)
                {
                    var want = (int)Math.Min(rented.Length, limit - buffer.Length);
                    var read = await response.ResponseStream.ReadAsync(rented.AsMemory(0, want), cancellationToken);
                    if (read == 0)
                        break;
                    buffer.Write(rented, 0, read);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }

            var truncated = buffer.Length > maxBytes;
            var bytes = buffer.ToArray();
            if (truncated)
                bytes = bytes[..maxBytes];
            return new ObjectBody(head, bytes, truncated);
        });
    }

    public Task<ObjectHead> ReplaceObjectAsync(
        S3Session session, string bucket, string key, Stream content, string? contentType, string? ifMatchETag,
        CancellationToken cancellationToken) =>
        Call(session, async client =>
        {
            S3ConditionalWritePolicies.For(session).EnsureConditionalReplaceSupported(ifMatchETag);

            var request = new PutObjectRequest
            {
                BucketName = bucket,
                Key = key,
                InputStream = content,
                AutoCloseStream = false,
                AutoResetStreamPosition = false,
                ContentType = string.IsNullOrWhiteSpace(contentType) ? null : contentType,
                UseChunkEncoding = !content.CanSeek,
                IfMatch = string.IsNullOrWhiteSpace(ifMatchETag) ? null : ifMatchETag
            };
            if (session.ServiceUrl?.StartsWith("https://", StringComparison.OrdinalIgnoreCase) == true)
                request.DisablePayloadSigning = true;
            var response = await client.PutObjectAsync(request, cancellationToken);
            var size = content.CanSeek ? content.Length : 0;
            return ToHead(bucket, key, size, response.ETag, contentType);
        });

    private static ObjectHead ToHead(string bucket, string key, long size, string? etag, string? contentType) =>
        new(
            bucket,
            key,
            size,
            string.IsNullOrWhiteSpace(etag) ? null : etag,
            Blank(contentType));

    private static string? Blank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private async Task<T> Call<T>(S3Session session, Func<AmazonS3Client, Task<T>> action)
    {
        using var lease = _clients.Acquire(session);
        var client = lease.Client;
        try
        {
            return await action(client);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw Map(exception, session);
        }
    }

    private async Task Call(S3Session session, Func<AmazonS3Client, Task> action)
    {
        using var lease = _clients.Acquire(session);
        var client = lease.Client;
        try
        {
            await action(client);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw Map(exception, session);
        }
    }

    internal static AmazonS3Client CreateClient(S3Session session)
    {
        var credentials = new BasicAWSCredentials(session.AccessKeyId, session.SecretAccessKey);
        var config = new AmazonS3Config
        {
            ForcePathStyle = session.ForcePathStyle,
            MaxErrorRetry = 0,
            Timeout = TimeSpan.FromMinutes(10),
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED
        };

        if (string.IsNullOrEmpty(session.ServiceUrl))
        {
            config.RegionEndpoint = RegionEndpoint.GetBySystemName(session.Region);
        }
        else
        {
            config.ServiceURL = session.ServiceUrl;
            config.AuthenticationRegion = session.Region;
            config.UseHttp = session.ServiceUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase);
        }

        return new AmazonS3Client(credentials, config);
    }

    internal static StorageException Map(Exception exception, S3Session session)
    {
        var secrets = new[] { session.AccessKeyId, session.SecretAccessKey };
        return exception switch
        {
            StorageException storage => storage,
            AmazonS3Exception s3 => new StorageException(StorageFailureClassifier.FromS3(
                s3.ErrorCode, (int)s3.StatusCode, s3.RequestId, s3.Message, secrets)),
            AmazonServiceException service => new StorageException(StorageFailureClassifier.FromS3(
                service.ErrorCode, (int)service.StatusCode, service.RequestId, service.Message, secrets)),
            _ => new StorageException(StorageFailureClassifier.FromTransport(
                exception.InnerException?.Message ?? exception.Message, secrets))
        };
    }

    private static DateTimeOffset ToOffset(DateTime? value)
    {
        if (value is null || value.Value == default)
            return DateTimeOffset.UnixEpoch;
        var specified = value.Value.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(value.Value, DateTimeKind.Utc)
            : value.Value.ToUniversalTime();
        return new DateTimeOffset(specified);
    }
}
