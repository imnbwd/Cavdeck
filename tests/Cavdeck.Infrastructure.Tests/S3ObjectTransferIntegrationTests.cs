using System.Text;
using System.Security.Cryptography;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Cavdeck.Application.Abstractions;
using Cavdeck.Application.Explorer;
using Cavdeck.Application.Transfers;
using Cavdeck.Domain.Storage;
using Cavdeck.Domain.Transfers;
using Cavdeck.Infrastructure.S3;
using Xunit;

namespace Cavdeck.Infrastructure.Tests;

public sealed class S3ObjectTransferIntegrationTests
{
    [Fact]
    public async Task Minio_resolves_a_copied_uri_and_empty_marker_across_buckets()
    {
        var endpoint = Environment.GetEnvironmentVariable("CAVDECK_TEST_MINIO_ENDPOINT");
        if (string.IsNullOrEmpty(endpoint))
            return;

        var access = Environment.GetEnvironmentVariable("CAVDECK_TEST_MINIO_ACCESS_KEY") ?? "testadmin";
        var secret = Environment.GetEnvironmentVariable("CAVDECK_TEST_MINIO_SECRET_KEY") ?? "testsecret123";
        using var setup = new AmazonS3Client(new BasicAWSCredentials(access, secret), new AmazonS3Config
        {
            ServiceURL = endpoint, AuthenticationRegion = "us-east-1", ForcePathStyle = true,
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED
        });
        var source = "cavdeck-path-source-" + Guid.NewGuid().ToString("N");
        var target = "cavdeck-path-target-" + Guid.NewGuid().ToString("N");
        const string file = "scope/資料%20 report.txt";
        const string marker = "scope/empty/";
        var created = new List<string>();
        try
        {
            foreach (var bucket in new[] { source, target })
            {
                await setup.PutBucketAsync(new PutBucketRequest { BucketName = bucket });
                created.Add(bucket);
            }
            await setup.PutObjectAsync(new PutObjectRequest { BucketName = target, Key = file, ContentBody = "test" });
            using var markerData = new MemoryStream();
            await setup.PutObjectAsync(new PutObjectRequest { BucketName = target, Key = marker, InputStream = markerData });

            var connection = new StorageConnection(Guid.NewGuid(), "MinIO", StorageProviderKind.Minio,
                endpoint, "us-east-1", "test", IsReadOnly: true, KeyPrefix: "scope/", ForcePathStyle: true);
            using var factory = new S3ProviderFactory(new MemoryVault(access, secret));
            var explorer = new ExplorerService(factory);

            var resolved = await explorer.ResolvePathAsync(connection, source, "scope/",
                ObjectLayout.ObjectUri(target, file), default);
            Assert.Equal(target, resolved?.Bucket);
            Assert.Equal(file, resolved?.File?.Key);
            Assert.Equal("scope/", resolved?.Listing.Prefix);

            var empty = await explorer.ResolvePathAsync(connection, source, "scope/",
                new ExplorerPathInput(target + "/empty/", BucketPath: true), default);
            Assert.Equal(target, empty?.Bucket);
            Assert.Equal(marker, empty?.Listing.Prefix);
            Assert.Empty(empty!.Listing.Entries);

            var denied = await Assert.ThrowsAsync<StorageException>(() => explorer.ResolvePathAsync(
                connection with { RestrictedBucket = source }, source, "scope/", ObjectLayout.ObjectUri(target, file), default));
            Assert.Equal("RestrictedBucket", denied.Failure.ErrorCode);
        }
        finally
        {
            if (created.Contains(target))
            {
                await setup.DeleteObjectAsync(target, file);
                await setup.DeleteObjectAsync(target, marker);
            }
            foreach (var bucket in created)
                await setup.DeleteBucketAsync(bucket);
        }
    }

    [Fact]
    public async Task Minio_ranged_response_and_multipart_roundtrip()
    {
        var endpoint = Environment.GetEnvironmentVariable("CAVDECK_TEST_MINIO_ENDPOINT");
        if (string.IsNullOrEmpty(endpoint))
            return;

        var accessKey = Environment.GetEnvironmentVariable("CAVDECK_TEST_MINIO_ACCESS_KEY") ?? "testadmin";
        var secretKey = Environment.GetEnvironmentVariable("CAVDECK_TEST_MINIO_SECRET_KEY") ?? "testsecret123";
        var config = new AmazonS3Config
        {
            ServiceURL = endpoint,
            AuthenticationRegion = "us-east-1",
            ForcePathStyle = true,
            UseHttp = endpoint.StartsWith("http://", StringComparison.OrdinalIgnoreCase),
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED
        };
        using var setup = new AmazonS3Client(new BasicAWSCredentials(accessKey, secretKey), config);
        var bucket = "cavdeck-test-" + Guid.NewGuid().ToString("N");
        await setup.PutBucketAsync(new PutBucketRequest { BucketName = bucket });
        var local = Path.Combine(Path.GetTempPath(), "cavdeck-minio-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(local);
        try
        {
            var bytes = Encoding.UTF8.GetBytes("hello-world");
            await setup.PutObjectAsync(new PutObjectRequest
            {
                BucketName = bucket,
                Key = "range.txt",
                InputStream = new MemoryStream(bytes),
                AutoCloseStream = true
            });
            await setup.PutObjectAsync(new PutObjectRequest
            {
                BucketName = bucket,
                Key = "md5.txt",
                InputStream = new MemoryStream(bytes),
                AutoCloseStream = true,
                MD5Digest = Convert.ToBase64String(MD5.HashData(bytes))
            });

            using var transfer = new S3ObjectTransfer(new MemoryVault(accessKey, secretKey));
            var connection = new StorageConnection(Guid.NewGuid(), "MinIO", StorageProviderKind.Minio,
                endpoint, "us-east-1", "test", ForcePathStyle: true);
            await using var lease = await transfer.OpenAsync(connection, write: false, CancellationToken.None);
            var head = await lease.HeadAsync(bucket, "range.txt", CancellationToken.None);
            Assert.NotNull(head);
            await using var body = await lease.OpenDownloadAsync(bucket, "range.txt", 6, CancellationToken.None);
            using var output = new MemoryStream();
            await body.Content.CopyToAsync(output);

            Assert.True(body.RangeHonored);
            Assert.Equal(head.Size, body.Identity.Size);
            Assert.True(body.Identity.SameContent(head));
            Assert.Equal("world", Encoding.UTF8.GetString(output.ToArray()));
            await using (var exact = await lease.OpenRangeAsync(bucket, "range.txt", 2, 4,
                head.ETag!, null, CancellationToken.None))
            {
                using var fixedOutput = new MemoryStream();
                await exact.Content.CopyToAsync(fixedOutput);
                Assert.Equal("llo-", Encoding.UTF8.GetString(fixedOutput.ToArray()));
            }

            var content = new byte[6 * 1024 * 1024 + 37];
            for (var index = 0; index < content.Length; index++)
                content[index] = (byte)(index % 251);
            var source = Path.Combine(local, "large.bin");
            await File.WriteAllBytesAsync(source, content);
            var journal = new MemoryTransferJournal();
            var resumedJob = TransferJob.Create(TransferKind.Upload, connection.Id, bucket,
                "resumed.bin", source, content.Length, TransferConflictPolicy.Overwrite);
            await using (var writeLease = await transfer.OpenAsync(connection, write: true, CancellationToken.None))
            {
                var uploadId = await writeLease.InitiateMultipartAsync(bucket, "resumed.bin", "application/octet-stream", CancellationToken.None);
                var firstPart = content[..(5 * 1024 * 1024)];
                await writeLease.UploadPartAsync(bucket, "resumed.bin", uploadId, 1,
                    new MemoryStream(firstPart), firstPart.Length,
                    Convert.ToBase64String(MD5.HashData(firstPart)), null, CancellationToken.None);
                resumedJob = resumedJob with
                {
                    State = TransferState.Running,
                    UploadId = uploadId,
                    Resume = ResumeMode.Multipart,
                    SourceModifiedAt = new DateTimeOffset(File.GetLastWriteTimeUtc(source))
                };
                await journal.UpsertAsync(resumedJob, CancellationToken.None);
            }

            var engine = new TransferEngine(transfer, journal, new SingleConnection(connection),
                new TransferEngineOptions
                {
                    MultipartThresholdBytes = 5 * 1024 * 1024,
                    PartSizeBytes = 5 * 1024 * 1024,
                    MaxConcurrentParts = 2
                });
            await using (engine)
            {
                await engine.StartAsync();
                await WaitCompletedAsync(engine, resumedJob.Id);
                Assert.Equal(content.Length, (await lease.HeadAsync(bucket, "resumed.bin", CancellationToken.None))!.Size);
                var smallSource = Path.Combine(local, "small.bin");
                await File.WriteAllTextAsync(smallSource, "verified-small-upload");
                var smallUploads = await engine.EnqueueUploadsAsync(connection, bucket, "", [smallSource], TransferConflictPolicy.Overwrite);
                await WaitCompletedAsync(engine, smallUploads[0].Id);
                var smallHead = await lease.HeadAsync(bucket, "small.bin", CancellationToken.None);
                Assert.Equal(new FileInfo(smallSource).Length, smallHead!.Size);

                var uploads = await engine.EnqueueUploadsAsync(connection, bucket, "", [source], TransferConflictPolicy.Overwrite);
                await WaitCompletedAsync(engine, uploads[0].Id);
                var readonlySource = connection with { Id = Guid.NewGuid(), IsReadOnly = true };
                await using (var cross = new TransferEngine(transfer, new MemoryTransferJournal(),
                    new TwoConnections(readonlySource, connection),
                    new TransferEngineOptions { CrossCopyPartSizeBytes = 5 * 1024 * 1024 }))
                {
                    await cross.StartAsync();
                    var copies = await cross.EnqueueCrossCopiesAsync(readonlySource, connection, bucket,
                        [new DownloadItem("large.bin", false, content.Length)], bucket, "copied/",
                        TransferConflictPolicy.Skip);
                    await WaitCompletedAsync(cross, copies[0].Id);
                    Assert.Equal("SHA256", cross.Current(copies[0].Id).CrossTransfer?.Verification);
                    Assert.Equal(content.Length, (await lease.HeadAsync(bucket, "copied/large.bin", CancellationToken.None))?.Size);
                }
                var downloads = await engine.EnqueueDownloadsAsync(connection, bucket,
                    [new DownloadItem("large.bin", false, content.Length)], Path.Combine(local, "out"), TransferConflictPolicy.Overwrite);
                await WaitCompletedAsync(engine, downloads[0].Id);
                Assert.Equal(content, await File.ReadAllBytesAsync(downloads[0].LocalPath));
            }
        }
        finally
        {
            await setup.DeleteObjectAsync(bucket, "range.txt");
            await setup.DeleteObjectAsync(bucket, "md5.txt");
            await setup.DeleteObjectAsync(bucket, "small.bin");
            await setup.DeleteObjectAsync(bucket, "large.bin");
            await setup.DeleteObjectAsync(bucket, "resumed.bin");
            await setup.DeleteObjectAsync(bucket, "copied/large.bin");
            await setup.DeleteBucketAsync(bucket);
            Directory.Delete(local, true);
        }
    }

    private static async Task WaitCompletedAsync(TransferEngine engine, Guid id)
    {
        var timeout = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < timeout)
        {
            var job = engine.Current(id);
            if (job.State == TransferState.Completed)
                return;
            if (job.State == TransferState.Failed)
                throw new Xunit.Sdk.XunitException($"Transfer failed: {job.Failure?.Kind} {job.Failure?.Code} {job.Failure?.Detail}; uploadId={job.UploadId}; bytes={job.BytesTransferred}");
            await Task.Delay(50);
        }
        throw new TimeoutException("MinIO transfer did not complete.");
    }

    private sealed class SingleConnection(StorageConnection connection) : ITransferConnections
    {
        public Task<StorageConnection?> FindAsync(Guid id, CancellationToken cancellationToken) =>
            Task.FromResult<StorageConnection?>(id == connection.Id ? connection : null);
    }

    private sealed class TwoConnections(StorageConnection source, StorageConnection destination) : ITransferConnections
    {
        public Task<StorageConnection?> FindAsync(Guid id, CancellationToken cancellationToken) =>
            Task.FromResult<StorageConnection?>(id == source.Id ? source
                : id == destination.Id ? destination : null);
    }

    private sealed class MemoryVault(string accessKey, string secretKey) : ICredentialVault
    {
        public Task SaveAsync(string reference, string accessKeyId, string secretAccessKey,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<(string AccessKeyId, string SecretAccessKey)?> ReadAsync(
            string reference, CancellationToken cancellationToken) =>
            Task.FromResult<(string AccessKeyId, string SecretAccessKey)?>((accessKey, secretKey));

        public Task DeleteAsync(string reference, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
