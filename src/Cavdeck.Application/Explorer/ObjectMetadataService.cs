using Cavdeck.Application.Abstractions;
using Cavdeck.Application.Connections;
using Cavdeck.Domain.Storage;

namespace Cavdeck.Application.Explorer;

public sealed class ObjectMetadataService(IObjectStorageProviderFactory providers, IConnectionCatalog? catalog = null)
{
    public async Task<ObjectMetadataSnapshot> ReadAsync(StorageConnection connection, string bucket,
        string key, CancellationToken token)
    {
        connection = await ConnectionAccess.ResolveAsync(catalog, connection, token);
        ObjectAccess.Ensure(connection, bucket, key, false);
        var snapshot = await Provider(connection).ReadMetadataAsync(connection, bucket, key, token);
        return snapshot with { EditBlock = ObjectMetadataRules.BasicBlock(connection, snapshot) };
    }

    public async Task<ObjectMetadataSnapshot> UpdateAsync(StorageConnection connection,
        ObjectMetadataSnapshot expected, ObjectMetadataValues desired, CancellationToken token)
    {
        connection = await ConnectionAccess.ResolveAsync(catalog, connection, token);
        ObjectAccess.Ensure(connection, expected.Head.Bucket, expected.Head.Key, true);
        if (ObjectMetadataRules.BasicBlock(connection, expected) != MetadataEditBlock.None)
            throw new StorageException(new StorageFailure(StorageFailureKind.Unsupported, "MetadataEditBlocked", null, null, null));
        if (ObjectMetadataRules.Validate(desired) != MetadataValidation.None)
            throw new ArgumentException("Invalid metadata.", nameof(desired));
        if (expected.Values.SameAs(desired)) return expected;
        return await Provider(connection).UpdateMetadataAsync(connection, expected, desired, token);
    }

    private IObjectMetadataProvider Provider(StorageConnection connection) =>
        providers.Create(connection.Provider) as IObjectMetadataProvider ??
        throw new StorageException(new StorageFailure(StorageFailureKind.Unsupported, "MetadataProvider", null, null, null));
}
