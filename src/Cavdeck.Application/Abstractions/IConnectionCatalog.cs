using Cavdeck.Domain.Storage;

namespace Cavdeck.Application.Abstractions;

public interface IConnectionCatalog
{
    Task<IReadOnlyList<StorageConnection>> ListAsync(CancellationToken cancellationToken = default);
    Task UpsertAsync(StorageConnection connection, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}
