using Cavdeck.Application.Abstractions;
using Cavdeck.Domain.Storage;
using Cavdeck.Infrastructure.S3;
using Xunit;

namespace Cavdeck.Infrastructure.Tests;

public sealed class S3DownloadLinkSignerTests
{
    [Fact]
    public async Task R2_link_uses_api_host_and_has_signature_and_expiry()
    {
        var connection = new StorageConnection(Guid.NewGuid(), "R2", StorageProviderKind.CloudflareR2,
            "https://1234567890abcdef1234567890abcdef.r2.cloudflarestorage.com", "auto", "secret",
            ForcePathStyle: true);
        var signer = new S3DownloadLinkSigner(new MemoryVault());

        var link = await signer.SignDownloadAsync(connection, "assets", "a b.txt",
            TimeSpan.FromHours(1), CancellationToken.None);

        Assert.False(link.IsPublic);
        Assert.NotNull(link.ExpiresAt);
        var uri = new Uri(link.Url);
        Assert.Equal("1234567890abcdef1234567890abcdef.r2.cloudflarestorage.com", uri.Host);
        Assert.Contains("X-Amz-Signature=", uri.Query, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("X-Amz-Expires=3600", uri.Query, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("a%20b.txt", uri.AbsolutePath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Transfer_and_signer_reuse_the_same_pooled_client()
    {
        var creations = 0;
        using var pool = new S3ClientLeasePool(session =>
        {
            Interlocked.Increment(ref creations);
            return AwsS3Gateway.CreateClient(session);
        });
        var vault = new MemoryVault();
        using var transfer = new S3ObjectTransfer(vault, pool);
        using var signer = new S3DownloadLinkSigner(vault, pool);
        var connection = new StorageConnection(Guid.NewGuid(), "R2", StorageProviderKind.CloudflareR2,
            "https://1234567890abcdef1234567890abcdef.r2.cloudflarestorage.com", "auto", "secret",
            ForcePathStyle: true);

        await using var lease = await transfer.OpenAsync(connection, write: false, CancellationToken.None);
        var link = await signer.SignDownloadAsync(connection, "assets", "example.txt",
            TimeSpan.FromMinutes(10), CancellationToken.None);

        Assert.False(string.IsNullOrWhiteSpace(link.Url));
        Assert.Equal(1, creations);

        // Borrowed clients belong to the pool, not to the individual services.
        transfer.Dispose();
        signer.Dispose();
        using var stillAvailable = pool.Acquire(new S3Session
        {
            ConnectionId = connection.Id,
            AccessKeyId = "test-access-key",
            SecretAccessKey = "test-secret-key",
            Region = connection.Region,
            ServiceUrl = connection.Endpoint,
            ForcePathStyle = connection.ForcePathStyle
        });
        Assert.Equal(1, creations);
    }

    private sealed class MemoryVault : ICredentialVault
    {
        public Task SaveAsync(string reference, string accessKeyId, string secretAccessKey,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<(string AccessKeyId, string SecretAccessKey)?> ReadAsync(
            string reference, CancellationToken cancellationToken) =>
            Task.FromResult<(string, string)?>(new("test-access-key", "test-secret-key"));

        public Task DeleteAsync(string reference, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
