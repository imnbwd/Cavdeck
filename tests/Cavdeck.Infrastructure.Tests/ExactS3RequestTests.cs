using System.Net;
using System.Net.Http.Headers;
using Amazon.Runtime;
using Amazon.S3;
using Cavdeck.Application.Abstractions;
using Cavdeck.Domain.Storage;
using Cavdeck.Infrastructure.S3;
using Xunit;

namespace Cavdeck.Infrastructure.Tests;

public sealed class ExactS3RequestTests
{
    [Fact]
    public async Task HeadSendsExactEscapedKeyAndNeverRequestsTheBody()
    {
        const string key = "space 空%2F//a\\b#? ";
        var handler = new RecordingHandler();
        using var pool = new S3ClientLeasePool(_ => CreateClient(handler));
        using var factory = new S3ProviderFactory(new Vault(), pool);
        var provider = factory.Create(StorageProviderKind.Minio);
        var result = await provider.HeadObjectAsync(Connection, "bucket", key, default);
        Assert.Equal(key, result.Key);
        Assert.Equal(HttpMethod.Head, handler.Method);
        Assert.Equal("/bucket/space%20%E7%A9%BA%252F//a%5Cb%23%3F%20", handler.Uri!.AbsolutePath);
    }

    [Fact]
    public async Task ConditionalPutSendsIfNoneMatchAndPreservesKey()
    {
        var handler = new RecordingHandler();
        using var pool = new S3ClientLeasePool(_ => CreateClient(handler));
        using var transfer = new S3ObjectTransfer(new Vault(), pool);
        await using var lease = await transfer.OpenAsync(Connection, write: true, default);
        lease.EnsureVerifiedConditionalPut();
        using var source = new MemoryStream([1, 2, 3]);
        await lease.PutConditionalAsync("bucket", "a//%2F.txt", source, source.Length, "text/plain",
            Convert.ToBase64String(System.Security.Cryptography.MD5.HashData(source.ToArray())), null, true, default);

        Assert.Equal(HttpMethod.Put, handler.Method);
        Assert.Equal("*", handler.IfNoneMatch);
        Assert.Equal("/bucket/a//%252F.txt", handler.Uri!.AbsolutePath);
    }

    private static readonly StorageConnection Connection = new(Guid.NewGuid(), "Local", StorageProviderKind.Minio,
        "https://storage.example.test", "us-east-1", "fictional", ForcePathStyle: true);

    private static AmazonS3Client CreateClient(RecordingHandler handler) =>
        new(new BasicAWSCredentials("fictional-access", "fictional-secret"), new AmazonS3Config
        {
            ServiceURL = Connection.Endpoint, AuthenticationRegion = Connection.Region, ForcePathStyle = true,
            HttpClientFactory = new Factory(handler),
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED
        });

    private sealed class Factory(RecordingHandler handler) : HttpClientFactory
    {
        public override HttpClient CreateHttpClient(IClientConfig clientConfig) => new(handler, disposeHandler: false);
        public override bool UseSDKHttpClientCaching(IClientConfig clientConfig) => false;
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public HttpMethod? Method { get; private set; }
        public Uri? Uri { get; private set; }
        public string? IfNoneMatch { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Method = request.Method;
            Uri = request.RequestUri;
            IfNoneMatch = request.Headers.TryGetValues("If-None-Match", out var values) ? values.Single() : null;
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };
            response.Headers.ETag = new EntityTagHeaderValue("\"fictional-etag\"");
            return Task.FromResult(response);
        }
    }

    private sealed class Vault : ICredentialVault
    {
        public Task<(string AccessKeyId, string SecretAccessKey)?> ReadAsync(string reference, CancellationToken token) =>
            Task.FromResult<(string, string)?>(("fictional-access", "fictional-secret"));
        public Task SaveAsync(string reference, string key, string secret, CancellationToken token) => throw new NotSupportedException();
        public Task DeleteAsync(string reference, CancellationToken token) => throw new NotSupportedException();
    }
}
