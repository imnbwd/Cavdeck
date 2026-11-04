using Amazon.S3.Model;
using Cavdeck.Domain.Storage;
using Cavdeck.Domain.Transfers;
using Cavdeck.Infrastructure.S3;
using Xunit;

namespace Cavdeck.Infrastructure.Tests;

public sealed class OssUploadCompatibilityTests
{
    [Theory]
    [InlineData("https://oss-cn-beijing.aliyuncs.com")]
    [InlineData("https://oss-cn-beijing-internal.aliyuncs.com")]
    public void Oss_new_object_uses_provider_no_overwrite_header(string endpoint)
    {
        var request = new PutObjectRequest();

        Policy(endpoint).ConfigurePut(request, null, requireMissing: true);

        Assert.Null(request.IfNoneMatch);
        Assert.Null(request.IfMatch);
        Assert.Equal("true", request.Headers["x-oss-forbid-overwrite"]);
    }

    [Fact]
    public void Other_s3_endpoints_keep_standard_conditions()
    {
        var create = new PutObjectRequest();
        var policy = Policy("https://example.r2.cloudflarestorage.com");
        policy.ConfigurePut(create, null, requireMissing: true);
        Assert.Equal("*", create.IfNoneMatch);
        Assert.Null(create.Headers["x-oss-forbid-overwrite"]);

        var overwrite = new PutObjectRequest();
        policy.ConfigurePut(overwrite, "etag", requireMissing: false);
        Assert.Equal("etag", overwrite.IfMatch);

        var multipart = new CompleteMultipartUploadRequest();
        policy.ConfigureMultipartComplete(multipart, null, requireMissing: true);
        Assert.Equal("*", multipart.IfNoneMatch);
    }

    [Fact]
    public void Oss_existing_object_is_not_overwritten_without_an_atomic_condition()
    {
        var error = Assert.Throws<StorageException>(() => Policy("https://oss-cn-beijing.aliyuncs.com")
            .ConfigurePut(new PutObjectRequest(), "etag", requireMissing: false));

        var failure = TransferFailures.FromStorage(error);
        Assert.Equal(TransferFailureKind.Unsupported, failure.Kind);
        Assert.False(failure.Retryable);
    }

    [Fact]
    public void Oss_conditional_multipart_fails_before_an_upload_is_started()
    {
        var error = Assert.Throws<StorageException>(() => Policy("https://oss-cn-beijing.aliyuncs.com")
            .EnsureConditionalMultipartSupported());

        var failure = TransferFailures.FromStorage(error);
        Assert.Equal("ConditionalMultipartUnsupported", failure.Code);
        Assert.Equal(TransferFailureKind.Unsupported, failure.Kind);
        Assert.False(failure.Retryable);
    }

    [Fact]
    public async Task Oss_preview_save_rejects_conditional_overwrite_before_sending_a_request()
    {
        var session = new S3Session
        {
            AccessKeyId = "test",
            SecretAccessKey = "test",
            Region = "cn-beijing",
            ServiceUrl = "https://oss-cn-beijing.aliyuncs.com"
        };
        using var content = new MemoryStream([1, 2, 3]);

        var error = await Assert.ThrowsAsync<StorageException>(() => new AwsS3Gateway()
            .ReplaceObjectAsync(session, "bucket", "note.txt", content, "text/plain", "etag",
                CancellationToken.None));

        Assert.Equal("ConditionalOverwriteUnsupported", error.Failure.ErrorCode);
        Assert.Equal(0, content.Position);
    }

    private static IS3ConditionalWritePolicy Policy(string endpoint) =>
        S3ConditionalWritePolicies.For(new S3Session
        {
            AccessKeyId = "test",
            SecretAccessKey = "test",
            Region = "cn-beijing",
            ServiceUrl = endpoint
        });
}
