using System.IO;
using System.Windows.Media.Imaging;
using Cavdeck.Application.Abstractions;
using Cavdeck.Domain.Preview;
using Cavdeck.Domain.Storage;

namespace Cavdeck.App.Features.Explorer;

/// <summary>
/// Per-workspace, read-only thumbnails. Only realized visual containers call GetAsync.
/// Provider reads are byte-bounded; two workers max; decoded frames are frozen and
/// retained only in a small in-memory LRU (never persisted to disk).
/// </summary>
public sealed class ThumbnailPipeline : IDisposable
{
    public const int MaximumSourceBytes = 4 * 1024 * 1024;
    public const int DecodeWidth = 192;
    public const int MaximumConcurrentReads = 2;
    public const long CacheBudgetBytes = 16 * 1024 * 1024;

    private readonly IObjectStorageProviderFactory _providers;
    private readonly ThumbnailLruCache<ThumbnailKey, BitmapSource?> _cache = new(CacheBudgetBytes);
    private readonly SemaphoreSlim _slots = new(MaximumConcurrentReads);
    private readonly CancellationTokenSource _shutdown = new();
    private bool _disposed;

    public ThumbnailPipeline(IObjectStorageProviderFactory providers)
    {
        _providers = providers ?? throw new ArgumentNullException(nameof(providers));
    }

    public static bool IsEligible(ObjectEntry entry) =>
        entry.Kind == ObjectEntryKind.File &&
        entry.SizeBytes > 0 && entry.SizeBytes <= MaximumSourceBytes &&
        PreviewRules.Classify(entry.Name) == PreviewFileType.Image;

    public async Task<BitmapSource?> GetAsync(
        StorageConnection connection, string bucket, ObjectEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(entry);
        if (_disposed || !IsEligible(entry))
            return null;

        // Include the object identity and available version hints to avoid using a stale
        // image after a remote replacement or switching storage connections.
        var cacheKey = new ThumbnailKey(connection.Id, bucket, entry.Key,
            entry.ETag, entry.SizeBytes, entry.LastModified);
        if (_cache.TryGet(cacheKey, out var hit))
            return hit;

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        var token = linked.Token;
        var entered = false;
        try
        {
            await _slots.WaitAsync(token);
            entered = true;
            token.ThrowIfCancellationRequested();
            if (_cache.TryGet(cacheKey, out hit))
                return hit;

            // The provider's bounded read also guards against a stale/incorrect listing size.
            // A thumbnail never queues a persistent transfer or downloads an unbounded file.
            var body = await _providers.Create(connection.Provider)
                .ReadObjectAsync(connection, bucket, entry.Key, MaximumSourceBytes, token);
            if (body.Truncated || body.Content.Length > MaximumSourceBytes)
                return null;

            var image = await Task.Run(() => Decode(body.Content), token);
            token.ThrowIfCancellationRequested();

            if (!_disposed)
            {
                // A failed WIC decode is negatively cached within the small LRU, so a
                // corrupt/unsupported image does not trigger endless requests on scroll.
                var bytes = image is null ? 1L : (long)image.PixelWidth * image.PixelHeight * 4;
                _cache.Put(cacheKey, image, Math.Max(1, bytes));
            }

            return image;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // Thumbnail errors are non-blocking: the normal image glyph stays visible.
            // Explorer's normal open/download commands still work.
            return null;
        }
        finally
        {
            if (entered)
                _slots.Release();
        }
    }

    private static BitmapSource? Decode(byte[] bytes)
    {
        if (bytes.Length == 0)
            return null;
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bitmap.DecodePixelWidth = DecodeWidth;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or InvalidOperationException
            or NotSupportedException or FileFormatException
            or System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _shutdown.Cancel();
        _cache.Clear();
        // In-flight tasks own linked tokens and release semaphore slots as they finish.
        // Avoid disposing synchronization objects while those tasks are unwinding.
    }

    private readonly record struct ThumbnailKey(
        Guid ConnectionId, string Bucket, string Key, string? ETag,
        long SizeBytes, DateTimeOffset? LastModified);
}
