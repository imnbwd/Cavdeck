using Amazon;
using Amazon.S3;
using Amazon.S3.Model;
using Cavdeck.Application.Abstractions;
using Cavdeck.Application.Sharing;
using Cavdeck.Domain.Storage;

namespace Cavdeck.Infrastructure.S3;

public sealed class S3DownloadLinkSigner : IDownloadLinkSigner, IDisposable
{
    private readonly ICredentialVault _vault;
    private readonly S3ClientLeasePool _clients;
    private readonly bool _ownsClients;

    public S3DownloadLinkSigner(ICredentialVault vault)
        : this(vault, new S3ClientLeasePool(), ownsClients: true) { }

    public S3DownloadLinkSigner(ICredentialVault vault, S3ClientLeasePool clients)
        : this(vault, clients, ownsClients: false) { }

    private S3DownloadLinkSigner(ICredentialVault vault, S3ClientLeasePool clients, bool ownsClients)
    {
        _vault = vault;
        _clients = clients;
        _ownsClients = ownsClients;
    }

    public void Dispose()
    {
        if (_ownsClients)
            _clients.Dispose();
    }

    public async Task<ShareLink> SignDownloadAsync(StorageConnection connection, string bucket,
        string key, TimeSpan lifetime, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (!ShareLinks.ValidLifetime(lifetime) || string.IsNullOrWhiteSpace(key))
            throw new ArgumentOutOfRangeException(nameof(lifetime));
        ObjectAccess.Ensure(connection, bucket, key, write: false);
        if (!ConnectionPlanner.Capabilities(connection.Provider).CanPresignDownload)
            throw new NotSupportedException("This provider cannot sign download links.");

        var credentials = await _vault.ReadAsync(connection.CredentialReference, cancellationToken);
        if (credentials is null || string.IsNullOrEmpty(credentials.Value.SecretAccessKey))
            throw new StorageException(new StorageFailure(
                StorageFailureKind.CredentialMissing, null, null, null, null));

        var session = new S3Session
        {
            ConnectionId = connection.Id,
            AccessKeyId = credentials.Value.AccessKeyId,
            SecretAccessKey = credentials.Value.SecretAccessKey,
            Region = connection.Region,
            ServiceUrl = string.IsNullOrEmpty(connection.Endpoint) ? null : connection.Endpoint,
            ForcePathStyle = connection.ForcePathStyle
        };
        cancellationToken.ThrowIfCancellationRequested();
        using var lease = _clients.Acquire(session);
        var client = lease.Client;
        var expiresAt = DateTimeOffset.UtcNow.Add(lifetime);
        try
        {
            var url = client.GetPreSignedURL(new GetPreSignedUrlRequest
            {
                BucketName = bucket,
                Key = key,
                Verb = HttpVerb.GET,
                Expires = expiresAt.UtcDateTime,
                Protocol = session.ServiceUrl?.StartsWith("http://", StringComparison.OrdinalIgnoreCase) == true
                    ? Protocol.HTTP : Protocol.HTTPS
            });
            return new ShareLink(url, false, expiresAt);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw AwsS3Gateway.Map(exception, session);
        }
    }
}
