namespace Cavdeck.Application.Connections;

public interface IConnectionMutationLock
{
    Task<IDisposable> AcquireAsync(CancellationToken cancellationToken);
}

public sealed class ConnectionCatalogBusyException : IOException;
