using Cavdeck.Application.Abstractions;
using Cavdeck.Domain.Storage;

namespace Cavdeck.Application.Preview;

/// <summary>Adapts the existing storage-provider abstraction to preview-only reads.</summary>
public sealed class ProviderPreviewContentReader : IPreviewContentReader
{
    private readonly IObjectStorageProviderFactory _providers;
    private readonly PreviewFileDownloader _files;

    public ProviderPreviewContentReader(IObjectStorageProviderFactory providers, string? temporaryDirectory = null)
    {
        _providers = providers ?? throw new ArgumentNullException(nameof(providers));
        _files = new PreviewFileDownloader(providers, temporaryDirectory);
    }

    /// <summary>Runs best-effort cleanup of expired temporary preview files.</summary>
    public void CleanupStaleFiles() => _files.CleanupStaleFiles();

    public Task<ObjectHead> HeadAsync(
        StorageConnection connection, string bucket, string key, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return _providers.Create(connection.Provider).HeadObjectAsync(connection, bucket, key, cancellationToken);
    }

    public Task<ObjectBody> ReadAsync(
        StorageConnection connection, string bucket, string key, int maxBytes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
        return _providers.Create(connection.Provider).ReadObjectAsync(connection, bucket, key, maxBytes, cancellationToken);
    }

    public async Task<PreviewFileLease> PrepareFileAsync(
        StorageConnection connection, string bucket, string key, string fileName,
        long expectedBytes, long maxBytes, IProgress<long>? progress, CancellationToken cancellationToken)
    {
        var path = await _files.PrepareAsync(
            connection, bucket, key, fileName, expectedBytes, maxBytes, progress, cancellationToken);
        return new PreviewFileLease(path, expectedBytes, _files.Release, _files.Detach);
    }
}
