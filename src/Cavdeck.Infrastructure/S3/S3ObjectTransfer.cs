using Amazon.S3;
using Amazon.S3.Model;
using Cavdeck.Application.Abstractions;
using Cavdeck.Application.Transfers;
using Cavdeck.Domain.Storage;
using Cavdeck.Domain.Transfers;

namespace Cavdeck.Infrastructure.S3;

public sealed class S3ObjectTransfer : IRemoteObjectTransfer, IDisposable
{
    private readonly ICredentialVault _vault;
    private readonly S3ClientLeasePool _clients;
    private readonly bool _ownsClients;
    private int _disposed;

    public S3ObjectTransfer(ICredentialVault vault)
        : this(vault, new S3ClientLeasePool(), ownsClients: true) { }

    public S3ObjectTransfer(ICredentialVault vault, S3ClientLeasePool clients)
        : this(vault, clients, ownsClients: false) { }

    private S3ObjectTransfer(ICredentialVault vault, S3ClientLeasePool clients, bool ownsClients)
    {
        _vault = vault;
        _clients = clients;
        _ownsClients = ownsClients;
    }

    public async Task<IRemoteTransferLease> OpenAsync(
        StorageConnection connection, bool write, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (!string.IsNullOrEmpty(connection.RestrictedBucket))
            ObjectAccess.Ensure(connection, connection.RestrictedBucket, null, write);
        else if (write && connection.IsReadOnly)
            throw new StorageException(new StorageFailure(StorageFailureKind.ReadOnly, null, null, null, null));

        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var session = await OpenSessionAsync(connection, cancellationToken);
        var lease = _clients.Acquire(session);
        if (Volatile.Read(ref _disposed) != 0)
        {
            lease.Dispose();
            throw new ObjectDisposedException(nameof(S3ObjectTransfer));
        }

        try
        {
            return new Lease(connection, session, lease.Client,
                S3ConditionalWritePolicies.For(session), lease.Dispose);
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0 && _ownsClients)
            _clients.Dispose();
    }

    private async Task<S3Session> OpenSessionAsync(StorageConnection connection, CancellationToken cancellationToken)
    {
        var stored = await _vault.ReadAsync(connection.CredentialReference, cancellationToken);
        if (stored is null || string.IsNullOrEmpty(stored.Value.SecretAccessKey))
            throw new StorageException(new StorageFailure(StorageFailureKind.CredentialMissing, null, null, null, null));

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

    private sealed class Lease(StorageConnection connection, S3Session session, AmazonS3Client client,
        IS3ConditionalWritePolicy conditions, Action release) : IRemoteTransferLease
    {
        private int _disposed;
        public bool SupportsVerifiedConditionalMultipart =>
            connection.Provider == StorageProviderKind.AwsS3 && session.ServiceUrl is null;

        public void EnsureVerifiedConditionalPut()
        {
            if (SupportsVerifiedConditionalMultipart || connection.Provider == StorageProviderKind.Minio
                && !S3ConditionalWritePolicies.IsAlibabaOssEndpoint(session.ServiceUrl)
                || connection.Provider == StorageProviderKind.CloudflareR2
                && Uri.TryCreate(session.ServiceUrl, UriKind.Absolute, out var endpoint)
                && endpoint.Scheme == "https"
                && endpoint.Host.EndsWith(".r2.cloudflarestorage.com", StringComparison.OrdinalIgnoreCase))
                return;
            throw new StorageException(new StorageFailure(StorageFailureKind.Unsupported,
                "ConditionalWriteUnsupported", null, null, null));
        }

        public void EnsureConditionalMultipartSupported() => conditions.EnsureConditionalMultipartSupported();

        public void EnsureVerifiedConditionalDelete()
        {
            if (!SupportsVerifiedConditionalMultipart)
                throw new StorageException(new StorageFailure(StorageFailureKind.Unsupported,
                    "ConditionalDeleteUnsupported", null, null, null));
        }

        public async Task<RemoteIdentity?> HeadAsync(string bucket, string key, CancellationToken cancellationToken)
        {
            Guard(bucket, key, write: false);
            try
            {
                var response = await client.GetObjectMetadataAsync(new GetObjectMetadataRequest
                {
                    BucketName = bucket,
                    Key = key
                }, cancellationToken);
                return new RemoteIdentity(response.ETag, response.ContentLength, ToOffset(response.LastModified),
                    IsAwsMd5(session, response.ETag, response.ServerSideEncryptionMethod?.ToString()))
                { VersionId = response.VersionId, ContentType = response.Headers.ContentType };
            }
            catch (AmazonS3Exception exception) when (
                exception.ErrorCode is "NotFound" or "NoSuchKey" or "404")
            {
                return null;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                throw AwsS3Gateway.Map(exception, session);
            }
        }

        public async Task<RemoteIdentity> PutAsync(
            string bucket, string key, Stream content, long length, string? contentType,
            string contentMd5, IProgress<long>? progress, CancellationToken cancellationToken)
        {
            Guard(bucket, key, write: true);
            try
            {
                var request = new PutObjectRequest
                {
                    BucketName = bucket,
                    Key = key,
                    InputStream = content,
                    AutoCloseStream = false,
                    AutoResetStreamPosition = false,
                    ContentType = string.IsNullOrWhiteSpace(contentType) ? null : contentType,
                    MD5Digest = contentMd5,
                    UseChunkEncoding = !content.CanSeek
                };
                request.Headers.ContentLength = length;
                if (session.ServiceUrl?.StartsWith("https://", StringComparison.OrdinalIgnoreCase) == true)
                    request.DisablePayloadSigning = true;
                if (progress is not null)
                    request.StreamTransferProgress += (_, args) => progress.Report(args.TransferredBytes);
                var response = await client.PutObjectAsync(request, cancellationToken);
                return new RemoteIdentity(response.ETag, length, DateTimeOffset.UtcNow,
                    IsAwsMd5(session, response.ETag, response.ServerSideEncryptionMethod?.ToString()));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                throw AwsS3Gateway.Map(exception, session);
            }
        }

        public Task<RemoteIdentity> PutConditionalAsync(
            string bucket, string key, Stream content, long length, string? contentType,
            string contentMd5, string? expectedETag, bool requireMissing,
            CancellationToken cancellationToken) =>
            PutConditionalAsync(bucket, key, content, length, contentType, contentMd5,
                expectedETag, requireMissing, null, cancellationToken);

        public async Task<RemoteIdentity> PutConditionalAsync(
            string bucket, string key, Stream content, long length, string? contentType,
            string contentMd5, string? expectedETag, bool requireMissing,
            IProgress<long>? progress, CancellationToken cancellationToken)
        {
            Guard(bucket, key, write: true);
            S3ConditionalWritePolicies.ValidateCondition(expectedETag, requireMissing);
            try
            {
                var request = new PutObjectRequest
                {
                    BucketName = bucket,
                    Key = key,
                    InputStream = content,
                    AutoCloseStream = false,
                    AutoResetStreamPosition = false,
                    ContentType = string.IsNullOrWhiteSpace(contentType) ? null : contentType,
                    MD5Digest = contentMd5,
                    UseChunkEncoding = !content.CanSeek
                };
                conditions.ConfigurePut(request, expectedETag, requireMissing);
                request.Headers.ContentLength = length;
                if (session.ServiceUrl?.StartsWith("https://", StringComparison.OrdinalIgnoreCase) == true)
                    request.DisablePayloadSigning = true;
                if (progress is not null)
                    request.StreamTransferProgress += (_, args) => progress.Report(args.TransferredBytes);
                var response = await client.PutObjectAsync(request, cancellationToken);
                return new RemoteIdentity(response.ETag, length, DateTimeOffset.UtcNow,
                    IsAwsMd5(session, response.ETag, response.ServerSideEncryptionMethod?.ToString()));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                throw MapConditionalWrite(exception);
            }
        }

        public async Task<string> InitiateMultipartAsync(
            string bucket, string key, string? contentType, CancellationToken cancellationToken)
        {
            Guard(bucket, key, write: true);
            try
            {
                var response = await client.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest
                {
                    BucketName = bucket,
                    Key = key,
                    ContentType = string.IsNullOrWhiteSpace(contentType) ? null : contentType
                }, cancellationToken);
                return response.UploadId;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                throw AwsS3Gateway.Map(exception, session);
            }
        }

        public async Task<string> UploadPartAsync(
            string bucket, string key, string uploadId, int partNumber, Stream content, long length,
            string contentMd5, IProgress<long>? progress, CancellationToken cancellationToken)
        {
            Guard(bucket, key, write: true);
            try
            {
                var request = new UploadPartRequest
                {
                    BucketName = bucket,
                    Key = key,
                    UploadId = uploadId,
                    PartNumber = partNumber,
                    PartSize = length,
                    InputStream = content,
                    MD5Digest = contentMd5,
                    DisablePayloadSigning = session.ServiceUrl?.StartsWith("https://", StringComparison.OrdinalIgnoreCase) == true
                };
                if (progress is not null)
                    request.StreamTransferProgress += (_, args) => progress.Report(args.TransferredBytes);
                var response = await client.UploadPartAsync(request, cancellationToken);
                return response.ETag;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                throw AwsS3Gateway.Map(exception, session);
            }
        }

        public async Task CompleteMultipartAsync(
            string bucket, string key, string uploadId, IReadOnlyList<TransferPart> parts,
            CancellationToken cancellationToken)
        {
            Guard(bucket, key, write: true);
            try
            {
                var request = new CompleteMultipartUploadRequest
                {
                    BucketName = bucket,
                    Key = key,
                    UploadId = uploadId,
                    PartETags = parts.OrderBy(part => part.Number)
                        .Select(part => new PartETag(part.Number, part.ETag)).ToList()
                };
                await client.CompleteMultipartUploadAsync(request, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                throw AwsS3Gateway.Map(exception, session);
            }
        }

        public async Task CompleteMultipartConditionalAsync(
            string bucket, string key, string uploadId, IReadOnlyList<TransferPart> parts,
            string? expectedETag, bool requireMissing, CancellationToken cancellationToken)
        {
            Guard(bucket, key, write: true);
            S3ConditionalWritePolicies.ValidateCondition(expectedETag, requireMissing);
            try
            {
                var request = new CompleteMultipartUploadRequest
                {
                    BucketName = bucket,
                    Key = key,
                    UploadId = uploadId,
                    PartETags = parts.OrderBy(part => part.Number)
                        .Select(part => new PartETag(part.Number, part.ETag)).ToList()
                };
                conditions.ConfigureMultipartComplete(request, expectedETag, requireMissing);
                await client.CompleteMultipartUploadAsync(request, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                throw MapConditionalWrite(exception);
            }
        }

        private StorageException MapConditionalWrite(Exception exception)
        {
            var mapped = AwsS3Gateway.Map(exception, session);
            if (mapped.Failure.StatusCode == 409)
                return new StorageException(mapped.Failure with { Kind = StorageFailureKind.PreconditionFailed });
            if (mapped.Failure.StatusCode == 501 || mapped.Failure.ErrorCode is
                "NotImplemented" or "UnsupportedHeader" or "UnsupportedOperation" or "InvalidRequest")
                return new StorageException(mapped.Failure with { Kind = StorageFailureKind.Unsupported });
            return mapped;
        }

        public async Task AbortMultipartAsync(
            string bucket, string key,             string uploadId, CancellationToken cancellationToken)
        {
            Guard(bucket, key, write: true);
            try
            {
                await client.AbortMultipartUploadAsync(new AbortMultipartUploadRequest
                {
                    BucketName = bucket,
                    Key = key,
                    UploadId = uploadId
                }, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                throw AwsS3Gateway.Map(exception, session);
            }
        }

        public async Task<IReadOnlyList<TransferPart>> ListPartsAsync(
            string bucket, string key,             string uploadId, CancellationToken cancellationToken)
        {
            Guard(bucket, key, write: true);
            try
            {
                var parts = new List<TransferPart>();
                string? marker = null;
                do
                {
                    var response = await client.ListPartsAsync(new ListPartsRequest
                    {
                        BucketName = bucket,
                        Key = key,
                        UploadId = uploadId,
                        PartNumberMarker = marker
                    }, cancellationToken);
                    foreach (var part in response.Parts ?? [])
                    {
                        parts.Add(new TransferPart(part.PartNumber ?? 0, 0, part.Size ?? 0, part.ETag));
                    }

                    marker = response.IsTruncated == true ? response.NextPartNumberMarker?.ToString() : null;
                }
                while (marker is not null);

                return parts;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                throw AwsS3Gateway.Map(exception, session);
            }
        }

        public async Task<DownloadBody> OpenDownloadAsync(
            string bucket, string key,             long? rangeStart, CancellationToken cancellationToken)
        {
            Guard(bucket, key, write: false);
            try
            {
                var request = new GetObjectRequest { BucketName = bucket, Key = key };
                long? wholeObjectSize = null;
                if (rangeStart is > 0)
                {
                    var meta = await client.GetObjectMetadataAsync(new GetObjectMetadataRequest
                    {
                        BucketName = bucket,
                        Key = key
                    }, cancellationToken);
                    var end = Math.Max(rangeStart.Value, meta.ContentLength - 1);
                    request.ByteRange = new ByteRange(rangeStart.Value, end);
                    wholeObjectSize = meta.ContentLength;
                }

                var response = await client.GetObjectAsync(request, cancellationToken);
                var honored = rangeStart is not > 0 || (int)response.HttpStatusCode == 206;
                if (rangeStart is > 0 && honored
                    && !ResumeRules.MatchesRange(response.ContentRange, rangeStart.Value,
                        wholeObjectSize!.Value, response.ContentLength))
                {
                    response.Dispose();
                    throw new StorageException(new StorageFailure(StorageFailureKind.Network,
                        "InvalidRangeResponse", 206, null, "The server returned an unexpected byte range."));
                }
                // A ranged response reports the slice length in Content-Length.
                var identity = new RemoteIdentity(response.ETag, wholeObjectSize ?? response.ContentLength,
                    ToOffset(response.LastModified),
                    IsAwsMd5(session, response.ETag, response.ServerSideEncryptionMethod?.ToString()));
                return new DownloadBody
                {
                    Content = new ResponseStream(response),
                    Identity = identity,
                    RangeHonored = honored
                };
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                throw AwsS3Gateway.Map(exception, session);
            }
        }

        public async Task<DownloadBody> OpenRangeAsync(
            string bucket, string key, long offset, long length, string expectedETag,
            string? versionId, CancellationToken cancellationToken)
        {
            Guard(bucket, key, write: false);
            if (offset < 0 || length <= 0 || length > long.MaxValue - offset || string.IsNullOrWhiteSpace(expectedETag))
                throw new ArgumentOutOfRangeException(nameof(length), "A valid fixed range and source ETag are required.");
            try
            {
                var request = new GetObjectRequest
                {
                    BucketName = bucket,
                    Key = key,
                    ByteRange = new ByteRange(offset, offset + length - 1),
                    EtagToMatch = expectedETag,
                    VersionId = versionId
                };
                var response = await client.GetObjectAsync(request, cancellationToken);
                if ((int)response.HttpStatusCode != 206
                    || !FixedRangeMatches(response.ContentRange, offset, length, response.ContentLength)
                    || !string.Equals(RemoteIdentity.Normalize(response.ETag ?? ""),
                        RemoteIdentity.Normalize(expectedETag), StringComparison.OrdinalIgnoreCase))
                {
                    response.Dispose();
                    throw new StorageException(new StorageFailure(StorageFailureKind.Network,
                        "InvalidRangeResponse", null, null, "The source returned an unexpected range or identity."));
                }
                return new DownloadBody
                {
                    Content = new ResponseStream(response),
                    Identity = new RemoteIdentity(response.ETag, 0, ToOffset(response.LastModified)),
                    RangeHonored = true
                };
            }
            catch (AmazonS3Exception exception) when ((int)exception.StatusCode == 412)
            {
                throw new StorageException(new StorageFailure(StorageFailureKind.PreconditionFailed,
                    "SourceChanged", 412, exception.RequestId, "The source object changed during copying."));
            }
            catch (Exception exception) when (exception is not OperationCanceledException and not StorageException)
            {
                throw AwsS3Gateway.Map(exception, session);
            }
        }

        private static bool FixedRangeMatches(string? contentRange, long offset, long length, long contentLength)
        {
            if (contentLength != length || string.IsNullOrEmpty(contentRange)
                || !contentRange.StartsWith("bytes ", StringComparison.OrdinalIgnoreCase))
                return false;
            var span = contentRange.AsSpan(6);
            var dash = span.IndexOf('-');
            var slash = span.IndexOf('/');
            return dash > 0 && slash > dash
                && long.TryParse(span[..dash], out var start)
                && long.TryParse(span[(dash + 1)..slash], out var end)
                && long.TryParse(span[(slash + 1)..], out var total)
                && start == offset && end == offset + length - 1 && total >= end + 1;
        }

        public async IAsyncEnumerable<ListedObject> ListAsync(
            string bucket, string prefix,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Guard(bucket, prefix, write: false);
            string? token = null;
            do
            {
                ListObjectsV2Response response;
                try
                {
                    response = await client.ListObjectsV2Async(new ListObjectsV2Request
                    {
                        BucketName = bucket,
                        Prefix = prefix,
                        ContinuationToken = token,
                        MaxKeys = 1000
                    }, cancellationToken);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    throw AwsS3Gateway.Map(exception, session);
                }

                foreach (var item in response.S3Objects ?? [])
                    yield return new ListedObject(item.Key ?? "", item.Size ?? 0, item.ETag);
                token = response.IsTruncated == true ? response.NextContinuationToken : null;
            }
            while (token is not null);
        }

        public async Task<ListedPage> ListPageAsync(string bucket, string prefix,
            string? continuationToken, CancellationToken cancellationToken)
        {
            Guard(bucket, prefix, write: false);
            try
            {
                var response = await client.ListObjectsV2Async(new ListObjectsV2Request
                {
                    BucketName = bucket,
                    Prefix = prefix,
                    ContinuationToken = continuationToken,
                    MaxKeys = 1000
                }, cancellationToken);
                if (response.IsTruncated == true && string.IsNullOrEmpty(response.NextContinuationToken))
                    throw new IOException("The provider omitted a continuation token from a truncated listing.");
                return new ListedPage((response.S3Objects ?? [])
                    .Select(item => new ListedObject(item.Key ?? "", item.Size ?? 0, item.ETag)).ToList(),
                    response.IsTruncated == true ? response.NextContinuationToken : null);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                throw AwsS3Gateway.Map(exception, session);
            }
        }

        public async Task DeleteAsync(string bucket, string key, CancellationToken cancellationToken)
        {
            Guard(bucket, key, write: true);
            try
            {
                await client.DeleteObjectAsync(new DeleteObjectRequest
                {
                    BucketName = bucket,
                    Key = key
                }, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                throw AwsS3Gateway.Map(exception, session);
            }
        }

        public async Task DeleteIfMatchAsync(string bucket, string key, string expectedETag,
            CancellationToken cancellationToken)
        {
            Guard(bucket, key, write: true);
            if (string.IsNullOrWhiteSpace(expectedETag))
                throw new ArgumentException("Conditional deletion requires an ETag.", nameof(expectedETag));
            try
            {
                await client.DeleteObjectAsync(new DeleteObjectRequest
                {
                    BucketName = bucket,
                    Key = key,
                    IfMatch = expectedETag
                }, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                throw AwsS3Gateway.Map(exception, session);
            }
        }

        private void Guard(string bucket, string? key, bool write) =>
            ObjectAccess.Ensure(connection, bucket, string.IsNullOrEmpty(key) ? null : key, write);

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                release();
            return ValueTask.CompletedTask;
        }
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

    private static bool IsAwsMd5(S3Session session, string? etag, string? encryption) =>
        session.ServiceUrl is null
        && (string.IsNullOrEmpty(encryption) || encryption == "AES256")
        && IntegrityRules.IsWholeObjectMd5(etag);

    private sealed class ResponseStream(GetObjectResponse response) : Stream
    {
        private readonly Stream _inner = response.ResponseStream;
        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => response.ContentLength;
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            _inner.ReadAsync(buffer, cancellationToken);
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
                response.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
