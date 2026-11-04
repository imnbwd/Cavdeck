using Cavdeck.Application.Abstractions;
using Cavdeck.Domain.Storage;

namespace Cavdeck.Application.Transfers;

public sealed class CatalogTransferConnections(IConnectionCatalog catalog) : ITransferConnections
{
    public Task<IReadOnlyList<StorageConnection>> ListAsync(CancellationToken cancellationToken) =>
        catalog.ListAsync(cancellationToken);

    public async Task<StorageConnection?> FindAsync(Guid id, CancellationToken cancellationToken)
    {
        var connections = await catalog.ListAsync(cancellationToken);
        return connections.FirstOrDefault(connection => connection.Id == id);
    }
}
