using Cavdeck.Application.Abstractions;
using Cavdeck.Domain.Storage;

namespace Cavdeck.Application.Connections;

public static class ConnectionAccess
{
    // Resolve local policy immediately before an operation. Never retarget an old
    // operation to a different backend or a newly restricted workspace.
    public static async Task<StorageConnection> ResolveAsync(IConnectionCatalog? catalog,
        StorageConnection snapshot, CancellationToken cancellationToken)
    {
        if (catalog is null)
            return snapshot;
        var current = (await catalog.ListAsync(cancellationToken))
            .FirstOrDefault(connection => connection.Id == snapshot.Id);
        if (current is null)
            throw new StorageException(new StorageFailure(
                StorageFailureKind.CredentialMissing, null, null, null, null));
        if (new ConnectionChange(snapshot, current).RequiresReconnect)
            throw new StorageException(new StorageFailure(
                StorageFailureKind.InvalidConfiguration, null, null, null, null));
        return current;
    }
}
