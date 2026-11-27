using Cavdeck.Domain.Storage;

namespace Cavdeck.Application.Abstractions;

public interface IObjectMetadataProvider
{
    Task<ObjectMetadataSnapshot> ReadMetadataAsync(StorageConnection connection, string bucket,
        string key, CancellationToken token);
    Task<ObjectMetadataSnapshot> UpdateMetadataAsync(StorageConnection connection,
        ObjectMetadataSnapshot expected, ObjectMetadataValues desired, CancellationToken token);
}
