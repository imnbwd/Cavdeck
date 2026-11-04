using Cavdeck.Application.Abstractions;
using Cavdeck.Application.Connections;
using Cavdeck.Domain.Storage;
using Xunit;

namespace Cavdeck.Application.Tests;

public sealed class ConnectionServiceTests
{
    [Fact]
    public async Task Save_stores_the_secret_in_the_vault_and_not_on_the_connection()
    {
        var vault = new MemoryVault();
        var catalog = new MemoryCatalog();
        var probe = new ScriptedProbe();
        var service = new ConnectionService(catalog, vault, probe);
        const string secret = "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY";

        var saved = await service.SaveAsync(new ConnectionDraft
        {
            Name = "Photos",
            Provider = StorageProviderKind.CloudflareR2,
            AccountId = "abcdefghijklmnopqrstuvwxyz012345",
            AccessKeyId = "AKIDEXAMPLE123456789",
            SecretAccessKey = secret
        });

        Assert.Equal("https://abcdefghijklmnopqrstuvwxyz012345.r2.cloudflarestorage.com", saved.Endpoint);
        Assert.Equal(CredentialReferences.ForConnection(saved.Id), saved.CredentialReference);
        Assert.Equal(secret, vault.Items[saved.CredentialReference].Secret);
        Assert.DoesNotContain(secret, System.Text.Json.JsonSerializer.Serialize(saved));
        Assert.Equal(1, vault.Saves);
    }

    [Fact]
    public async Task Test_uses_the_resolved_endpoint_without_saving()
    {
        var vault = new MemoryVault();
        var probe = new ScriptedProbe { Result = StorageProbeResult.Success(2, null) };
        var service = new ConnectionService(new MemoryCatalog(), vault, probe);

        var attempt = await service.TestAsync(new ConnectionDraft
        {
            Name = "Local",
            Provider = StorageProviderKind.Minio,
            Endpoint = "http://127.0.0.1:9000",
            ForcePathStyle = true,
            AccessKeyId = "minioadmin",
            SecretAccessKey = "minioadmin"
        });

        Assert.True(attempt.Succeeded);
        Assert.Equal("http://127.0.0.1:9000", probe.Last!.ServiceUrl);
        Assert.True(probe.Last.ForcePathStyle);
        Assert.Equal(0, vault.Saves);
    }

    [Fact]
    public async Task Failed_catalog_write_removes_a_new_secret()
    {
        var vault = new MemoryVault();
        var service = new ConnectionService(new BoomCatalog(), vault, new ScriptedProbe());

        await Assert.ThrowsAsync<IOException>(() => service.SaveAsync(new ConnectionDraft
        {
            Name = "Photos",
            Provider = StorageProviderKind.AwsS3,
            Region = "us-east-1",
            AccessKeyId = "AKIDEXAMPLE123456789",
            SecretAccessKey = "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY"
        }));

        Assert.Empty(vault.Items);
        Assert.Equal(1, vault.Deletes);
    }

    [Fact]
    public async Task Edit_can_keep_the_stored_secret()
    {
        var id = Guid.NewGuid();
        var reference = CredentialReferences.ForConnection(id);
        var vault = new MemoryVault();
        vault.Items[reference] = ("AKIDEXAMPLE123456789", "stored-secret-value");
        var catalog = new MemoryCatalog();
        catalog.Items.Add(new StorageConnection(
            id, "Old", StorageProviderKind.AwsS3, "", "us-east-1", reference));
        var service = new ConnectionService(catalog, vault, new ScriptedProbe());

        var saved = await service.SaveAsync(new ConnectionDraft
        {
            Id = id,
            Name = "Renamed",
            Provider = StorageProviderKind.AwsS3,
            Region = "eu-west-1",
            AccessKeyId = "AKIDEXAMPLE123456789",
            KeepExistingSecret = true
        });

        Assert.Equal("Renamed", saved.Name);
        Assert.Equal("eu-west-1", saved.Region);
        Assert.Equal(0, vault.Saves);
        Assert.Equal("stored-secret-value", vault.Items[reference].Secret);
    }

    [Fact]
    public async Task Changing_the_access_key_requires_a_new_secret()
    {
        var id = Guid.NewGuid();
        var reference = CredentialReferences.ForConnection(id);
        var vault = new MemoryVault();
        vault.Items[reference] = ("AKIDEXAMPLE123456789", "stored-secret-value");
        var service = new ConnectionService(new MemoryCatalog(), vault, new ScriptedProbe());

        var exception = await Assert.ThrowsAsync<ConnectionValidationException>(() => service.SaveAsync(new ConnectionDraft
        {
            Id = id,
            Name = "Renamed",
            Provider = StorageProviderKind.AwsS3,
            Region = "us-east-1",
            AccessKeyId = "AKIDEXAMPLE000000000",
            KeepExistingSecret = true
        }));

        Assert.Contains(exception.Issues, issue => issue.Field == ConnectionField.Secret);
    }

    [Fact]
    public async Task Delete_removes_the_catalog_entry_and_the_secret()
    {
        var id = Guid.NewGuid();
        var reference = CredentialReferences.ForConnection(id);
        var vault = new MemoryVault();
        vault.Items[reference] = ("key", "stored-secret-value");
        var catalog = new MemoryCatalog();
        catalog.Items.Add(new StorageConnection(id, "n", StorageProviderKind.AwsS3, "", "us-east-1", reference));
        var service = new ConnectionService(catalog, vault, new ScriptedProbe());

        await service.DeleteAsync(id);

        Assert.Empty(catalog.Items);
        Assert.False(vault.Items.ContainsKey(reference));
    }


    [Fact]
    public async Task Failed_existing_connection_save_restores_original_credential()
    {
        var id = Guid.NewGuid();
        var reference = CredentialReferences.ForConnection(id);
        var vault = new MemoryVault();
        vault.Items[reference] = ("AKIDEXAMPLE123456789", "original-secret");
        var catalog = new MemoryCatalog { FailNextUpsert = true };
        catalog.Items.Add(new StorageConnection(id, "Original", StorageProviderKind.AwsS3,
            "", "us-east-1", reference));
        var service = new ConnectionService(catalog, vault, new ScriptedProbe());

        await Assert.ThrowsAsync<IOException>(() => service.SaveAsync(new ConnectionDraft
        {
            Id = id, Name = "New", Provider = StorageProviderKind.AwsS3,
            Region = "us-east-1", AccessKeyId = "AKIDEXAMPLE123456789",
            SecretAccessKey = "replacement-secret"
        }));

        Assert.Equal("original-secret", vault.Items[reference].Secret);
        Assert.Equal("Original", Assert.Single(catalog.Items).Name);
    }

    [Fact]
    public async Task Failed_connection_deletion_preserves_credentials_and_catalog()
    {
        var id = Guid.NewGuid();
        var reference = CredentialReferences.ForConnection(id);
        var vault = new MemoryVault();
        vault.Items[reference] = ("access", "secret");
        var catalog = new MemoryCatalog { FailNextDelete = true };
        catalog.Items.Add(new StorageConnection(id, "Original", StorageProviderKind.AwsS3,
            "", "us-east-1", reference));
        var service = new ConnectionService(catalog, vault, new ScriptedProbe());

        await Assert.ThrowsAsync<IOException>(() => service.DeleteAsync(id));
        Assert.Single(catalog.Items);
        Assert.Equal("secret", vault.Items[reference].Secret);
        Assert.Equal(0, vault.Deletes);
    }

    [Fact]
    public async Task Failed_credential_deletion_restores_connection_catalog()
    {
        var id = Guid.NewGuid();
        var reference = CredentialReferences.ForConnection(id);
        var vault = new MemoryVault { FailNextDelete = true };
        vault.Items[reference] = ("access", "secret");
        var catalog = new MemoryCatalog();
        catalog.Items.Add(new StorageConnection(id, "Original", StorageProviderKind.AwsS3,
            "", "us-east-1", reference));
        var service = new ConnectionService(catalog, vault, new ScriptedProbe());

        await Assert.ThrowsAsync<IOException>(() => service.DeleteAsync(id));
        Assert.Single(catalog.Items);
        Assert.Equal("secret", vault.Items[reference].Secret);
    }

    [Fact]
    public async Task Failed_credential_delete_after_mutation_restores_both_stores()
    {
        var id = Guid.NewGuid();
        var reference = CredentialReferences.ForConnection(id);
        var vault = new MemoryVault { FailAfterDeleting = true };
        vault.Items[reference] = ("access", "secret");
        var catalog = new MemoryCatalog();
        catalog.Items.Add(new StorageConnection(id, "Original", StorageProviderKind.AwsS3,
            "", "us-east-1", reference));
        var service = new ConnectionService(catalog, vault, new ScriptedProbe());

        await Assert.ThrowsAsync<IOException>(() => service.DeleteAsync(id));
        Assert.Single(catalog.Items);
        Assert.Equal("secret", vault.Items[reference].Secret);
    }

    private sealed class MemoryVault : ICredentialVault
    {
        public Dictionary<string, (string Access, string Secret)> Items { get; } = new();
        public int Saves { get; private set; }
        public int Deletes { get; private set; }
        public bool FailNextDelete { get; set; }
        public bool FailAfterDeleting { get; set; }

        public Task SaveAsync(string reference, string accessKeyId, string secretAccessKey,
            CancellationToken cancellationToken)
        {
            Saves++;
            Items[reference] = (accessKeyId, secretAccessKey);
            return Task.CompletedTask;
        }

        public Task<(string AccessKeyId, string SecretAccessKey)?> ReadAsync(
            string reference, CancellationToken cancellationToken)
        {
            if (!Items.TryGetValue(reference, out var value))
                return Task.FromResult<(string AccessKeyId, string SecretAccessKey)?>(null);
            return Task.FromResult<(string AccessKeyId, string SecretAccessKey)?>((value.Access, value.Secret));
        }

        public Task DeleteAsync(string reference, CancellationToken cancellationToken)
        {
            if (FailNextDelete)
            {
                FailNextDelete = false;
                throw new IOException("Failed to delete credential");
            }
            Deletes++;
            Items.Remove(reference);
            if (FailAfterDeleting)
            {
                FailAfterDeleting = false;
                throw new IOException("Credential delete failed after mutation");
            }
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Saving_an_editor_for_a_deleted_connection_cannot_recreate_it()
    {
        var catalog = new MemoryCatalog();
        var vault = new MemoryVault();
        var service = new ConnectionService(catalog, vault, new ScriptedProbe());
        await Assert.ThrowsAsync<StorageException>(() => service.SaveAsync(new ConnectionDraft
        {
            Id = Guid.NewGuid(), Name = "Deleted", Provider = StorageProviderKind.AwsS3,
            Region = "us-east-1", AccessKeyId = "AKIDEXAMPLE123456789", SecretAccessKey = "new-secret-value"
        }));
        Assert.Empty(catalog.Items);
        Assert.Empty(vault.Items);
    }

    private sealed class MemoryCatalog : IConnectionCatalog
    {
        public List<StorageConnection> Items { get; } = [];
        public bool FailNextUpsert { get; set; }
        public bool FailNextDelete { get; set; }

        public Task<IReadOnlyList<StorageConnection>> ListAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<StorageConnection>>(Items.ToList());

        public Task UpsertAsync(StorageConnection connection, CancellationToken cancellationToken)
        {
            if (FailNextUpsert)
            {
                FailNextUpsert = false;
                throw new IOException("Failed to persist connection");
            }
            var index = Items.FindIndex(item => item.Id == connection.Id);
            if (index >= 0)
                Items[index] = connection;
            else
                Items.Add(connection);
            return Task.CompletedTask;
        }

        public Task DeleteAsync(Guid id, CancellationToken cancellationToken)
        {
            if (FailNextDelete)
            {
                FailNextDelete = false;
                throw new IOException("Failed to remove connection");
            }
            Items.RemoveAll(item => item.Id == id);
            return Task.CompletedTask;
        }
    }

    private sealed class BoomCatalog : IConnectionCatalog
    {
        public Task<IReadOnlyList<StorageConnection>> ListAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<StorageConnection>>([]);

        public Task UpsertAsync(StorageConnection connection, CancellationToken cancellationToken) =>
            throw new IOException("disk");

        public Task DeleteAsync(Guid id, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class ScriptedProbe : IStorageProbe
    {
        public StorageProbeRequest? Last { get; private set; }
        public StorageProbeResult Result { get; init; } = StorageProbeResult.Success(0, null);

        public Task<StorageProbeResult> ProbeAsync(
            StorageProbeRequest request, CancellationToken cancellationToken)
        {
            Last = request;
            return Task.FromResult(Result);
        }
    }
}
