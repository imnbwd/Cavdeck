using Cavdeck.Domain.Storage;
using Xunit;

namespace Cavdeck.Domain.Tests;

public sealed class StorageFailureTests
{
    [Theory]
    [InlineData("InvalidAccessKeyId", 403, StorageFailureKind.InvalidCredentials)]
    [InlineData("SignatureDoesNotMatch", 403, StorageFailureKind.InvalidCredentials)]
    [InlineData("AccessDenied", 403, StorageFailureKind.AccessDenied)]
    [InlineData("AuthorizationHeaderMalformed", 400, StorageFailureKind.Endpoint)]
    [InlineData("NoSuchBucket", 404, StorageFailureKind.BucketNotFound)]
    [InlineData("PreconditionFailed", 412, StorageFailureKind.PreconditionFailed)]
    public void S3_codes_map_to_distinct_failures(string code, int status, StorageFailureKind expected)
    {
        var failure = StorageFailureClassifier.FromS3(code, status, "req-1", "raw");
        Assert.Equal(expected, failure.Kind);
        Assert.Equal(code, failure.ErrorCode);
        Assert.Equal("req-1", failure.RequestId);
    }

    [Fact]
    public void Missing_host_is_an_endpoint_problem()
    {
        var failure = StorageFailureClassifier.FromTransport("No such host is known.");
        Assert.Equal(StorageFailureKind.Endpoint, failure.Kind);
    }

    [Fact]
    public void Timeout_without_a_status_is_network()
    {
        var failure = StorageFailureClassifier.FromTransport("The storage service timed out.");
        Assert.Equal(StorageFailureKind.Network, failure.Kind);
    }

    [Fact]
    public void Redactor_removes_secrets_and_signed_query_values()
    {
        const string secret = "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY";
        var message = $"sig failed for {secret} X-Amz-Signature=abcdef0123456789&X-Amz-Credential={secret}";
        var redacted = SecretRedactor.Redact(message, [secret]);
        Assert.DoesNotContain(secret, redacted);
        Assert.DoesNotContain("abcdef0123456789", redacted);
        Assert.Contains("X-Amz-Signature=••••", redacted);
    }

    [Fact]
    public void Read_only_and_foreign_prefix_never_leave_the_guard()
    {
        var connection = new StorageConnection(
            Guid.NewGuid(), "n", StorageProviderKind.Minio, "http://127.0.0.1:9000", "us-east-1",
            "ref", IsReadOnly: true, KeyPrefix: "images/");

        var readOnly = Assert.Throws<StorageException>(() =>
            ObjectAccess.Ensure(connection, "bucket", "images/a.txt", write: true));
        Assert.Equal(StorageFailureKind.ReadOnly, readOnly.Failure.Kind);

        var prefix = Assert.Throws<StorageException>(() =>
            ObjectAccess.CombinePrefix(connection, "other/"));
        Assert.Equal("RestrictedPrefix", prefix.Failure.ErrorCode);
        Assert.Equal("images/2026/", ObjectAccess.CombinePrefix(connection, "images/2026/"));
    }
}
