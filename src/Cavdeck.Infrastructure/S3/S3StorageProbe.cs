using Cavdeck.Application.Abstractions;
using Cavdeck.Domain.Storage;

namespace Cavdeck.Infrastructure.S3;

public sealed class S3StorageProbe : IStorageProbe, IDisposable
{
    private readonly IS3Gateway _gateway;

    public S3StorageProbe() : this(new AwsS3Gateway()) { }

    // Unsaved connection tests use fresh clients, not the saved-connection cache.
    public S3StorageProbe(S3ClientLeasePool clients) : this(new AwsS3Gateway(clients)) { }

    internal S3StorageProbe(IS3Gateway gateway) => _gateway = gateway;

    public void Dispose()
    {
        if (_gateway is IDisposable disposable)
            disposable.Dispose();
    }

    public async Task<StorageProbeResult> ProbeAsync(
        StorageProbeRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        var session = new S3Session
        {
            AccessKeyId = request.AccessKeyId,
            SecretAccessKey = request.SecretAccessKey,
            Region = request.Region,
            ServiceUrl = request.ServiceUrl,
            ForcePathStyle = request.ForcePathStyle
        };
        var secrets = new[] { request.AccessKeyId, request.SecretAccessKey };

        try
        {
            if (!string.IsNullOrWhiteSpace(request.Bucket))
            {
                await _gateway.HeadBucketAsync(session, request.Bucket, timeout.Token);
                if (!string.IsNullOrWhiteSpace(request.KeyPrefix))
                    await _gateway.ListObjectsAsync(
                        session, request.Bucket, request.KeyPrefix, null, 1, timeout.Token);
                return StorageProbeResult.Success(null, request.Bucket);
            }

            var names = await _gateway.ListBucketNamesAsync(session, timeout.Token);
            return StorageProbeResult.Success(names.Count, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return StorageProbeResult.Failed(
                StorageFailureClassifier.FromTransport("The storage service timed out.", secrets));
        }
        catch (StorageException exception)
        {
            return StorageProbeResult.Failed(exception.Failure);
        }
        catch (Exception exception)
        {
            return StorageProbeResult.Failed(StorageFailureClassifier.FromTransport(exception.Message, secrets));
        }
    }
}
