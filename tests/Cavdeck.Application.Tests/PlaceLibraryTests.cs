using Cavdeck.Application.Places;
using Xunit;

namespace Cavdeck.Application.Tests;

public sealed class PlaceLibraryTests
{
    [Fact]
    public async Task Path_history_deduplicates_caps_and_clears_by_connection()
    {
        var store = new MemoryStore();
        var library = new PlaceLibrary(store);
        var id = Guid.NewGuid();
        var other = Guid.NewGuid();
        var entry = new PathHistoryEntry(id, "assets", "", new("first"), "assets", "file", true, DateTimeOffset.UtcNow);
        await library.RecordVisitAsync(other, "assets", "", path: entry with { ConnectionId = other });
        for (var index = 0; index < PlaceLibrary.PathHistoryLimit; index++)
            await library.RecordVisitAsync(id, "assets", "", path: entry with { Key = $"file-{index}" });
        var replaced = entry with { Key = "file-0", Input = new("s3://assets/file-0") };
        await library.RecordVisitAsync(id, "assets", "", path: replaced);
        Assert.Equal(PlaceLibrary.PathHistoryLimit, library.PathHistory.Count(item => item.ConnectionId == id));
        Assert.Equal(replaced, library.PathHistory.First(item => item.ConnectionId == id));
        await library.RecordVisitAsync(id, "assets", "", path: entry);
        Assert.Equal(PlaceLibrary.PathHistoryLimit + 1, library.PathHistory.Count);

        var restored = new PlaceLibrary(store);
        await restored.LoadAsync();
        Assert.Equal(library.PathHistory, restored.PathHistory);
        await restored.ClearPathHistoryAsync(id);
        Assert.Equal(other, Assert.Single(restored.PathHistory).ConnectionId);
        Assert.Equal(2, restored.Recent.Count);
        await restored.PruneConnectionsAsync(new HashSet<Guid> { id });
        Assert.Empty(restored.PathHistory);
    }

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
