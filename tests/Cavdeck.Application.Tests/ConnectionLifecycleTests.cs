using Cavdeck.Application.Abstractions;
using Cavdeck.Application.Connections;
using Cavdeck.Application.Explorer;
using Cavdeck.Domain.Storage;
using Xunit;

namespace Cavdeck.Application.Tests;

public sealed class ConnectionLifecycleTests
{
    private static StorageConnection Connection() => new(
        Guid.NewGuid(), "Original", StorageProviderKind.Minio, "http://localhost:9000", "us-east-1", "cred");

    [Fact]
    public void Name_and_write_policy_changes_do_not_require_reconnecting()
    {
        var previous = Connection();
        Assert.False(new ConnectionChange(previous, previous with { Name = "Renamed" }).RequiresReconnect);
        Assert.False(new ConnectionChange(previous, previous with { IsReadOnly = true }).RequiresReconnect);
        Assert.False(new ConnectionChange(previous with { IsReadOnly = true }, previous).RequiresReconnect);
    }

    [Fact]
    public void Backend_scope_and_credential_changes_require_reconnecting()
    {
        var previous = Connection();
        foreach (var current in new[]
        {
            previous with { Endpoint = "http://other:9000" },
            previous with { Region = "eu-west-1" },
            previous with { Provider = StorageProviderKind.AwsS3 },
            previous with { ForcePathStyle = true },
            previous with { RestrictedBucket = "assets" },
            previous with { KeyPrefix = "allowed/" }
        })
            Assert.True(new ConnectionChange(previous, current).RequiresReconnect);
        Assert.True(new ConnectionChange(previous, previous, CredentialsChanged: true).RequiresReconnect);
    }

    [Theory]
    [InlineData("assets", "allowed/sub/", true)]
    [InlineData("assets", "allowed/", true)]
    [InlineData("other", "allowed/", false)]
    [InlineData("assets", "allowed-other/", false)]
    [InlineData(null, "", false)]
    public void Scope_changes_keep_only_locations_inside_the_new_root(string? bucket, string prefix, bool expected)
    {
        var connection = Connection() with { RestrictedBucket = "assets", KeyPrefix = "allowed/" };
        Assert.Equal(expected, ConnectionChange.ContainsLocation(connection, bucket, prefix));
    }

    [Fact]
    public async Task Old_snapshots_resolve_both_directions_of_the_write_policy()
    {
        var old = Connection();
        var catalog = new MutableConnectionCatalog(old with { Name = "Renamed", IsReadOnly = true });
        var current = await ConnectionAccess.ResolveAsync(catalog, old, CancellationToken.None);
        Assert.True(current.IsReadOnly);
        Assert.Equal("Renamed", current.Name);
        catalog.Current = old;
        Assert.False((await ConnectionAccess.ResolveAsync(catalog, current, CancellationToken.None)).IsReadOnly);
    }

    [Fact]
    public async Task Deleted_or_retargeted_snapshots_are_rejected()
    {
        var old = Connection();
        var catalog = new MutableConnectionCatalog(null);
        var deleted = await Assert.ThrowsAsync<StorageException>(() =>
            ConnectionAccess.ResolveAsync(catalog, old, CancellationToken.None));
        Assert.Equal(StorageFailureKind.CredentialMissing, deleted.Failure.Kind);
        catalog.Current = old with { Endpoint = "http://other:9000" };
        var changed = await Assert.ThrowsAsync<StorageException>(() =>
            ConnectionAccess.ResolveAsync(catalog, old, CancellationToken.None));
        Assert.Equal(StorageFailureKind.InvalidConfiguration, changed.Failure.Kind);
    }

    [Fact]
    public async Task Explorer_mutations_obey_the_current_catalog_without_touching_storage()
    {
        var old = Connection();
        var catalog = new MutableConnectionCatalog(old with { IsReadOnly = true });
        var service = new ExplorerService(new RejectProviderFactory(), catalog);
        var result = await service.CreateFolderAsync(old, "assets", "", "new", CancellationToken.None);
        Assert.Equal(StorageFailureKind.ReadOnly, Assert.Single(result.Issues).Failure.Kind);
        catalog.Current = null;
        await Assert.ThrowsAsync<StorageException>(() =>
            service.CreateTextFileAsync(old, "assets", "", "new.txt", CancellationToken.None));
    }

    private sealed class RejectProviderFactory : IObjectStorageProviderFactory
    {
        public IObjectStorageProvider Create(StorageProviderKind kind) =>
            throw new InvalidOperationException("Storage must not be accessed after a policy change.");
    }
}

internal sealed class MutableConnectionCatalog(StorageConnection? current) : IConnectionCatalog
{
    public StorageConnection? Current { get; set; } = current;
    public Task<IReadOnlyList<StorageConnection>> ListAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<StorageConnection>>(Current is { } connection ? [connection] : []);
    public Task UpsertAsync(StorageConnection connection, CancellationToken cancellationToken = default)
    {
        Current = connection;
        return Task.CompletedTask;
    }
    public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        if (Current?.Id == id)
            Current = null;
        return Task.CompletedTask;
    }
}
