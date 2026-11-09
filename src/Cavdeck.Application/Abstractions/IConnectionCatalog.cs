using Cavdeck.Domain.Storage;

namespace Cavdeck.Application.Abstractions;

public interface IConnectionCatalog
{
    Task<IReadOnlyList<StorageConnection>> ListAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<StorageConnection>> ListForMutationAsync(CancellationToken cancellationToken) =>
        ListAsync(cancellationToken);
    Task UpsertAsync(StorageConnection connection, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}
