using Amazon.S3.Model;
using Cavdeck.Domain.Storage;

namespace Cavdeck.Infrastructure.S3;

internal interface IS3ConditionalWritePolicy
{
    void ConfigurePut(PutObjectRequest request, string? expectedETag, bool requireMissing);
    void EnsureConditionalReplaceSupported(string? ifMatchETag);
    void EnsureConditionalMultipartSupported();
    void ConfigureMultipartComplete(CompleteMultipartUploadRequest request,
        string? expectedETag, bool requireMissing);
}

internal static class S3ConditionalWritePolicies
{
    private static readonly IS3ConditionalWritePolicy Standard = new StandardS3ConditionalWritePolicy();
    private static readonly IS3ConditionalWritePolicy AlibabaOss = new AlibabaOssConditionalWritePolicy();

    public static IS3ConditionalWritePolicy For(S3Session session) =>
        IsAlibabaOssEndpoint(session.ServiceUrl) ? AlibabaOss : Standard;

    internal static bool IsAlibabaOssEndpoint(string? serviceUrl) =>
        Uri.TryCreate(serviceUrl, UriKind.Absolute, out var uri)
        && (uri.Host.Equals("oss.aliyuncs.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.StartsWith("oss-", StringComparison.OrdinalIgnoreCase)
                && uri.Host.EndsWith(".aliyuncs.com", StringComparison.OrdinalIgnoreCase));

    internal static void ValidateCondition(string? expectedETag, bool requireMissing)
    {
        if (requireMissing == (expectedETag is not null))
            throw new ArgumentException("Exactly one destination condition is required.");
    }
}

internal sealed class StandardS3ConditionalWritePolicy : IS3ConditionalWritePolicy
{
    public void ConfigurePut(PutObjectRequest request, string? expectedETag, bool requireMissing)
    {
        S3ConditionalWritePolicies.ValidateCondition(expectedETag, requireMissing);
        request.IfMatch = expectedETag;
        request.IfNoneMatch = requireMissing ? "*" : null;
    }

    public void EnsureConditionalReplaceSupported(string? ifMatchETag) { }

    public void EnsureConditionalMultipartSupported() { }

    public void ConfigureMultipartComplete(CompleteMultipartUploadRequest request,
        string? expectedETag, bool requireMissing)
    {
        S3ConditionalWritePolicies.ValidateCondition(expectedETag, requireMissing);
        request.IfMatch = expectedETag;
        request.IfNoneMatch = requireMissing ? "*" : null;
    }
}

internal sealed class AlibabaOssConditionalWritePolicy : IS3ConditionalWritePolicy
{
    public void ConfigurePut(PutObjectRequest request, string? expectedETag, bool requireMissing)
    {
        S3ConditionalWritePolicies.ValidateCondition(expectedETag, requireMissing);
        if (!requireMissing)
            throw UnsupportedOverwrite();

        // OSS rejects S3 conditional PUT headers. Its own no-overwrite header
        // protects new objects unless bucket versioning is enabled or suspended.
        request.Headers["x-oss-forbid-overwrite"] = "true";
    }

    public void EnsureConditionalReplaceSupported(string? ifMatchETag)
    {
        if (!string.IsNullOrWhiteSpace(ifMatchETag))
            throw UnsupportedOverwrite();
    }

    public void EnsureConditionalMultipartSupported() =>
        throw new StorageException(new StorageFailure(StorageFailureKind.Unsupported,
            "ConditionalMultipartUnsupported", null, null,
            "This OSS endpoint cannot complete a multipart upload with the required destination condition."));

    public void ConfigureMultipartComplete(CompleteMultipartUploadRequest request,
        string? expectedETag, bool requireMissing)
    {
        S3ConditionalWritePolicies.ValidateCondition(expectedETag, requireMissing);
        EnsureConditionalMultipartSupported();
    }

    private static StorageException UnsupportedOverwrite() =>
        new(new StorageFailure(StorageFailureKind.Unsupported,
            "ConditionalOverwriteUnsupported", null, null,
            "Alibaba Cloud OSS does not support conditional PutObject overwrites."));
}
