using System.Text.Json;
using System.Text.Json.Serialization;
using Cavdeck.Application.Abstractions;
using Cavdeck.Application.Connections;
using Cavdeck.Domain.Storage;
using Cavdeck.Infrastructure.Configuration;

namespace Cavdeck.Infrastructure.Connections;

// Non-secret connection catalog. Access keys never belong in this file.
public sealed class JsonConnectionCatalog : IConnectionCatalog
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _filePath;
    private readonly bool _readOnly;
    private readonly IConnectionMutationLock? _mutationLock;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public JsonConnectionCatalog() : this(false) { }

    public JsonConnectionCatalog(bool readOnly, IConnectionMutationLock? mutationLock = null)
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Cavdeck", "connections.json"), readOnly, mutationLock)
    {
    }

    public JsonConnectionCatalog(string filePath, bool readOnly = false, IConnectionMutationLock? mutationLock = null)
    {
        _filePath = filePath;
        _readOnly = readOnly;
        _mutationLock = mutationLock;
    }

    public string FilePath => _filePath;
    public ConfigurationRecovery? LastRecovery { get; private set; }

    public async Task<IReadOnlyList<StorageConnection>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        if (_readOnly || _mutationLock is null)
            return await ListForMutationAsync(cancellationToken);

        // Normal reads can share the atomically replaced file. Recovery writes
        // use the same transaction lock as connection mutations.
        try
        {
            var current = await ReadValidatedAsync(_filePath, cancellationToken, allowReplacement: true);
            return current.Connections!;
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException
            or JsonException or InvalidDataException)
        {
            if ((exception is FileNotFoundException or DirectoryNotFoundException) && !File.Exists(_filePath + ".bak"))
                return [];
            using var lease = await _mutationLock.AcquireAsync(cancellationToken);
            return await ListForMutationAsync(cancellationToken);
        }
    }

    public async Task<IReadOnlyList<StorageConnection>> ListForMutationAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await ReadAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task UpsertAsync(StorageConnection connection, CancellationToken cancellationToken = default)
    {
        if (_readOnly)
            throw new InvalidOperationException("The connection catalog is read-only.");
        ArgumentNullException.ThrowIfNull(connection);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var connections = (await ReadAsync(cancellationToken)).ToList();
            var index = connections.FindIndex(item => item.Id == connection.Id);
            if (index >= 0)
                connections[index] = connection;
            else
                connections.Add(connection);
            await WriteAsync(connections, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        if (_readOnly)
            throw new InvalidOperationException("The connection catalog is read-only.");
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var connections = (await ReadAsync(cancellationToken))
                .Where(item => item.Id != id)
                .ToList();
            await WriteAsync(connections, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<IReadOnlyList<StorageConnection>> ReadAsync(CancellationToken cancellationToken)
    {
        if (_readOnly)
        {
            try
            {
                return (await ReadValidatedAsync(_filePath, cancellationToken, allowReplacement: true)).Connections!;
            }
            catch (IOException exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
            {
                if (File.Exists(_filePath + ".bak"))
                    throw new InvalidDataException("The connection catalog needs recovery.", exception);
                return [];
            }
        }

        var result = await RecoverableJsonFile.LoadAsync(
            _filePath, ReadValidatedAsync, allowDefaults: false, cancellationToken);
        LastRecovery = result.Recovery;
        return result.Value?.Connections ?? [];
    }

    private Task WriteAsync(List<StorageConnection> connections, CancellationToken cancellationToken) =>
        RecoverableJsonFile.WriteAsync(
            _filePath, new ConnectionFile { Schema = 1, Connections = connections },
            SerializerOptions, ReadValidatedAsync, cancellationToken);

    private static Task<ConnectionFile> ReadValidatedAsync(
        string path, CancellationToken cancellationToken) => ReadValidatedAsync(path, cancellationToken, false);

    private static async Task<ConnectionFile> ReadValidatedAsync(string path, CancellationToken cancellationToken, bool allowReplacement)
    {
        await using var stream = new FileStream(path, FileMode.Open,
            FileAccess.Read, allowReplacement ? FileShare.Read | FileShare.Delete : FileShare.Read, 4096, FileOptions.Asynchronous);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("The connection catalog must be an object.");

        // Check the version before deserializing records: a newer schema may
        // also introduce enum values the current serializer does not understand.
        var schemaProperty = document.RootElement.EnumerateObject()
            .FirstOrDefault(property => string.Equals(
                property.Name, "Schema", StringComparison.OrdinalIgnoreCase));
        if (schemaProperty.Value.ValueKind == JsonValueKind.Number &&
            (!schemaProperty.Value.TryGetInt64(out var schemaVersion) || schemaVersion > 1))
            throw new UnsupportedConfigurationVersionException("The connection catalog uses a newer schema.");

        var file = document.RootElement.Deserialize<ConnectionFile>(SerializerOptions)
            ?? throw new InvalidDataException("The connection catalog is empty.");
        if (file.Schema > 1)
            throw new UnsupportedConfigurationVersionException("The connection catalog uses a newer schema.");
        if (file.Schema != 1 || file.Connections is null)
            throw new InvalidDataException("The connection catalog has missing or invalid required fields.");

        var ids = new HashSet<Guid>();
        foreach (var connection in file.Connections)
        {
            if (connection is null ||
                connection.Id == Guid.Empty || !ids.Add(connection.Id) ||
                string.IsNullOrWhiteSpace(connection.Name) ||
                !Enum.IsDefined(connection.Provider) ||
                string.IsNullOrWhiteSpace(connection.Region) ||
                connection.Endpoint is null ||
                string.IsNullOrWhiteSpace(connection.CredentialReference))
                throw new InvalidDataException("The connection catalog contains an invalid connection.");
        }

        file.Connections = file.Connections
            .OrderBy(connection => connection.Name, StringComparer.OrdinalIgnoreCase).ToList();
        return file;
    }

    private sealed class ConnectionFile
    {
        public int Schema { get; set; } = 1;
        public List<StorageConnection>? Connections { get; set; }
    }
}
