using Cavdeck.Domain.Storage;

namespace Cavdeck.Application.Abstractions;

public interface IObjectStorageProviderFactory
{
    IObjectStorageProvider Create(StorageProviderKind kind);
}
