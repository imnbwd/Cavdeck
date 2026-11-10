using Cavdeck.Domain.Storage;

namespace Cavdeck.Application.Preview;

/// <summary>Read-only access to content used by preview viewers.</summary>
public interface IPreviewContentReader
{
    Task<ObjectHead> HeadAsync(
        StorageConnection connection, string bucket, string key, CancellationToken cancellationToken);

    Task<ObjectBody> ReadAsync(
        StorageConnection connection, string bucket, string key, int maxBytes, CancellationToken cancellationToken);

    Task<PreviewFileLease> PrepareFileAsync(
        StorageConnection connection, string bucket, string key, string fileName,
        long expectedBytes, long maxBytes, IProgress<long>? progress, CancellationToken cancellationToken);
}
