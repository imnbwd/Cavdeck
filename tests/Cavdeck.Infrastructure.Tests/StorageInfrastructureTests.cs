using System.Text.Json;
using Cavdeck.Infrastructure.Configuration;
using Cavdeck.Domain.Storage;
using Cavdeck.Infrastructure.Connections;
using Cavdeck.Infrastructure.S3;
using Cavdeck.Infrastructure.Security;
using Xunit;

namespace Cavdeck.Infrastructure.Tests;

public sealed class JsonConnectionCatalogTests
{
    [Fact]
    public async Task Roundtrip_keeps_non_secret_fields_only()
    {
        var path = Path.Combine(Path.GetTempPath(), "cavdeck-tests", Guid.NewGuid().ToString("N"), "connections.json");
        var catalog = new JsonConnectionCatalog(path);
        var connection = new StorageConnection(
            Guid.NewGuid(), "Photos", StorageProviderKind.CloudflareR2,
            "https://abcdefghijklmnopqrstuvwxyz012345.r2.cloudflarestorage.com",
            "auto", CredentialReferences.ForConnection(Guid.NewGuid()),
            RestrictedBucket: "assets", KeyPrefix: "images/", ForcePathStyle: true,
            AccountId: "abcdefghijklmnopqrstuvwxyz012345");

        await catalog.UpsertAsync(connection);
        var loaded = await catalog.ListAsync();

        Assert.Equal(connection, Assert.Single(loaded));
        var json = await File.ReadAllTextAsync(path);
        Assert.DoesNotContain("Secret", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Newer_schema_is_refused()
    {
        var path = Path.Combine(Path.GetTempPath(), "cavdeck-tests", Guid.NewGuid().ToString("N"), "connections.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, """{"Schema":9,"Connections":[]}""");

        var catalog = new JsonConnectionCatalog(path);
        await Assert.ThrowsAsync<UnsupportedConfigurationVersionException>(() => catalog.ListAsync());
    }
}

public sealed class WindowsCredentialVaultTests
{
    [Fact]
    public async Task Secret_roundtrips_and_delete_is_idempotent()
    {
        var vault = new WindowsCredentialVault();
        var reference = $"Cavdeck:test:{Guid.NewGuid():N}";
        try
        {
            await vault.SaveAsync(reference, "AKIDEXAMPLE123456789", "stored-secret-value", CancellationToken.None);
            var first = await vault.ReadAsync(reference, CancellationToken.None);
            Assert.Equal("AKIDEXAMPLE123456789", first?.AccessKeyId);
            Assert.Equal("stored-secret-value", first?.SecretAccessKey);

            await vault.SaveAsync(reference, "AKIDEXAMPLE123456789", "rotated-secret-value", CancellationToken.None);
            var rotated = await vault.ReadAsync(reference, CancellationToken.None);
            Assert.Equal("rotated-secret-value", rotated?.SecretAccessKey);

            await vault.DeleteAsync(reference, CancellationToken.None);
            Assert.Null(await vault.ReadAsync(reference, CancellationToken.None));
            await vault.DeleteAsync(reference, CancellationToken.None);
        }
        finally
        {
            await vault.DeleteAsync(reference, CancellationToken.None);
        }
    }
}

public sealed class S3ProviderGuardTests
{
    [Fact]
    public async Task Restricted_bucket_does_not_call_list_buckets()
    {
        var gateway = new FakeGateway();
        var provider = new S3CompatibleStorageProvider(
            StorageProviderKind.Minio, new EmptyVault(), gateway);
        var connection = Sample(restricted: "assets");

        var buckets = await provider.ListBucketsAsync(connection, CancellationToken.None);

        Assert.Equal("assets", Assert.Single(buckets).Name);
        Assert.Equal(0, gateway.ListCalls);
    }

    [Fact]
    public async Task Read_only_upload_does_not_reach_the_gateway()
    {
        var gateway = new FakeGateway();
        var provider = new S3CompatibleStorageProvider(
            StorageProviderKind.Minio, new EmptyVault(), gateway);
        var connection = Sample() with { IsReadOnly = true };

        var error = await Assert.ThrowsAsync<StorageException>(() => provider.UploadAsync(
            connection, "assets", "images/a.txt", new MemoryStream([1]), null, null, CancellationToken.None));

        Assert.Equal(StorageFailureKind.ReadOnly, error.Failure.Kind);
        Assert.Equal(0, gateway.UploadCalls);
    }

    [Fact]
    public async Task Prefix_outside_the_connection_limit_does_not_reach_the_gateway()
    {
        var gateway = new FakeGateway();
        var provider = new S3CompatibleStorageProvider(
            StorageProviderKind.Minio, new EmptyVault(), gateway);

        var error = await Assert.ThrowsAsync<StorageException>(() => provider.ListPrefixAsync(
            Sample(), "assets", "other/", null, 50, CancellationToken.None));

        Assert.Equal(StorageFailureKind.AccessDenied, error.Failure.Kind);
        Assert.Equal(0, gateway.PrefixCalls);
    }

    [Fact]
    public async Task Missing_secret_is_reported_without_a_network_call()
    {
        var gateway = new FakeGateway();
        var provider = new S3CompatibleStorageProvider(
            StorageProviderKind.Minio, new EmptyVault(), gateway);

        var error = await Assert.ThrowsAsync<StorageException>(() =>
            provider.ListBucketsAsync(Sample(), CancellationToken.None));

        Assert.Equal(StorageFailureKind.CredentialMissing, error.Failure.Kind);
        Assert.Equal(0, gateway.ListCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Exact_page_preserves_raw_prefix_page_size_token_and_cancellation(bool recursive)
    {
        const string prefix = "images/ 空//x%2F";
        var gateway = new FakeGateway();
        var provider = new S3CompatibleStorageProvider(StorageProviderKind.Minio,
            new EmptyVault(("fictional-key", "fictional-secret")), gateway);
        using var cancellation = new CancellationTokenSource();
        var page = await provider.ListExactPageAsync(Sample("assets"), "assets", prefix,
            recursive, "opaque=page+token", 2, cancellation.Token);

        Assert.Equal((prefix, "opaque=page+token", 2, recursive, cancellation.Token), gateway.ExactRequest);
        Assert.Equal("following-page", page.ContinuationToken);
        if (recursive)
            Assert.Empty(page.CommonPrefixes);
        else
            Assert.Equal(prefix + "/child/", Assert.Single(page.CommonPrefixes));
        Assert.Equal(prefix + "/", Assert.Single(page.Objects).Key);

        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.ListExactPageAsync(
            Sample("assets"), "assets", prefix, recursive, null, 1, cancellation.Token));
    }

    [Fact]
    public async Task Exact_page_honors_scope_and_uses_configured_root_for_empty_prefix()
    {
        var gateway = new FakeGateway();
        var provider = new S3CompatibleStorageProvider(StorageProviderKind.Minio,
            new EmptyVault(("fictional-key", "fictional-secret")), gateway);
        var connection = Sample("assets");
        var page = await provider.ListExactPageAsync(connection, "assets", "", false, null, 25, default);
        Assert.Equal("images/", gateway.ExactRequest.Prefix);
        Assert.Empty(page.Objects);
        Assert.Empty(page.CommonPrefixes);
        Assert.Null(page.ContinuationToken);
        var wrongBucket = await Assert.ThrowsAsync<StorageException>(() => provider.ListExactPageAsync(
            connection, "other", "images/", true, null, 25, default));
        Assert.Equal(StorageFailureKind.AccessDenied, wrongBucket.Failure.Kind);
        var wrongPrefix = await Assert.ThrowsAsync<StorageException>(() => provider.ListExactPageAsync(
            connection, "assets", "/images/", false, null, 25, default));
        Assert.Equal(StorageFailureKind.AccessDenied, wrongPrefix.Failure.Kind);
    }

    [Fact]
    public async Task Head_preserves_object_key_and_propagates_storage_errors()
    {
        const string key = "images/a//b%2F ";
        var gateway = new FakeGateway();
        var provider = new S3CompatibleStorageProvider(StorageProviderKind.Minio,
            new EmptyVault(("fictional-key", "fictional-secret")), gateway);
        var connection = Sample("assets");
        Assert.Equal(new ObjectHead("assets", key, 0, null, null),
            await provider.HeadObjectAsync(connection, "assets", key, default));
        foreach (var kind in new[] { StorageFailureKind.BucketNotFound, StorageFailureKind.AccessDenied, StorageFailureKind.Network })
        {
            gateway.HeadFailure = new StorageFailure(kind, null, null, null, "fictional provider text");
            var error = await Assert.ThrowsAsync<StorageException>(() =>
                provider.HeadObjectAsync(connection, "assets", key, default));
            Assert.Equal(kind, error.Failure.Kind);
        }
    }

    private static StorageConnection Sample(string? restricted = null) => new(
        Guid.NewGuid(), "Local", StorageProviderKind.Minio, "http://127.0.0.1:9000", "us-east-1",
        CredentialReferences.ForConnection(Guid.NewGuid()), RestrictedBucket: restricted, KeyPrefix: "images/");

    private sealed class EmptyVault((string, string)? credential = null) : Cavdeck.Application.Abstractions.ICredentialVault
    {
        public Task SaveAsync(string reference, string accessKeyId, string secretAccessKey,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<(string AccessKeyId, string SecretAccessKey)?> ReadAsync(
            string reference, CancellationToken cancellationToken) =>
            Task.FromResult(credential);

        public Task DeleteAsync(string reference, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeGateway : IS3Gateway
    {
        public int ListCalls { get; private set; }
        public int UploadCalls { get; private set; }
        public int PrefixCalls { get; private set; }
        public (string Prefix, string? Token, int Limit, bool Recursive, CancellationToken Cancellation) ExactRequest { get; private set; }
        public StorageFailure? HeadFailure { get; set; }

        public Task<IReadOnlyList<string>> ListBucketNamesAsync(S3Session session, CancellationToken cancellationToken)
        {
            ListCalls++;
            return Task.FromResult<IReadOnlyList<string>>(["assets"]);
        }

        public Task HeadBucketAsync(S3Session session, string bucket, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<StoragePage<StorageObject>> ListObjectsAsync(
            S3Session session, string bucket, string prefix, string? continuationToken, int pageSize,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ExactRequest = (prefix, continuationToken, pageSize, true, cancellationToken);
            return Task.FromResult(new StoragePage<StorageObject>(
                continuationToken is null ? [] : [new(bucket, prefix + "/", 0, DateTimeOffset.UnixEpoch, null, null)],
                continuationToken is null ? null : "following-page"));
        }

        public Task<PrefixPage> ListPrefixAsync(
            S3Session session, string bucket, string prefix, string? continuationToken, int pageSize,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PrefixCalls++;
            ExactRequest = (prefix, continuationToken, pageSize, false, cancellationToken);
            return Task.FromResult(continuationToken is null
                ? new PrefixPage([], [], null)
                : new PrefixPage([prefix + "/child/"],
                    [new(bucket, prefix + "/", 0, DateTimeOffset.UnixEpoch, null, null)], "following-page"));
        }

        public Task<bool> ObjectExistsAsync(
            S3Session session, string bucket, string key, CancellationToken cancellationToken) =>
            Task.FromResult(false);

        public Task CopyObjectAsync(
            S3Session session, string sourceBucket, string sourceKey, string destinationBucket, string destinationKey,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task UploadAsync(
            S3Session session, string bucket, string key, Stream content, string? contentType,
            IProgress<long>? progress, CancellationToken cancellationToken)
        {
            UploadCalls++;
            return Task.CompletedTask;
        }

        public Task DownloadAsync(
            S3Session session, string bucket, string key, Stream destination, IProgress<long>? progress,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task DeleteObjectAsync(S3Session session, string bucket, string key, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<ObjectHead> HeadObjectAsync(
            S3Session session, string bucket, string key, CancellationToken cancellationToken) =>
            HeadFailure is { } error
                ? Task.FromException<ObjectHead>(new StorageException(error))
                : Task.FromResult(new ObjectHead(bucket, key, 0, null, null));

        public Task<ObjectBody> ReadObjectAsync(
            S3Session session, string bucket, string key, int maxBytes, CancellationToken cancellationToken) =>
            Task.FromResult(new ObjectBody(new ObjectHead(bucket, key, 0, null, null), [], false));

        public Task<ObjectHead> ReplaceObjectAsync(
            S3Session session, string bucket, string key, Stream content, string? contentType, string? ifMatchETag,
            CancellationToken cancellationToken) =>
            Task.FromResult(new ObjectHead(bucket, key, 0, ifMatchETag, contentType));
    }
}
