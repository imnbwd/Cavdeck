using Cavdeck.Application.Abstractions;
using Cavdeck.Domain.Storage;

namespace Cavdeck.Infrastructure.S3;

public sealed class S3ProviderFactory : IObjectStorageProviderFactory, IDisposable
{
    private readonly ICredentialVault _vault;
    private readonly IS3Gateway _gateway;

    public S3ProviderFactory(ICredentialVault vault) : this(vault, new AwsS3Gateway()) { }

    // The application injects the same singleton pool into browsing and transfers.
    public S3ProviderFactory(ICredentialVault vault, S3ClientLeasePool clients)
        : this(vault, new AwsS3Gateway(clients)) { }

    internal S3ProviderFactory(ICredentialVault vault, IS3Gateway gateway)
    {
        _vault = vault;
        _gateway = gateway;
    }

    public IObjectStorageProvider Create(StorageProviderKind kind) =>
        new S3CompatibleStorageProvider(kind, _vault, _gateway);

    public void Dispose()
    {
        if (_gateway is IDisposable disposable)
            disposable.Dispose();
    }
}
