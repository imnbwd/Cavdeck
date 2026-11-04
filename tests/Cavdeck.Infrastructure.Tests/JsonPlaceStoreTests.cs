using Cavdeck.Application.Places;
using Cavdeck.Infrastructure.Places;
using Xunit;

namespace Cavdeck.Infrastructure.Tests;

public sealed class JsonPlaceStoreTests
{
    [Fact]
    public async Task Saved_places_round_trip_without_credentials()
    {
        var directory = Path.Combine(Path.GetTempPath(), "cavdeck-places-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "places.json");
            var store = new JsonPlaceStore(path);
            var place = new SavedPlace(Guid.NewGuid(), "assets", "images/", DateTimeOffset.UtcNow);
            await store.SaveAsync(new PlaceSnapshot([place], [place]));

            var loaded = await new JsonPlaceStore(path).LoadAsync();
            Assert.Equal(place, Assert.Single(loaded.Favorites));
            Assert.Equal(place, Assert.Single(loaded.Recent));
            Assert.DoesNotContain("SecretAccessKey", await File.ReadAllTextAsync(path));
        }
        finally
        {
            var path = Path.Combine(directory, "places.json");
            if (File.Exists(path)) File.Delete(path);
            Directory.Delete(directory);
        }
    }
}
