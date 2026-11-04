using System.Text.Json;
using Cavdeck.Application.Abstractions;
using Cavdeck.Application.Settings;
using Cavdeck.Infrastructure.Configuration;

namespace Cavdeck.Infrastructure.Settings;

public sealed class JsonSettingsStore : ISettingsStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _filePath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public JsonSettingsStore() : this(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Cavdeck", "settings.json")) { }

    public JsonSettingsStore(string filePath) => _filePath = filePath;

    public ConfigurationRecovery? LastRecovery { get; private set; }

    public async Task<UserPreferences> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var result = await RecoverableJsonFile.LoadAsync(
                _filePath, ReadValidatedAsync, allowDefaults: true, cancellationToken);
            LastRecovery = result.Recovery;
            return result.Value ?? new UserPreferences();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(UserPreferences preferences,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await RecoverableJsonFile.WriteAsync(
                _filePath, preferences, SerializerOptions, ReadValidatedAsync, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task<UserPreferences> ReadValidatedAsync(
        string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open,
            FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("The settings document must be an object.");

        // All individual preferences are optional for older versions; malformed
        // present values still fail deserialization rather than being ignored.
        return document.RootElement.Deserialize<UserPreferences>(SerializerOptions)
            ?? throw new InvalidDataException("The settings document is empty.");
    }
}
