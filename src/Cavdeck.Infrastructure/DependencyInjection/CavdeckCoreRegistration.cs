using Cavdeck.Application.Abstractions;
using Cavdeck.Application.Connections;
using Cavdeck.Application.Explorer;
using Cavdeck.Application.Transfers;
using Cavdeck.Infrastructure.Connections;
using Cavdeck.Infrastructure.S3;
using Cavdeck.Infrastructure.Security;
using Cavdeck.Infrastructure.Transfers;
using Microsoft.Extensions.DependencyInjection;

namespace Cavdeck.Infrastructure.DependencyInjection;

public static class CavdeckCoreRegistration
{
    public static IServiceCollection AddCavdeckCore(this IServiceCollection services,
        string? transferJournalPath = null, bool readOnlyCatalog = false)
    {
        services.AddSingleton<ICredentialVault, WindowsCredentialVault>();
        services.AddSingleton(_ => new JsonConnectionCatalog(readOnlyCatalog));
        services.AddSingleton<IConnectionCatalog>(sp => sp.GetRequiredService<JsonConnectionCatalog>());
        services.AddSingleton<IStorageProbe, S3StorageProbe>();
        services.AddSingleton<S3ClientLeasePool>();
        services.AddSingleton<IObjectStorageProviderFactory, S3ProviderFactory>();
        services.AddSingleton<ConnectionService>();
        services.AddSingleton<ExplorerService>();
        services.AddSingleton<ITransferJournal>(_ =>
            new SqliteTransferJournal(transferJournalPath ?? SqliteTransferJournal.DefaultPath()));
        services.AddSingleton<ITransferConnections, CatalogTransferConnections>();
        services.AddSingleton<IRemoteObjectTransfer, S3ObjectTransfer>();
        services.AddSingleton<TransferEngine>();
        return services;
    }
}
