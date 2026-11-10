using Cavdeck.Application.Abstractions;
using Cavdeck.Application.Connections;
using Cavdeck.Application.Explorer;
using Cavdeck.Application.Preview;
using Cavdeck.Application.Sharing;
using Cavdeck.Application.Transfers;
using Cavdeck.Infrastructure.Connections;
using Cavdeck.Infrastructure.S3;
using Cavdeck.Infrastructure.Security;
using Cavdeck.Infrastructure.Transfers;
using Cavdeck.Infrastructure.Updates;
using Microsoft.Extensions.DependencyInjection;

namespace Cavdeck.Infrastructure.DependencyInjection;

public static class CavdeckCoreRegistration
{
    public static IServiceCollection AddCavdeckCore(this IServiceCollection services,
        string? transferJournalPath = null, bool readOnlyCatalog = false)
    {
        services.AddSingleton<ICredentialVault, WindowsCredentialVault>();
        services.AddSingleton<IApplicationActivityGate, FileApplicationActivityGate>();
        services.AddSingleton<IConnectionMutationLock>(_ => new FileConnectionMutationLock());
        services.AddSingleton(sp => new JsonConnectionCatalog(readOnlyCatalog, sp.GetRequiredService<IConnectionMutationLock>()));
        services.AddSingleton<IConnectionCatalog>(sp => sp.GetRequiredService<JsonConnectionCatalog>());
        services.AddSingleton<IStorageProbe, S3StorageProbe>();
        services.AddSingleton<S3ClientLeasePool>();
        services.AddSingleton<IDownloadLinkSigner, S3DownloadLinkSigner>();
        services.AddSingleton<IObjectStorageProviderFactory, S3ProviderFactory>();
        services.AddSingleton<IPreviewContentReader, ProviderPreviewContentReader>();
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
