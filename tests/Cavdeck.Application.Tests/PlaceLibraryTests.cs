using Cavdeck.Application.Places;
using Xunit;

namespace Cavdeck.Application.Tests;

public sealed class PlaceLibraryTests
{
    [Fact]
    public async Task Favorites_toggle_and_recent_visits_are_distinct()
    {
        var store = new MemoryStore();
        var library = new PlaceLibrary(store);
        var id = Guid.NewGuid();
        await library.LoadAsync();

        await library.ToggleFavoriteAsync(id, "photos", "2026/");
        await library.RecordVisitAsync(id, "photos", "2026/");
        await library.RecordVisitAsync(id, "photos", "2026/");

        Assert.True(library.IsFavorite(id, "photos", "2026/"));
        Assert.Single(library.Favorites);
        Assert.Single(library.Recent);
        await library.ToggleFavoriteAsync(id, "photos", "2026/");
        Assert.Empty(library.Favorites);
        Assert.Single(library.Recent);
    }

    [Fact]
    public async Task Recent_order_is_newest_first_and_bounded()
    {
        var library = new PlaceLibrary(new MemoryStore());
        var id = Guid.NewGuid();
        await library.LoadAsync();
        for (var i = 0; i < PlaceLibrary.RecentLimit + 5; i++)
            await library.RecordVisitAsync(id, "assets", $"folder-{i}/");

        Assert.Equal(PlaceLibrary.RecentLimit, library.Recent.Count);
        Assert.Equal("folder-54/", library.Recent[0].Prefix);
        Assert.DoesNotContain(library.Recent, item => item.Prefix == "folder-0/");
        await library.RecordVisitAsync(id, "assets", "folder-30/");
        Assert.Equal("folder-30/", library.Recent[0].Prefix);
        Assert.Equal(PlaceLibrary.RecentLimit, library.Recent.Count);
    }

    [Fact]
    public async Task Saved_places_survive_reload_and_deleted_connections_are_pruned()
    {
        var store = new MemoryStore();
        var kept = Guid.NewGuid();
        var deleted = Guid.NewGuid();
        var library = new PlaceLibrary(store);
        await library.LoadAsync();
        await library.ToggleFavoriteAsync(kept, null, "");
        await library.ToggleFavoriteAsync(deleted, "old", "");
        await library.RecordVisitAsync(deleted, "old", "");

        var restored = new PlaceLibrary(store);
        await restored.LoadAsync();
        Assert.Equal(2, restored.Favorites.Count);
        await restored.PruneConnectionsAsync(new HashSet<Guid> { kept });
        Assert.Single(restored.Favorites);
        Assert.Empty(restored.Recent);
    }

    private sealed class MemoryStore : IPlaceStore
    {
        private PlaceSnapshot _snapshot = new([], []);
        public Task<PlaceSnapshot> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(_snapshot);
        public Task SaveAsync(PlaceSnapshot snapshot, CancellationToken cancellationToken = default)
        {
            _snapshot = snapshot;
            return Task.CompletedTask;
        }
    }
}
