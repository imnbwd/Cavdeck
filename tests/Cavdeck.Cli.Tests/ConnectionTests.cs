using System.Text.Json;
using Cavdeck.Application.Abstractions;
using Cavdeck.Application.Connections;
using Cavdeck.Domain.Storage;
using Cavdeck.Infrastructure.Connections;
using Xunit;

namespace Cavdeck.Cli.Tests;

public sealed class ConnectionTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("cavdeck-catalog-").FullName;
    private static readonly StorageConnection Saved = new(Guid.NewGuid(), "Production", StorageProviderKind.CloudflareR2,
        "https://account.r2.cloudflarestorage.com", "auto", "fictional-vault-reference", true, "bucket", "allowed/");

    [Fact]
    public async Task ResolvesGuidAndCaseInsensitiveName()
    {
        var service = await CreateAsync([Saved], new Vault());
        Assert.Equal(Saved, await CliRuntime.ResolveAsync(service, Saved.Id.ToString(), default));
        Assert.Equal(Saved, await CliRuntime.ResolveAsync(service, "PRODUCTION", default));
    }

    [Fact]
    public async Task RejectsMissingAndAmbiguousNames()
    {
        var service = await CreateAsync([Saved, Saved with { Id = Guid.NewGuid(), Name = "production" }], new Vault());
        var missing = await Assert.ThrowsAsync<CliException>(() => CliRuntime.ResolveAsync(service, "missing", default));
        var ambiguous = await Assert.ThrowsAsync<CliException>(() => CliRuntime.ResolveAsync(service, "Production", default));
        Assert.Equal((3, "CONNECTION_NOT_FOUND"), (missing.Error.ExitCode, missing.Error.Code));
        Assert.Equal((2, "AMBIGUOUS_CONNECTION"), (ambiguous.Error.ExitCode, ambiguous.Error.Code));
    }

    [Fact]
    public async Task MissingCredentialAndProbeFailuresMapWithoutProviderProse()
    {
        var missing = await CreateAsync([Saved], new Vault());
        var failure = (await missing.TestStoredAsync(Saved.Id)).Probe!.Failure!;
        Assert.Equal("AUTH_FAILED", CliError.FromStorage(failure).Code);

        var service = await CreateAsync([Saved], new Vault(("fictional-key", "fictional-secret")));
        failure = (await service.TestStoredAsync(Saved.Id)).Probe!.Failure!;
        var error = CliError.FromStorage(failure);
        Assert.Equal(6, error.ExitCode);
        Assert.DoesNotContain("fictional-secret", JsonSerializer.Serialize(error));
    }

    [Fact]
    public void BucketPrefixAndReadOnlyAreEnforced()
    {
        Assert.Throws<StorageException>(() => ObjectAccess.Ensure(Saved, "elsewhere", "allowed/file", false));
        Assert.Throws<StorageException>(() => ObjectAccess.Ensure(Saved, "bucket", "allowed", false));
        Assert.Throws<StorageException>(() => ObjectAccess.Ensure(Saved, "bucket", "allowed/file", true));
        ObjectAccess.Ensure(Saved, "bucket", "allowed/a//b", false);
    }

    [Fact]
    public async Task CliCatalogDoesNotRestoreBackupOrWrite()
    {
        var path = Path.Combine(_root, "connections.json");
        var desktop = new JsonConnectionCatalog(path);
        await desktop.UpsertAsync(Saved);
        var cli = new JsonConnectionCatalog(path, readOnly: true);
        Assert.Equal(Saved, Assert.Single(await cli.ListAsync()));
        Assert.Single(await cli.ListAsync());

        await Assert.ThrowsAsync<InvalidOperationException>(() => cli.DeleteAsync(Saved.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => cli.UpsertAsync(Saved));
        await File.WriteAllTextAsync(path, "broken");
        await Assert.ThrowsAnyAsync<JsonException>(() => cli.ListAsync());
        Assert.Equal("broken", await File.ReadAllTextAsync(path));
        File.Delete(path);
        await Assert.ThrowsAsync<InvalidDataException>(() => cli.ListAsync());
        Assert.False(File.Exists(path));
        Assert.True(File.Exists(path + ".bak"));
        Assert.Empty(Directory.GetFiles(_root, "*.corrupt-*"));
    }

    private async Task<ConnectionService> CreateAsync(StorageConnection[] connections, Vault vault)
    {
        var path = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".json");
        var desktop = new JsonConnectionCatalog(path);
        foreach (var connection in connections)
            await desktop.UpsertAsync(connection);
        return new(new JsonConnectionCatalog(path, readOnly: true), vault, new Probe());
    }

    private sealed class Vault((string, string)? credential = null) : ICredentialVault
    {
        public Task<(string AccessKeyId, string SecretAccessKey)?> ReadAsync(string reference, CancellationToken token) =>
            Task.FromResult(credential);
        public Task SaveAsync(string reference, string key, string secret, CancellationToken token) => throw new NotSupportedException();
        public Task DeleteAsync(string reference, CancellationToken token) => throw new NotSupportedException();
    }

    private sealed class Probe : IStorageProbe
    {
        public Task<StorageProbeResult> ProbeAsync(StorageProbeRequest request, CancellationToken token)
        {
            Assert.Equal("bucket", request.Bucket);
            Assert.Equal("allowed/", request.KeyPrefix);
            return Task.FromResult(StorageProbeResult.Failed(
                new StorageFailure(StorageFailureKind.Network, null, null, null, "fictional-secret")));
        }
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
