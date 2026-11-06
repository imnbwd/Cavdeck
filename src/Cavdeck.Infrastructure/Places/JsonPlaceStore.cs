using System.Text.Json;
using Cavdeck.Application.Places;

namespace Cavdeck.Infrastructure.Places;

public sealed class JsonPlaceStore : IPlaceStore
{
    private readonly string _filePath;
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public JsonPlaceStore() : this(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Cavdeck", "places.json")) { }

    public JsonPlaceStore(string filePath) => _filePath = filePath;

    public async Task<PlaceSnapshot> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_filePath))
            return new PlaceSnapshot([], []);
        await using var stream = await Task.Run(() => new FileStream(_filePath, FileMode.Open,
            FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous), cancellationToken);
        var loaded = await JsonSerializer.DeserializeAsync<PlaceSnapshot>(stream, Options, cancellationToken);
        return new PlaceSnapshot(loaded?.Favorites ?? [], loaded?.Recent ?? [])
        {
            PathHistory = loaded?.PathHistory ?? []
        };
    }

    public async Task SaveAsync(PlaceSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(_filePath)!;
        await Task.Run(() => Directory.CreateDirectory(directory), cancellationToken);
        var temp = _filePath + ".tmp";
        try
        {
            await using (var stream = await Task.Run(() => new FileStream(temp, FileMode.Create,
                FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous), cancellationToken))
            {
                await JsonSerializer.SerializeAsync(stream, snapshot, Options, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            await Task.Run(() => File.Move(temp, _filePath, true), cancellationToken);
        }
        finally
        {
            await Task.Run(() =>
            {
                if (File.Exists(temp)) File.Delete(temp);
            });
        }
    }
}
