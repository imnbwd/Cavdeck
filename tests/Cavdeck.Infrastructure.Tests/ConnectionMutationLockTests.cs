using System.Collections.Concurrent;
using Cavdeck.Application.Abstractions;
using Cavdeck.Application.Connections;
using Cavdeck.Domain.Storage;
using Cavdeck.Infrastructure.Connections;
using Xunit;

namespace Cavdeck.Infrastructure.Tests;

public sealed class ConnectionMutationLockTests
{
    [Fact]
    public async Task IndependentCatalogServicesShareTheOsLockAcrossSaveUpdateAndDelete()
    {
        var root = Directory.CreateTempSubdirectory("cavdeck-connection-lock-").FullName;
        try
        {
            var lockPath = Path.Combine(root, "catalog.lock");
            var catalogPath = Path.Combine(root, "connections.json");
            var vault = new Vault();
            var firstLock = new FileConnectionMutationLock(lockPath);
            var secondLock = new FileConnectionMutationLock(lockPath);
            var first = new ConnectionService(new JsonConnectionCatalog(catalogPath, mutationLock: firstLock),
                vault, new Probe(), firstLock);
            var second = new ConnectionService(new JsonConnectionCatalog(catalogPath, mutationLock: secondLock),
                vault, new Probe(), secondLock);

            using (var held = new FileStream(lockPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                var blocked = new FileConnectionMutationLock(lockPath, TimeSpan.FromMilliseconds(20));
                await Assert.ThrowsAsync<ConnectionCatalogBusyException>(() => blocked.AcquireAsync(CancellationToken.None));
                using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.SaveAsync(Draft("cancelled"), cancellation.Token));
                Assert.False(File.Exists(catalogPath));
            }

            var saved = await Task.WhenAll(first.SaveAsync(Draft("one")), second.SaveAsync(Draft("two")));
            Assert.Equal(2, (await first.ListAsync()).Count);
            var duplicate = await Assert.ThrowsAsync<ConnectionValidationException>(() => second.SaveAsync(Draft("ONE")));
            Assert.Equal(ConnectionIssueCode.Duplicate, Assert.Single(duplicate.Issues).Code);
            var original = saved[0];
            await Task.WhenAll(
                first.UpdateAsync(original.Id, (current, access) => Draft(current.Name, current.Id,
                    access, "eu-west-1", current.IsReadOnly)),
                second.UpdateAsync(original.Id, (current, access) => Draft(current.Name, current.Id,
                    access, current.Region, true)));
            var updated = (await second.ListAsync()).Single(connection => connection.Id == original.Id);
            Assert.Equal("eu-west-1", updated.Region);
            Assert.True(updated.IsReadOnly);
            Assert.Equal(original.CredentialReference, updated.CredentialReference);

            await Task.WhenAll(first.DeleteAsync(original.Id), second.SaveAsync(Draft("three")));
            Assert.Equal(2, (await second.ListAsync()).Count);
            Assert.Null(await vault.ReadAsync(original.CredentialReference, CancellationToken.None));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static ConnectionDraft Draft(string name, Guid? id = null, string? access = null,
        string region = "us-east-1", bool readOnly = false) => new()
    {
        Id = id, Name = name, Region = region, Provider = StorageProviderKind.AwsS3,
        AccessKeyId = access ?? "fictional-access", SecretAccessKey = id is null ? "fictional-secret" : null,
        KeepExistingSecret = id is not null, IsReadOnly = readOnly
    };

    private sealed class Probe : IStorageProbe
    {
        public Task<StorageProbeResult> ProbeAsync(StorageProbeRequest request, CancellationToken token) =>
            throw new NotSupportedException();
    }

    private sealed class Vault : ICredentialVault
    {
        private readonly ConcurrentDictionary<string, (string AccessKeyId, string SecretAccessKey)> _values = new();
        public Task<(string AccessKeyId, string SecretAccessKey)?> ReadAsync(string reference, CancellationToken token) =>
            Task.FromResult<(string, string)?>(_values.TryGetValue(reference, out var value) ? value : null);
        public Task SaveAsync(string reference, string accessKeyId, string secretAccessKey, CancellationToken token)
        {
            _values[reference] = (accessKeyId, secretAccessKey);
            return Task.CompletedTask;
        }
        public Task DeleteAsync(string reference, CancellationToken token)
        {
            _values.TryRemove(reference, out _);
            return Task.CompletedTask;
        }
    }
}
