using Cavdeck.Application.Settings;
using Cavdeck.Domain.Storage;
using Cavdeck.Infrastructure.Configuration;
using Cavdeck.Infrastructure.Connections;
using Cavdeck.Infrastructure.Settings;
using Xunit;

namespace Cavdeck.Infrastructure.Tests;

public sealed class ConfigurationRecoveryTests
{
    [Fact]
    public async Task Settings_first_save_creates_backup_and_next_save_rotates_last_valid_document()
    {
        using var sandbox = new TemporaryConfigurationDirectory();
        var path = sandbox.File("settings.json");
        var store = new JsonSettingsStore(path);

        await store.SaveAsync(new UserPreferences { MaxConcurrentTransfers = 2 });
        Assert.True(File.Exists(path + ".bak"));

        await store.SaveAsync(new UserPreferences { MaxConcurrentTransfers = 7 });
        Assert.Equal(7, (await store.LoadAsync()).MaxConcurrentTransfers);
        Assert.Equal(2, (await new JsonSettingsStore(path + ".bak").LoadAsync()).MaxConcurrentTransfers);
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public async Task Settings_restore_valid_backup_and_keep_damaged_original()
    {
        using var sandbox = new TemporaryConfigurationDirectory();
        var path = sandbox.File("settings.json");
        var store = new JsonSettingsStore(path);
        await store.SaveAsync(new UserPreferences { MaxConcurrentTransfers = 2 });
        await store.SaveAsync(new UserPreferences { MaxConcurrentTransfers = 7 });
        await File.WriteAllTextAsync(path, "{broken");

        var recovered = await store.LoadAsync();

        Assert.Equal(2, recovered.MaxConcurrentTransfers);
        Assert.Equal(ConfigurationRecoveryKind.BackupRestored, store.LastRecovery?.Kind);
        Assert.NotNull(store.LastRecovery);
        Assert.Equal("{broken", await File.ReadAllTextAsync(store.LastRecovery.PreservedPath));
        Assert.Equal(2, (await new JsonSettingsStore(path).LoadAsync()).MaxConcurrentTransfers);
        Assert.Equal(2, (await new JsonSettingsStore(path + ".bak").LoadAsync()).MaxConcurrentTransfers);
    }

    [Fact]
    public async Task Settings_without_valid_backup_use_defaults_but_never_discard_damaged_files()
    {
        using var sandbox = new TemporaryConfigurationDirectory();
        var path = sandbox.File("settings.json");
        var store = new JsonSettingsStore(path);
        await store.SaveAsync(new UserPreferences { MaxConcurrentTransfers = 6 });
        await File.WriteAllTextAsync(path, "{broken-main");
        await File.WriteAllTextAsync(path + ".bak", "{broken-backup");

        var loaded = await store.LoadAsync();

        Assert.Equal(3, loaded.MaxConcurrentTransfers);
        Assert.Equal(ConfigurationRecoveryKind.DefaultsUsed, store.LastRecovery?.Kind);
        Assert.NotNull(store.LastRecovery);
        Assert.Equal("{broken-main", await File.ReadAllTextAsync(store.LastRecovery.PreservedPath));
        Assert.Equal("{broken-backup", await File.ReadAllTextAsync(path + ".bak"));
        Assert.False(File.Exists(path));

        await store.SaveAsync(new UserPreferences { MaxConcurrentTransfers = 4 });

        Assert.Equal(4, (await store.LoadAsync()).MaxConcurrentTransfers);
        Assert.True(File.Exists(path + ".bak"));
        Assert.Contains(Directory.GetFiles(sandbox.Directory, "settings.json.bak.corrupt-*"),
            candidate => File.ReadAllText(candidate) == "{broken-backup");
    }

    [Fact]
    public async Task Settings_with_only_damaged_backup_archive_it_before_using_defaults()
    {
        using var sandbox = new TemporaryConfigurationDirectory();
        var path = sandbox.File("settings.json");
        await File.WriteAllTextAsync(path + ".bak", "{invalid");
        var store = new JsonSettingsStore(path);

        var settings = await store.LoadAsync();

        Assert.Equal(3, settings.MaxConcurrentTransfers);
        Assert.Equal(ConfigurationRecoveryKind.DefaultsUsed, store.LastRecovery?.Kind);
        Assert.Equal("{invalid", File.ReadAllText(store.LastRecovery!.PreservedPath));
        Assert.False(File.Exists(path + ".bak"));
        await store.SaveAsync(settings);
        Assert.True(File.Exists(path + ".bak"));
    }

    [Fact]
    public async Task Connection_catalog_recovers_valid_backup_without_removing_original()
    {
        using var sandbox = new TemporaryConfigurationDirectory();
        var path = sandbox.File("connections.json");
        var original = Connection("Photos");
        var second = Connection("Docs");
        var catalog = new JsonConnectionCatalog(path);
        await catalog.UpsertAsync(original);
        await catalog.UpsertAsync(second);

        await File.WriteAllTextAsync(path, "{broken");
        var restored = new JsonConnectionCatalog(path);
        var loaded = await restored.ListAsync();

        Assert.Equal(original, Assert.Single(loaded));
        Assert.Equal(ConfigurationRecoveryKind.BackupRestored, restored.LastRecovery?.Kind);
        Assert.Equal("{broken", File.ReadAllText(restored.LastRecovery!.PreservedPath));
        Assert.Equal(original, Assert.Single(await new JsonConnectionCatalog(path).ListAsync()));
        Assert.Equal(original, Assert.Single(await new JsonConnectionCatalog(path + ".bak").ListAsync()));
    }

    [Fact]
    public async Task Unrecoverable_catalog_refuses_all_mutations_and_keeps_original_bytes()
    {
        using var sandbox = new TemporaryConfigurationDirectory();
        var path = sandbox.File("connections.json");
        var catalog = new JsonConnectionCatalog(path);
        await catalog.UpsertAsync(Connection("One"));
        await File.WriteAllTextAsync(path, "{damaged-main");
        await File.WriteAllTextAsync(path + ".bak", "{damaged-backup");

        await Assert.ThrowsAsync<InvalidDataException>(() => catalog.ListAsync());
        await Assert.ThrowsAsync<InvalidDataException>(() => catalog.UpsertAsync(Connection("Two")));
        await Assert.ThrowsAsync<InvalidDataException>(() => catalog.DeleteAsync(Guid.NewGuid()));

        Assert.Equal("{damaged-main", File.ReadAllText(path));
        Assert.Equal("{damaged-backup", File.ReadAllText(path + ".bak"));
        Assert.Empty(Directory.GetFiles(sandbox.Directory, "*.corrupt-*"));
    }

    [Fact]
    public async Task Newer_catalog_schema_never_downgrades_to_old_backup()
    {
        using var sandbox = new TemporaryConfigurationDirectory();
        var path = sandbox.File("connections.json");
        var catalog = new JsonConnectionCatalog(path);
        await catalog.UpsertAsync(Connection("One"));
        const string newFormat = """{"Schema":9,"Connections":[{"Provider":"FutureStorage"}]}""";
        await File.WriteAllTextAsync(path, newFormat);

        await Assert.ThrowsAsync<UnsupportedConfigurationVersionException>(() => catalog.ListAsync());
        await Assert.ThrowsAsync<UnsupportedConfigurationVersionException>(() => catalog.DeleteAsync(Guid.NewGuid()));

        Assert.Equal(newFormat, File.ReadAllText(path));
        Assert.True(File.Exists(path + ".bak"));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"Schema":1}""")]
    [InlineData("""{"Schema":1,"Connections":null}""")]
    [InlineData("""{"Schema":1,"Connections":[null]}""")]
    public async Task Missing_connection_records_are_not_silently_treated_as_empty(string document)
    {
        using var sandbox = new TemporaryConfigurationDirectory();
        var path = sandbox.File("connections.json");
        await File.WriteAllTextAsync(path, document);

        await Assert.ThrowsAnyAsync<InvalidDataException>(() => new JsonConnectionCatalog(path).ListAsync());
        Assert.Equal(document, File.ReadAllText(path));
    }

    [Fact]
    public async Task Missing_catalog_is_empty_without_warning_or_unnecessary_backup()
    {
        using var sandbox = new TemporaryConfigurationDirectory();
        var path = sandbox.File("connections.json");
        var catalog = new JsonConnectionCatalog(path);

        Assert.Empty(await catalog.ListAsync());
        Assert.Null(catalog.LastRecovery);
        Assert.False(File.Exists(path + ".bak"));
    }

    [Fact]
    public async Task Orphaned_valid_backup_can_restore_missing_primary()
    {
        using var sandbox = new TemporaryConfigurationDirectory();
        var path = sandbox.File("connections.json");
        var catalog = new JsonConnectionCatalog(path);
        var original = Connection("One");
        await catalog.UpsertAsync(original);
        File.Delete(path);

        var loaded = new JsonConnectionCatalog(path);
        Assert.Equal(original, Assert.Single(await loaded.ListAsync()));
        Assert.Equal(ConfigurationRecoveryKind.BackupRestored, loaded.LastRecovery?.Kind);
        Assert.True(File.Exists(path));
    }

    private static StorageConnection Connection(string name)
    {
        var id = Guid.NewGuid();
        return new StorageConnection(id, name, StorageProviderKind.Minio,
            "http://127.0.0.1:9000", "us-east-1", $"Cavdeck:Test:{id:N}");
    }

    private sealed class TemporaryConfigurationDirectory : IDisposable
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(),
            "cavdeck-config-recovery-tests-" + Guid.NewGuid().ToString("N"));

        public TemporaryConfigurationDirectory() => System.IO.Directory.CreateDirectory(Directory);
        public string File(string fileName) => Path.Combine(Directory, fileName);
        public void Dispose() => System.IO.Directory.Delete(Directory, recursive: true);
    }
}
