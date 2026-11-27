using Amazon.S3;
using Amazon.S3.Model.Internal.MarshallTransformations;
using Cavdeck.Domain.Storage;
using Cavdeck.Infrastructure.S3;
using Xunit;

namespace Cavdeck.Infrastructure.Tests;

public sealed class ObjectMetadataCopyTests
{
    [Theory]
    [InlineData(StorageProviderKind.AwsS3)]
    [InlineData(StorageProviderKind.Minio)]
    [InlineData(StorageProviderKind.CloudflareR2)]
    public void Copy_preserves_storage_and_uses_provider_specific_conditions(StorageProviderKind provider)
    {
        var values = new ObjectMetadataValues(new("text/plain", "no-cache", "inline", null, "en", null),
            new Dictionary<string, string> { ["project"] = "site" });
        var current = new ObjectMetadataSnapshot(new("assets", "folder/a.txt", 4, "\"etag\"", "text/plain"),
            DateTimeOffset.UtcNow, "STANDARD_IA", provider == StorageProviderKind.CloudflareR2 ? null : "AES256",
            null, null, values, MetadataEditBlock.None);
        var request = AwsS3Gateway.CreateMetadataCopy(provider, current, values);
        var wire = CopyObjectRequestMarshaller.Instance.Marshall(request);
        Assert.Equal("REPLACE", wire.Headers["x-amz-metadata-directive"]);
        Assert.Equal("site", wire.Headers["x-amz-meta-project"]);
        Assert.Equal("no-cache", wire.Headers["Cache-Control"]);
        Assert.Equal("\"etag\"", wire.Headers["x-amz-copy-source-if-match"]);
        Assert.Equal(request.SourceBucket, request.DestinationBucket);
        Assert.Equal(request.SourceKey, request.DestinationKey);
        Assert.Equal(S3MetadataDirective.REPLACE, request.MetadataDirective);
        Assert.Equal("STANDARD_IA", request.StorageClass.Value);
        Assert.Equal("no-cache", request.Headers.CacheControl);
        Assert.Equal("\"etag\"", request.ETagToMatch);
        if (provider == StorageProviderKind.CloudflareR2)
        {
            Assert.Null(request.IfMatch);
            Assert.Equal("\"etag\"", wire.Headers["cf-copy-destination-if-match"]);
            Assert.False(wire.Headers.ContainsKey("If-Match"));
            Assert.Equal("\"etag\"", request.Headers["cf-copy-destination-if-match"]);
        }
        else
        {
            Assert.Equal("\"etag\"", wire.Headers["If-Match"]);
            Assert.Equal("\"etag\"", request.IfMatch);
            Assert.Equal(TaggingDirective.COPY, request.TaggingDirective);
            Assert.Equal(ServerSideEncryptionMethod.AES256, request.ServerSideEncryptionMethod);
        }
    }
}
