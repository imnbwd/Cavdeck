using System.Security.Cryptography;
using System.Text;
using Cavdeck.Application.Abstractions;
using Cavdeck.Application.Preview;
using Cavdeck.Domain.Preview;
using Cavdeck.Domain.Storage;
using Cavdeck.Infrastructure.S3;
using Xunit;

namespace Cavdeck.Cli.Tests;

/// <summary>
/// Opt-in smoke tests against REAL AWS S3, Cloudflare R2 and MinIO endpoints.
/// The same CAVDECK_CLI_TEST_* variables used by CloudStorageTests are required.
/// All writes are confined to a unique folder inside cavdeck-cli-test/.
/// Skipping without credentials does NOT count as cloud acceptance.
/// </summary>
public sealed class PreviewCloudTests
{
    [PreviewCloudFact("AWS")]
    public Task AWS_preview_roundtrip() => VerifyAsync("AWS", StorageProviderKind.AwsS3);

    [PreviewCloudFact("R2")]
    public Task R2_preview_roundtrip() => VerifyAsync("R2", StorageProviderKind.CloudflareR2);

    [PreviewCloudFact("MINIO")]
    public Task MinIO_preview_roundtrip() => VerifyAsync("MINIO", StorageProviderKind.Minio);

    private static async Task VerifyAsync(string provider, StorageProviderKind kind)
    {
        var bucket = Read(provider, "BUCKET")!;
        var prefix = Read(provider, "PREFIX")!.TrimEnd('/') + "/";
        Assert.StartsWith("cavdeck-cli-test/", prefix, StringComparison.Ordinal);
        var root = prefix + "preview-" + Guid.NewGuid().ToString("N") + "/";
        var endpoint = Read(provider, "ENDPOINT") ?? "";
        var region = Read(provider, "REGION") ??
            (kind == StorageProviderKind.CloudflareR2 ? "auto" : "us-east-1");
        var connection = new StorageConnection(
            Guid.NewGuid(), "Preview Cloud QA", kind, endpoint, region,
            "preview-tests", RestrictedBucket: bucket, KeyPrefix: prefix,
            ForcePathStyle: kind != StorageProviderKind.AwsS3);

        var vault = new TestVault(Read(provider, "ACCESS_KEY")!, Read(provider, "SECRET_KEY")!);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var token = timeout.Token;
        using var pool = new S3ClientLeasePool();
        using var providers = new S3ProviderFactory(vault, pool);
        using var transfer = new S3ObjectTransfer(vault, pool);
        await using var writeLease = await transfer.OpenAsync(connection, write: true, token);
        writeLease.EnsureVerifiedConditionalPut();

        var temp = Directory.CreateTempSubdirectory("cavdeck-preview-cloud-").FullName;
        var service = new PreviewService(new ProviderPreviewContentReader(providers, temp), providers);
        var objects = providers.Create(kind);
        var text = Encoding.UTF8.GetBytes("Cavdeck 1.4 preview\n你好 · こんにちは · Grüße");
        var png = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVQIHWP4z8DwHwAFgAI/ScL2XQAAAABJRU5ErkJggg==");
        var pdf = CreatePdf();
        var wav = CreateWav();
        var video = Encoding.ASCII.GetBytes("preview-media-byte-range-smoke"); // Transport only; not a codec fixture.

        var fixtures = new[]
        {
            new Fixture("notes-中文.txt", "text/plain", text, PreviewLoadKind.Text),
            new Fixture("image.png", "image/png", png, PreviewLoadKind.Image),
            new Fixture("document.pdf", "application/pdf", pdf, PreviewLoadKind.Pdf),
            new Fixture("audio.wav", "audio/wav", wav, PreviewLoadKind.Media),
            new Fixture("video.mp4", "video/mp4", video, PreviewLoadKind.Media)
        };
        var created = new List<string>();
        try
        {
            foreach (var file in fixtures)
            {
                var key = root + file.Name;
                using var stream = new MemoryStream(file.Bytes, writable: false);
                // Register the unique fixture before upload, so a partial success is
                // still cleaned even if the API times out after committing it.
                created.Add(key);
                await writeLease.PutConditionalAsync(
                    bucket, key, stream, stream.Length, file.ContentType,
                    Convert.ToBase64String(MD5.HashData(file.Bytes)), null, true, token);
            }

            // Exercises HEAD, list, bounded GET, UTF-8 decoding and type recognition.
            foreach (var file in fixtures)
            {
                var key = root + file.Name;
                var head = await objects.HeadObjectAsync(connection, bucket, key, token);
                Assert.Equal(file.Bytes.Length, head.SizeBytes);
                var page = await objects.ListExactPageAsync(connection, bucket, key, true, null, 1, token);
                Assert.Equal(key, Assert.Single(page.Objects).Key);

                var result = await service.OpenAsync(connection, bucket, key, file.Name, token);
                Assert.Equal(file.Expected, result.Kind);
                if (file.Expected == PreviewLoadKind.Text)
                    Assert.Equal(Encoding.UTF8.GetString(file.Bytes), result.Text);
                else if (file.Expected == PreviewLoadKind.Image)
                    Assert.Equal(file.Bytes, result.Image);
                else
                {
                    Assert.Null(result.Image);
                    Assert.Null(result.Text);
                }

                // PDF and media are NOT eagerly buffered; their explicit download
                // must yield identical bytes, and dispose must remove the local copy.
                if (file.Expected is PreviewLoadKind.Pdf or PreviewLoadKind.Media)
                {
                    var lease = await service.PrepareFileAsync(
                        connection, bucket, key, file.Name, head.SizeBytes,
                        file.Expected == PreviewLoadKind.Pdf
                            ? PreviewRules.MaxPdfBytes : PreviewRules.MaxMediaBytes,
                        null, token);
                    Assert.True(File.Exists(lease.FilePath));
                    Assert.Equal(file.Bytes, await File.ReadAllBytesAsync(lease.FilePath, token));
                    await lease.DisposeAsync();
                    Assert.False(File.Exists(lease.FilePath));
                }
            }

            // Cancellation and failure must not leave orphan preview files.
            using (var canceled = new CancellationTokenSource())
            {
                canceled.Cancel();
                var file = fixtures[2];
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                    service.PrepareFileAsync(connection, bucket, root + file.Name,
                        file.Name, file.Bytes.Length, PreviewRules.MaxPdfBytes, null, canceled.Token));
            }
            Assert.Empty(Directory.EnumerateFiles(temp));
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            try
            {
                foreach (var key in created)
                    await writeLease.DeleteAsync(bucket, key, cleanup.Token);
            }
            finally
            {
                Directory.Delete(temp, recursive: true);
            }
        }
    }

    private sealed record Fixture(string Name, string ContentType, byte[] Bytes, PreviewLoadKind Expected);

    // A valid one-page PDF with a cross-reference table and no external resources.
    private static byte[] CreatePdf()
    {
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 200] /Contents 4 0 R >>",
            "<< /Length 0 >>\nstream\n\nendstream"
        };
        var builder = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Length; i++)
        {
            offsets.Add(Encoding.ASCII.GetByteCount(builder.ToString()));
            builder.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        var startXref = Encoding.ASCII.GetByteCount(builder.ToString());
        builder.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
            builder.Append($"{offset:0000000000} 00000 n \n");
        builder.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{startXref}\n%%EOF");
        return Encoding.ASCII.GetBytes(builder.ToString());
    }

    // Standard 8 kHz mono 16-bit PCM; playable without optional codecs.
    private static byte[] CreateWav()
    {
        const int samples = 800;
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + samples * 2);
        writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
        writer.Write(16); // PCM fmt size
        writer.Write((short)1); // PCM
        writer.Write((short)1); // Mono
        writer.Write(8000);
        writer.Write(16000);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(samples * 2);
        for (var i = 0; i < samples; i++)
            writer.Write((short)0);
        return stream.ToArray();
    }

    private static string? Read(string provider, string name) =>
        Environment.GetEnvironmentVariable($"CAVDECK_CLI_TEST_{provider}_{name}");

    private sealed class PreviewCloudFactAttribute : FactAttribute
    {
        public PreviewCloudFactAttribute(string provider)
        {
            var required = provider == "AWS"
                ? new[] { "BUCKET", "PREFIX", "ACCESS_KEY", "SECRET_KEY" }
                : new[] { "BUCKET", "PREFIX", "ACCESS_KEY", "SECRET_KEY", "ENDPOINT" };
            if (Read(provider, "ENABLED") != "1" ||
                required.Any(name => string.IsNullOrWhiteSpace(Read(provider, name))))
                Skip = $"Real {provider} preview test credentials and isolated bucket are not configured.";
        }
    }

    private sealed class TestVault(string accessKey, string secretKey) : ICredentialVault
    {
        public Task<(string AccessKeyId, string SecretAccessKey)?> ReadAsync(
            string reference, CancellationToken token) =>
            Task.FromResult<(string, string)?>((accessKey, secretKey));

        public Task SaveAsync(string reference, string accessKeyId, string secretAccessKey,
            CancellationToken token) => throw new NotSupportedException();

        public Task DeleteAsync(string reference, CancellationToken token) =>
            throw new NotSupportedException();
    }
}
