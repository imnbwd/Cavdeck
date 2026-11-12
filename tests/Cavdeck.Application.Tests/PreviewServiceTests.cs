using System.Text;
using Cavdeck.Application.Abstractions;
using Cavdeck.Application.Preview;
using Cavdeck.Domain.Preview;
using Cavdeck.Domain.Storage;
using Xunit;

namespace Cavdeck.Application.Tests;

public sealed class PreviewServiceTests
{
    [Fact]
    public async Task Open_decodes_text_and_keeps_the_etag()
    {
        var store = new PreviewStore
        {
            Head = Head(12, "\"abc\"", "application/json"),
            Body = """{"ok":true}"""u8.ToArray()
        };
        var opened = await new PreviewService(new SingleFactory(store)).OpenAsync(
            Connection(), "assets", "notes.json", "notes.json", CancellationToken.None);

        Assert.Equal(PreviewLoadKind.Text, opened.Kind);
        Assert.Equal("""{"ok":true}""", opened.Text);
        Assert.Equal("\"abc\"", opened.Head?.ETag);
        Assert.Equal("application/json", opened.Head?.ContentType);
        Assert.Equal(1, store.Reads);
    }

    [Fact]
    public async Task Open_does_not_download_a_text_file_over_the_cap()
    {
        var store = new PreviewStore { Head = Head(PreviewRules.MaxTextBytes + 1, "\"big\"", "text/plain") };
        var opened = await new PreviewService(new SingleFactory(store)).OpenAsync(
            Connection(), "assets", "notes.txt", "notes.txt", CancellationToken.None);

        Assert.Equal(PreviewLoadKind.TooLarge, opened.Kind);
        Assert.Equal(0, store.Reads);
    }

    [Fact]
    public async Task Immersive_image_allows_explicit_read_above_side_pane_limit()
    {
        var head = Head(PreviewRules.MaxImageBytes + 1, "\"image\"", "image/jpeg");
        var store = new PreviewStore { Head = head, Body = [0xFF, 0xD8, 0xFF] };
        var service = new PreviewService(new SingleFactory(store));

        var sidePane = await service.OpenAsync(
            Connection(), "assets", "large.jpg", "large.jpg", CancellationToken.None);
        var immersive = await service.OpenImageAsync(
            Connection(), "assets", "large.jpg", "large.jpg", CancellationToken.None);

        Assert.Equal(PreviewLoadKind.TooLarge, sidePane.Kind);
        Assert.Equal(PreviewLoadKind.Image, immersive.Kind);
        Assert.Equal(1, store.Reads);
    }

    [Fact]
    public async Task Immersive_image_rejects_large_sources_without_reading_body()
    {
        var store = new PreviewStore
        {
            Head = Head(PreviewRules.MaxImmersiveImageBytes + 1, "\"large\"", "image/png")
        };
        var opened = await new PreviewService(new SingleFactory(store)).OpenImageAsync(
            Connection(), "assets", "huge.png", "huge.png", CancellationToken.None);

        Assert.Equal(PreviewLoadKind.TooLarge, opened.Kind);
        Assert.Equal(0, store.Reads);
    }

    [Fact]
    public async Task Immersive_image_rejects_non_images_even_with_a_small_body()
    {
        var store = new PreviewStore
        {
            Head = Head(10, "\"text\"", "text/plain"), Body = "hello"u8.ToArray()
        };
        var opened = await new PreviewService(new SingleFactory(store)).OpenImageAsync(
            Connection(), "assets", "notes.txt", "notes.txt", CancellationToken.None);

        Assert.Equal(PreviewLoadKind.External, opened.Kind);
        Assert.Equal(0, store.Reads);
    }

    [Theory]
    [InlineData("song.mp3", "audio/mpeg")]
    [InlineData("clip.mp4", "video/mp4")]
    public async Task Selecting_media_returns_metadata_without_downloading(string file, string contentType)
    {
        var store = new PreviewStore { Head = Head(50_000_000, "\"media\"", contentType) };
        var opened = await new PreviewService(new SingleFactory(store)).OpenAsync(
            Connection(), "assets", file, file, CancellationToken.None);

        Assert.Equal(PreviewLoadKind.Media, opened.Kind);
        Assert.Equal(0, store.Reads);
        Assert.Null(opened.Image);
    }

    [Fact]
    public async Task Oversized_media_is_rejected_before_reading()
    {
        var store = new PreviewStore
        {
            Head = Head(PreviewRules.MaxMediaBytes + 1, "\"large\"", "video/mp4")
        };
        var opened = await new PreviewService(new SingleFactory(store)).OpenAsync(
            Connection(), "assets", "huge.mp4", "huge.mp4", CancellationToken.None);
        Assert.Equal(PreviewLoadKind.TooLarge, opened.Kind);
        Assert.Equal(0, store.Reads);
    }

    [Fact]
    public async Task Pdf_returns_metadata_without_buffering_body()
    {
        var store = new PreviewStore { Head = Head(20_000_000, "\"pdf\"", "application/pdf") };
        var opened = await new PreviewService(new SingleFactory(store)).OpenAsync(
            Connection(), "assets", "report.pdf", "report.pdf", CancellationToken.None);
        Assert.Equal(PreviewLoadKind.Pdf, opened.Kind);
        Assert.Equal(0, store.Reads);
        Assert.Null(opened.Image);
    }

    [Fact]
    public async Task Oversize_pdf_does_not_download()
    {
        var store = new PreviewStore { Head = Head(PreviewRules.MaxPdfBytes + 1, "\"big\"", "application/pdf") };
        var opened = await new PreviewService(new SingleFactory(store)).OpenAsync(
            Connection(), "assets", "report.pdf", "report.pdf", CancellationToken.None);
        Assert.Equal(PreviewLoadKind.TooLarge, opened.Kind);
        Assert.Equal(0, store.Reads);
    }

    [Fact]
    public async Task Bounded_preparation_streams_to_file_and_release_cleans_it()
    {
        var store = new PreviewStore { Body = "%PDF-1.7"u8.ToArray() };
        var root = TempDirectory();
        try
        {
            var downloader = new PreviewFileDownloader(new SingleFactory(store), root);
            var progress = new RecordedProgress();
            var file = await downloader.PrepareAsync(
                Connection(), "assets", "report.pdf", "report.pdf",
                store.Body.Length, PreviewRules.MaxPdfBytes, progress, CancellationToken.None);
            Assert.EndsWith(".pdf", file);
            Assert.Equal(store.Body, await File.ReadAllBytesAsync(file));
            Assert.Equal(store.Body.Length, progress.LastBytes);
            downloader.Release(file);
            Assert.False(File.Exists(file));
        }
        finally { RemoveDirectory(root); }
    }

    [Fact]
    public async Task Provider_reader_returns_an_idempotent_lease_for_a_prepared_file()
    {
        var store = new PreviewStore { Body = "%PDF-1.7"u8.ToArray() };
        var root = TempDirectory();
        try
        {
            var reader = new ProviderPreviewContentReader(new SingleFactory(store), root);
            var lease = await reader.PrepareFileAsync(
                Connection(), "assets", "report.pdf", "report.pdf",
                store.Body.Length, PreviewRules.MaxPdfBytes, null, CancellationToken.None);
            var path = lease.FilePath;

            Assert.True(File.Exists(path));
            Assert.Equal(store.Body.Length, lease.Length);
            Assert.Equal(store.Body, await File.ReadAllBytesAsync(path));

            await lease.DisposeAsync();
            await lease.DisposeAsync();
            Assert.False(File.Exists(path));
        }
        finally { RemoveDirectory(root); }
    }

    [Fact]
    public async Task Active_file_lease_is_not_removed_by_stale_copy_pruning()
    {
        var store = new PreviewStore { Body = "%PDF-1.7"u8.ToArray() };
        var root = TempDirectory();
        try
        {
            var reader = new ProviderPreviewContentReader(new SingleFactory(store), root);
            var active = await reader.PrepareFileAsync(
                Connection(), "assets", "active.pdf", "active.pdf",
                store.Body.Length, PreviewRules.MaxPdfBytes, null, CancellationToken.None);
            File.SetLastWriteTimeUtc(active.FilePath, DateTime.UtcNow.AddDays(-3));

            var next = await reader.PrepareFileAsync(
                Connection(), "assets", "next.pdf", "next.pdf",
                store.Body.Length, PreviewRules.MaxPdfBytes, null, CancellationToken.None);

            Assert.True(File.Exists(active.FilePath));
            await active.DisposeAsync();
            await next.DisposeAsync();
            Assert.False(File.Exists(active.FilePath));
        }
        finally { RemoveDirectory(root); }
    }

    [Fact]
    public async Task Detaching_a_file_lease_preserves_the_external_copy_for_retention_cleanup()
    {
        var store = new PreviewStore { Body = "%PDF-1.7"u8.ToArray() };
        var root = TempDirectory();
        try
        {
            var reader = new ProviderPreviewContentReader(new SingleFactory(store), root);
            var lease = await reader.PrepareFileAsync(
                Connection(), "assets", "external.pdf", "external.pdf",
                store.Body.Length, PreviewRules.MaxPdfBytes, null, CancellationToken.None);
            var path = lease.Detach();

            await lease.DisposeAsync();
            Assert.True(File.Exists(path));
            File.Delete(path);
        }
        finally { RemoveDirectory(root); }
    }

    [Fact]
    public void Cleanup_stale_files_removes_only_expired_managed_copies()
    {
        var root = TempDirectory();
        try
        {
            var stale = CreatePreviewCopy(root, "stale.pdf", DateTime.UtcNow.AddHours(-49));
            var recent = CreatePreviewCopy(root, "recent.pdf", DateTime.UtcNow.AddHours(-24));
            var unrelated = Path.Combine(root, "notes.txt");
            File.WriteAllText(unrelated, "keep");
            File.SetLastWriteTimeUtc(unrelated, DateTime.UtcNow.AddDays(-7));

            var downloader = new PreviewFileDownloader(new SingleFactory(new PreviewStore()), root);
            downloader.CleanupStaleFiles();

            Assert.False(File.Exists(stale));
            Assert.True(File.Exists(recent));
            Assert.True(File.Exists(unrelated));
        }
        finally { RemoveDirectory(root); }
    }

    [Fact]
    public void Cleanup_stale_files_does_not_create_a_missing_directory()
    {
        var root = TempDirectory();
        try
        {
            var reader = new ProviderPreviewContentReader(new SingleFactory(new PreviewStore()), root);

            reader.CleanupStaleFiles();

            Assert.False(Directory.Exists(root));
        }
        finally { RemoveDirectory(root); }
    }

    [Fact]
    public async Task Cleanup_stale_files_preserves_active_leases()
    {
        var store = new PreviewStore { Body = "%PDF-1.7"u8.ToArray() };
        var root = TempDirectory();
        try
        {
            var reader = new ProviderPreviewContentReader(new SingleFactory(store), root);
            var lease = await reader.PrepareFileAsync(
                Connection(), "assets", "active.pdf", "active.pdf",
                store.Body.Length, PreviewRules.MaxPdfBytes, null, CancellationToken.None);
            try
            {
                File.SetLastWriteTimeUtc(lease.FilePath, DateTime.UtcNow.AddHours(-49));

                reader.CleanupStaleFiles();

                Assert.True(File.Exists(lease.FilePath));
            }
            finally
            {
                await lease.DisposeAsync();
            }
        }
        finally { RemoveDirectory(root); }
    }

    [Fact]
    public async Task Cleanup_stale_files_removes_expired_detached_copies()
    {
        var store = new PreviewStore { Body = "%PDF-1.7"u8.ToArray() };
        var root = TempDirectory();
        try
        {
            var reader = new ProviderPreviewContentReader(new SingleFactory(store), root);
            var lease = await reader.PrepareFileAsync(
                Connection(), "assets", "external.pdf", "external.pdf",
                store.Body.Length, PreviewRules.MaxPdfBytes, null, CancellationToken.None);
            var path = lease.Detach();
            await lease.DisposeAsync();
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours(-49));

            reader.CleanupStaleFiles();

            Assert.False(File.Exists(path));
        }
        finally { RemoveDirectory(root); }
    }

    [Fact]
    public void Cleanup_stale_files_skips_locked_files_and_retries_later()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var root = TempDirectory();
        try
        {
            var stale = CreatePreviewCopy(root, "locked.pdf", DateTime.UtcNow.AddHours(-49));
            var downloader = new PreviewFileDownloader(new SingleFactory(new PreviewStore()), root);

            using (new FileStream(stale, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                downloader.CleanupStaleFiles();
                Assert.True(File.Exists(stale));
            }

            downloader.CleanupStaleFiles();
            Assert.False(File.Exists(stale));
        }
        finally { RemoveDirectory(root); }
    }

    [Fact]
    public async Task Bounded_preparation_blocks_large_objects_before_network_io()
    {
        var store = new PreviewStore { Body = new byte[4] };
        var downloader = new PreviewFileDownloader(new SingleFactory(store), TempDirectory());
        await Assert.ThrowsAsync<IOException>(() => downloader.PrepareAsync(
            Connection(), "assets", "large.pdf", "large.pdf", 1025, 1024, null, CancellationToken.None));
        Assert.Equal(0, store.Downloads);
    }

    [Fact]
    public async Task Bounded_preparation_detects_object_growth_and_removes_partial_file()
    {
        var store = new PreviewStore { Body = new byte[2048] };
        var root = TempDirectory();
        try
        {
            var downloader = new PreviewFileDownloader(new SingleFactory(store), root);
            await Assert.ThrowsAsync<IOException>(() => downloader.PrepareAsync(
                Connection(), "assets", "changed.pdf", "changed.pdf", 512, 1024, null, CancellationToken.None));
            Assert.Empty(Directory.GetFiles(root));
        }
        finally { RemoveDirectory(root); }
    }

    [Fact]
    public async Task Bounded_preparation_detects_short_download_and_removes_partial_file()
    {
        var store = new PreviewStore { Body = new byte[10] };
        var root = TempDirectory();
        try
        {
            var downloader = new PreviewFileDownloader(new SingleFactory(store), root);
            await Assert.ThrowsAsync<IOException>(() => downloader.PrepareAsync(
                Connection(), "assets", "incomplete.pdf", "incomplete.pdf", 11, 1024, null, CancellationToken.None));
            Assert.Empty(Directory.GetFiles(root));
        }
        finally { RemoveDirectory(root); }
    }

    [Fact]
    public async Task Bounded_preparation_cancel_after_write_removes_file()
    {
        var store = new PreviewStore { Body = new byte[512], CancelAfterWrite = true };
        var root = TempDirectory();
        using var cancellation = new CancellationTokenSource();
        store.CancellationSource = cancellation;
        try
        {
            var downloader = new PreviewFileDownloader(new SingleFactory(store), root);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => downloader.PrepareAsync(
                Connection(), "assets", "cancel.pdf", "cancel.pdf", 512, 1024, null, cancellation.Token));
            Assert.Empty(Directory.GetFiles(root));
        }
        finally { RemoveDirectory(root); }
    }

    [Fact]
    public async Task Bounded_preparation_provider_error_removes_partial_file()
    {
        var store = new PreviewStore { Body = new byte[30], ThrowAfterWrite = true };
        var root = TempDirectory();
        try
        {
            var downloader = new PreviewFileDownloader(new SingleFactory(store), root);
            await Assert.ThrowsAsync<IOException>(() => downloader.PrepareAsync(
                Connection(), "assets", "error.pdf", "error.pdf", 30, 1024, null, CancellationToken.None));
            Assert.Empty(Directory.GetFiles(root));
        }
        finally { RemoveDirectory(root); }
    }

    [Fact]
    public async Task Preparation_cannot_write_into_a_different_directory_using_file_name()
    {
        var store = new PreviewStore { Body = [1, 2] };
        var root = TempDirectory();
        try
        {
            var downloader = new PreviewFileDownloader(new SingleFactory(store), root);
            var file = await downloader.PrepareAsync(
                Connection(), "assets", "other", "../data.pdf", 2, 1024, null, CancellationToken.None);
            Assert.Equal(Path.GetFullPath(root), Path.GetDirectoryName(file));
            downloader.Release(file);
            Assert.Empty(Directory.GetFiles(root));
        }
        finally { RemoveDirectory(root); }
    }

    private static string CreatePreviewCopy(string directory, string name, DateTime lastWriteUtc)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{Guid.NewGuid():N}-{name}");
        File.WriteAllText(path, "preview");
        File.SetLastWriteTimeUtc(path, lastWriteUtc);
        return path;
    }

    private static string TempDirectory() =>
        Path.Combine(Path.GetTempPath(), "cavdeck-preview-tests", Guid.NewGuid().ToString("N"));

    private static void RemoveDirectory(string path)
    {
        if (Directory.Exists(path))
            Directory.Delete(path, recursive: true);
    }

    private sealed class RecordedProgress : IProgress<long>
    {
        public long LastBytes { get; private set; }
        public void Report(long value) => LastBytes = value;
    }

    [Fact]
    public async Task Truncated_body_is_not_editable()
    {
        var store = new PreviewStore
        {
            Head = Head(4, "\"v\"", "text/plain"),
            Body = "note"u8.ToArray(),
            Truncate = true
        };
        var opened = await new PreviewService(new SingleFactory(store)).OpenAsync(
            Connection(), "assets", "notes.txt", "notes.txt", CancellationToken.None);

        Assert.Equal(PreviewLoadKind.TooLarge, opened.Kind);
        Assert.Null(opened.Text);
    }

    [Fact]
    public async Task Invalid_utf8_is_not_editable()
    {
        var store = new PreviewStore
        {
            Head = Head(1, "\"v\"", "text/plain"),
            Body = [0xFF]
        };
        var opened = await new PreviewService(new SingleFactory(store)).OpenAsync(
            Connection(), "assets", "notes.txt", "notes.txt", CancellationToken.None);

        Assert.Equal(PreviewLoadKind.NotText, opened.Kind);
        Assert.Null(opened.Text);
    }

    [Fact]
    public async Task Save_sends_if_match_and_keeps_the_content_type()
    {
        var store = new PreviewStore();
        var saved = await new PreviewService(new SingleFactory(store)).SaveTextAsync(
            Connection(), "assets", "notes.txt", "next", TextCharset.Utf8, false,
            "text/plain", "\"abc\"", CancellationToken.None);

        Assert.Equal(PreviewSaveKind.Saved, saved.Kind);
        Assert.Equal("text/plain", store.SavedType);
        Assert.Equal("\"abc\"", store.SavedMatch);
        Assert.Equal("next", Encoding.UTF8.GetString(store.SavedBytes!));
    }

    [Fact]
    public async Task Save_reports_a_conflict()
    {
        var store = new PreviewStore { Conflict = true };
        var saved = await new PreviewService(new SingleFactory(store)).SaveTextAsync(
            Connection(), "assets", "notes.txt", "next", TextCharset.Utf8, false,
            "text/plain", "\"abc\"", CancellationToken.None);

        Assert.Equal(PreviewSaveKind.Conflict, saved.Kind);
        Assert.Equal(StorageFailureKind.PreconditionFailed, saved.Failure?.Kind);
    }

    [Fact]
    public async Task Unsupported_conditional_save_requires_explicit_overwrite()
    {
        var store = new PreviewStore { ConditionalOverwriteUnsupported = true };
        var service = new PreviewService(new SingleFactory(store));

        var guarded = await service.SaveTextAsync(Connection(), "assets", "notes.txt", "draft",
            TextCharset.Utf8, false, "text/plain", "etag", CancellationToken.None);
        Assert.Equal(PreviewSaveKind.RequiresOverwrite, guarded.Kind);
        Assert.Null(store.SavedBytes);

        var explicitOverwrite = await service.SaveTextAsync(Connection(), "assets", "notes.txt", "draft",
            TextCharset.Utf8, false, "text/plain", null, CancellationToken.None);
        Assert.Equal(PreviewSaveKind.Saved, explicitOverwrite.Kind);
        Assert.Equal("draft", Encoding.UTF8.GetString(store.SavedBytes!));
    }

    private static ObjectHead Head(long size, string etag, string type) =>
        new("assets", "notes.txt", size, etag, type);

    private static StorageConnection Connection() => new(
        Guid.NewGuid(), "Local", StorageProviderKind.Minio, "http://127.0.0.1:9000", "us-east-1", "ref");

    [Fact]
    public async Task Save_rechecks_read_only_policy_and_deleted_connections_including_overwrite()
    {
        var old = Connection();
        var store = new PreviewStore();
        var catalog = new MutableConnectionCatalog(old with { IsReadOnly = true });
        var service = new PreviewService(new SingleFactory(store), catalog);
        foreach (var etag in new string?[] { "version", null })
        {
            var saved = await service.SaveTextAsync(old, "assets", "notes.txt", "draft",
                TextCharset.Utf8, false, "text/plain", etag, CancellationToken.None);
            Assert.Equal(PreviewSaveKind.Failed, saved.Kind);
            Assert.Equal(StorageFailureKind.ReadOnly, saved.Failure?.Kind);
            Assert.Null(store.SavedBytes);
        }
        catalog.Current = null;
        var deleted = await service.SaveTextAsync(old, "assets", "notes.txt", "draft",
            TextCharset.Utf8, false, "text/plain", null, CancellationToken.None);
        Assert.Equal(StorageFailureKind.CredentialMissing, deleted.Failure?.Kind);
        Assert.Null(store.SavedBytes);
    }

    private sealed class SingleFactory(IObjectStorageProvider provider) : IObjectStorageProviderFactory
    {
        public IObjectStorageProvider Create(StorageProviderKind kind) => provider;
    }

    private sealed class PreviewStore : IObjectStorageProvider
    {
        public bool ConditionalOverwriteUnsupported { get; set; }
        public ObjectHead Head { get; set; } = new("assets", "notes.txt", 0, null, null);
        public byte[] Body { get; set; } = [];
        public bool Truncate { get; set; }
        public bool Conflict { get; set; }
        public bool CancelAfterWrite { get; set; }
        public bool ThrowAfterWrite { get; set; }
        public CancellationTokenSource? CancellationSource { get; set; }
        public int Downloads { get; private set; }
        public int Reads { get; private set; }
        public string? SavedType { get; private set; }
        public string? SavedMatch { get; private set; }
        public byte[]? SavedBytes { get; private set; }

        public StorageProviderKind Kind => StorageProviderKind.Minio;
        public ProviderCapabilities Capabilities { get; } = ConnectionPlanner.Capabilities(StorageProviderKind.Minio);

        public Task<IReadOnlyList<StorageBucket>> ListBucketsAsync(
            StorageConnection connection, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<StoragePage<StorageObject>> ListObjectsAsync(
            StorageConnection connection, string bucket, string prefix, string? continuationToken, int pageSize,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<PrefixPage> ListPrefixAsync(
            StorageConnection connection, string bucket, string prefix, string? continuationToken, int pageSize,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> ObjectExistsAsync(
            StorageConnection connection, string bucket, string key, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task CopyObjectAsync(
            StorageConnection connection, string sourceBucket, string sourceKey, string destinationBucket,
            string destinationKey, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task UploadAsync(
            StorageConnection connection, string bucket, string key, Stream content, string? contentType,
            IProgress<long>? progress, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public async Task DownloadAsync(
            StorageConnection connection, string bucket, string key, Stream destination, IProgress<long>? progress,
            CancellationToken cancellationToken)
        {
            Downloads++;
            await destination.WriteAsync(Body, cancellationToken);
            progress?.Report(Body.Length);
            if (CancelAfterWrite)
                CancellationSource?.Cancel();
            if (ThrowAfterWrite)
                throw new IOException("Injected remote download interruption.");
        }

        public Task DeleteAsync(
            StorageConnection connection, string bucket, string key, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ObjectHead> HeadObjectAsync(
            StorageConnection connection, string bucket, string key, CancellationToken cancellationToken) =>
            Task.FromResult(Head);

        public Task<ObjectBody> ReadObjectAsync(
            StorageConnection connection, string bucket, string key, int maxBytes, CancellationToken cancellationToken)
        {
            Reads++;
            return Task.FromResult(new ObjectBody(Head, Body, Truncate));
        }

        public Task<ObjectHead> ReplaceObjectAsync(
            StorageConnection connection, string bucket, string key, Stream content, string? contentType,
            string? ifMatchETag, CancellationToken cancellationToken)
        {
            if (ConditionalOverwriteUnsupported && !string.IsNullOrWhiteSpace(ifMatchETag))
                throw new StorageException(new StorageFailure(StorageFailureKind.Unsupported,
                    "ConditionalOverwriteUnsupported", null, null, null));
            if (Conflict)
                throw new StorageException(new StorageFailure(
                    StorageFailureKind.PreconditionFailed, "PreconditionFailed", 412, null, null));
            SavedType = contentType;
            SavedMatch = ifMatchETag;
            using var buffer = new MemoryStream();
            content.CopyTo(buffer);
            SavedBytes = buffer.ToArray();
            return Task.FromResult(Head with { SizeBytes = SavedBytes.Length, ETag = "\"next\"" });
        }
    }
}
