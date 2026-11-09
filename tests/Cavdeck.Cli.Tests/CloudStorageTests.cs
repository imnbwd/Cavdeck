using System.Text;
using Cavdeck.Application.Abstractions;
using Cavdeck.Domain.Storage;
using Cavdeck.Infrastructure.S3;
using Xunit;

namespace Cavdeck.Cli.Tests;

public sealed class CloudStorageTests
{
    [CloudFact("AWS")]
    public Task AwsS3() => VerifyAsync("AWS", StorageProviderKind.AwsS3);

    [CloudFact("R2")]
    public Task CloudflareR2() => VerifyAsync("R2", StorageProviderKind.CloudflareR2);

    [CloudFact("MINIO")]
    public Task Minio() => VerifyAsync("MINIO", StorageProviderKind.Minio);

    private static async Task VerifyAsync(string provider, StorageProviderKind kind)
    {
        var bucket = Read(provider, "BUCKET")!;
        var prefix = Read(provider, "PREFIX")!.TrimEnd('/') + "/";
        Assert.StartsWith("cavdeck-cli-test/", prefix, StringComparison.Ordinal);
        var key = prefix + Guid.NewGuid().ToString("N") + "/unicode 空%2F//file.txt";
        var credentials = new Vault(Read(provider, "ACCESS_KEY")!, Read(provider, "SECRET_KEY")!);
        var connection = new StorageConnection(Guid.NewGuid(), "CLI integration", kind, Read(provider, "ENDPOINT") ?? "",
            Read(provider, "REGION") ?? (kind == StorageProviderKind.CloudflareR2 ? "auto" : "us-east-1"),
            "integration", RestrictedBucket: bucket, KeyPrefix: prefix, ForcePathStyle: kind != StorageProviderKind.AwsS3);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var token = timeout.Token;
        using var pool = new S3ClientLeasePool();
        using var transfer = new S3ObjectTransfer(credentials, pool);
        using var providers = new S3ProviderFactory(credentials, pool);
        var objects = providers.Create(kind);
        await using var lease = await transfer.OpenAsync(connection, write: true, token);
        lease.EnsureVerifiedConditionalPut();
        var owned = false;
        try
        {
            await PutAsync("original");
            owned = true;
            var error = await Assert.ThrowsAsync<StorageException>(() => PutAsync("must never overwrite"));
            Assert.Equal(StorageFailureKind.PreconditionFailed, error.Failure.Kind);
            var head = await objects.HeadObjectAsync(connection, bucket, key, token);
            Assert.Equal(8, head.SizeBytes);
            var page = await objects.ListExactPageAsync(connection, bucket, key, true, null, 1, token);
            Assert.Equal(key, Assert.Single(page.Objects).Key);
            await using var body = await lease.OpenDownloadAsync(bucket, key, null, token);
            using var reader = new StreamReader(body.Content);
            Assert.Equal("original", await reader.ReadToEndAsync(token));

            if (lease.SupportsVerifiedConditionalMultipart)
            {
                var upload = await lease.InitiateMultipartAsync(bucket, key, "text/plain", token);
                try
                {
                    using var content = new MemoryStream(Encoding.UTF8.GetBytes("multipart replacement"));
                    var tag = await lease.UploadPartAsync(bucket, key, upload, 1, content, content.Length,
                        Digest(content), null, token);
                    var conflict = await Assert.ThrowsAsync<StorageException>(() => lease.CompleteMultipartConditionalAsync(
                        bucket, key, upload, [new(1, 0, content.Length, tag)], null, true, token));
                    Assert.Equal(StorageFailureKind.PreconditionFailed, conflict.Failure.Kind);
                }
                finally
                {
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    try { await lease.AbortMultipartAsync(bucket, key, upload, cleanup.Token); }
                    catch (StorageException e) when (e.Failure.ErrorCode == "NoSuchUpload") { }
                }
            }
        }
        finally
        {
            if (owned)
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await lease.DeleteAsync(bucket, key, cleanup.Token);
            }
        }

        async Task PutAsync(string data)
        {
            using var content = new MemoryStream(Encoding.UTF8.GetBytes(data));
            await lease.PutConditionalAsync(bucket, key, content, content.Length, "text/plain",
                Digest(content), null, true, token);
        }
    }

    private static string Digest(MemoryStream content) =>
        Convert.ToBase64String(System.Security.Cryptography.MD5.HashData(content.ToArray()));

    private static string? Read(string provider, string field) =>
        Environment.GetEnvironmentVariable($"CAVDECK_CLI_TEST_{provider}_{field}");

    private sealed class CloudFactAttribute : FactAttribute
    {
        public CloudFactAttribute(string provider)
        {
            var fields = provider == "AWS"
                ? new[] { "BUCKET", "PREFIX", "ACCESS_KEY", "SECRET_KEY" }
                : new[] { "BUCKET", "PREFIX", "ACCESS_KEY", "SECRET_KEY", "ENDPOINT" };
            if (Read(provider, "ENABLED") != "1" || fields.Any(field => string.IsNullOrWhiteSpace(Read(provider, field))))
                Skip = $"Dedicated {provider} test storage is not configured.";
        }
    }

    private sealed class Vault(string key, string secret) : ICredentialVault
    {
        public Task<(string AccessKeyId, string SecretAccessKey)?> ReadAsync(string reference, CancellationToken token) =>
            Task.FromResult<(string, string)?>((key, secret));
        public Task SaveAsync(string reference, string accessKeyId, string secretAccessKey, CancellationToken token) => throw new NotSupportedException();
        public Task DeleteAsync(string reference, CancellationToken token) => throw new NotSupportedException();
    }
}
