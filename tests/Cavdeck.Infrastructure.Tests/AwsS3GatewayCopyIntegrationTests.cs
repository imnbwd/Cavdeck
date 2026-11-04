using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Cavdeck.Infrastructure.S3;
using Xunit;

namespace Cavdeck.Infrastructure.Tests;

public sealed class AwsS3GatewayCopyIntegrationTests
{
    [Fact]
    public async Task Multipart_copy_preserves_content_type_metadata_and_bytes_on_minio()
    {
        var endpoint = Environment.GetEnvironmentVariable("CAVDECK_TEST_MINIO_ENDPOINT");
        if (string.IsNullOrEmpty(endpoint))
            return;

        var access = Environment.GetEnvironmentVariable("CAVDECK_TEST_MINIO_ACCESS_KEY") ?? "testadmin";
        var secret = Environment.GetEnvironmentVariable("CAVDECK_TEST_MINIO_SECRET_KEY") ?? "testsecret123";
        var config = new AmazonS3Config
        {
            ServiceURL = endpoint,
            AuthenticationRegion = "us-east-1",
            ForcePathStyle = true,
            UseHttp = endpoint.StartsWith("http://", StringComparison.OrdinalIgnoreCase),
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED
        };
        using var client = new AmazonS3Client(new BasicAWSCredentials(access, secret), config);
        var bucket = "cavdeck-copy-" + Guid.NewGuid().ToString("N");
        await client.PutBucketAsync(new PutBucketRequest { BucketName = bucket });
        try
        {
            var bytes = new byte[6 * 1024 * 1024 + 7];
            for (var index = 0; index < bytes.Length; index++)
                bytes[index] = (byte)(index % 251);
            using var input = new MemoryStream(bytes);
            var put = new PutObjectRequest
            {
                BucketName = bucket,
                Key = "source.bin",
                InputStream = input,
                ContentType = "application/x-cavdeck-test",
                AutoCloseStream = false
            };
            put.Metadata.Add("sample", "copied");
            await client.PutObjectAsync(put);

            var session = new S3Session
            {
                AccessKeyId = access,
                SecretAccessKey = secret,
                Region = "us-east-1",
                ServiceUrl = endpoint,
                ForcePathStyle = true
            };
            var gateway = new AwsS3Gateway(singleCopyLimit: 5 * 1024 * 1024);
            await gateway.CopyObjectAsync(session, bucket, "source.bin", bucket, "target.bin", CancellationToken.None);

            using var output = await client.GetObjectAsync(bucket, "target.bin");
            using var buffer = new MemoryStream();
            await output.ResponseStream.CopyToAsync(buffer);
            Assert.Equal(bytes, buffer.ToArray());
            Assert.Equal("application/x-cavdeck-test", output.Headers.ContentType);
            Assert.Equal("copied", output.Metadata["sample"]);
        }
        finally
        {
            await client.DeleteObjectAsync(bucket, "target.bin");
            await client.DeleteObjectAsync(bucket, "source.bin");
            await client.DeleteBucketAsync(bucket);
        }
    }
}
