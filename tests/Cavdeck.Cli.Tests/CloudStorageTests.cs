using System.Text;
using Cavdeck.Application.Abstractions;
using Cavdeck.Application.Explorer;
using Cavdeck.Application.Transfers;
using Cavdeck.Domain.Storage;
using Cavdeck.Domain.Transfers;
using Cavdeck.Infrastructure.S3;
using Cavdeck.Infrastructure.Transfers;
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
            await VerifyManagementAsync(transfer, providers, credentials, pool, connection, bucket, key, head.ETag, token);

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

    private static async Task VerifyManagementAsync(S3ObjectTransfer transfer, S3ProviderFactory providers,
        Vault credentials, S3ClientLeasePool pool, StorageConnection source, string bucket, string key,
        string? etag, CancellationToken token)
    {
        var marker = key + ".dir/";
        var copy = key + ".copy";
        var moved = key + ".moved";
        var ownsMarker = false;
        var ownsCopy = false;
        var ownsMoved = false;
        var target = source with { Id = Guid.NewGuid(), Name = "integration target" };
        var explorer = new ExplorerService(providers, remote: transfer);
        using var signer = new S3DownloadLinkSigner(credentials, pool);
        await using var cleanupLease = await transfer.OpenAsync(source, write: true, token);
        var root = Directory.CreateTempSubdirectory("cavdeck-cli-cloud-").FullName;
        try
        {
            await explorer.CreateFolderMarkerAsync(source, bucket, marker, token);
            ownsMarker = true;
            var duplicate = await Assert.ThrowsAsync<StorageException>(() => explorer.CreateFolderMarkerAsync(source, bucket, marker, token));
            Assert.Equal(StorageFailureKind.PreconditionFailed, duplicate.Failure.Kind);

            var signed = await signer.SignDownloadAsync(source with { IsReadOnly = true }, bucket, key,
                TimeSpan.FromMinutes(1), token);
            Assert.False(signed.IsPublic);
            Assert.True(Uri.TryCreate(signed.Url, UriKind.Absolute, out _));
            using var http = new HttpClient();
            string downloaded;
            try { downloaded = await http.GetStringAsync(signed.Url, token); }
            catch (HttpRequestException) { throw new InvalidOperationException("The presigned GET failed."); }
            Assert.Equal("original", downloaded);

            var copied = await RunCopyAsync(key, copy, etag, move: false);
            ownsCopy = copied.State == TransferState.Completed;
            Assert.Equal(TransferState.Completed, copied.State);
            Assert.Equal("SHA256", copied.CrossTransfer?.Verification);
            var before = await cleanupLease.HeadAsync(bucket, copy, token);
            Assert.NotNull(before);
            if (source.Provider == StorageProviderKind.AwsS3 && source.Endpoint.Length == 0)
            {
                var conflict = await Assert.ThrowsAsync<StorageException>(() => cleanupLease.DeleteIfMatchAsync(bucket, copy,
                    "\"00000000000000000000000000000000\"", token));
                Assert.Equal(StorageFailureKind.PreconditionFailed, conflict.Failure.Kind);
                var movedJob = await RunCopyAsync(copy, moved, before.ETag, move: true);
                ownsMoved = movedJob.State == TransferState.Completed;
                Assert.Equal(TransferState.Completed, movedJob.State);
                Assert.Null(await cleanupLease.HeadAsync(bucket, copy, token));
                ownsCopy = false;
            }
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            if (ownsMarker)
                await cleanupLease.DeleteAsync(bucket, marker, cleanup.Token);
            if (ownsCopy)
                await cleanupLease.DeleteAsync(bucket, copy, cleanup.Token);
            if (ownsMoved)
                await cleanupLease.DeleteAsync(bucket, moved, cleanup.Token);
            Directory.Delete(root, recursive: true);
        }

        async Task<TransferJob> RunCopyAsync(string from, string to, string? expected, bool move)
        {
            using var journal = new SqliteTransferJournal(Path.Combine(root, Guid.NewGuid().ToString("N") + ".db"));
            await using var engine = new TransferEngine(transfer, journal, new Connections(source, target),
                new TransferEngineOptions { IsolatedRun = true });
            var run = await TransferRunCoordinator.RunAsync(engine, (worker, ct) =>
                worker.EnqueueCrossCopiesAsync(source, target, bucket, [new DownloadItem(from, false, 8, to, expected)],
                    bucket, "", TransferConflictPolicy.Fail, ct, move, exactTargets: true), progress: false, token);
            Assert.Null(run.SubmissionError);
            Assert.Null(run.BackgroundError);
            return Assert.Single(run.Jobs);
        }
    }

    private sealed class Connections(StorageConnection source, StorageConnection destination) : ITransferConnections
    {
        public Task<StorageConnection?> FindAsync(Guid id, CancellationToken token) =>
            Task.FromResult<StorageConnection?>(id == source.Id ? source : id == destination.Id ? destination : null);
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
