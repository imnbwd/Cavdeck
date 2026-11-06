using Cavdeck.Domain.Storage;

namespace Cavdeck.Application.Places;

public sealed record SavedPlace(Guid ConnectionId, string? Bucket, string Prefix,
    DateTimeOffset VisitedAt);

public sealed record PathHistoryEntry(Guid ConnectionId, string SourceBucket, string SourcePrefix,
    ExplorerPathInput Input, string Bucket, string Key, bool IsFile, DateTimeOffset VisitedAt);

public sealed record PlaceSnapshot(IReadOnlyList<SavedPlace> Favorites, IReadOnlyList<SavedPlace> Recent)
{
    public IReadOnlyList<PathHistoryEntry> PathHistory { get; init; } = [];
}

public interface IPlaceStore
{
    Task<PlaceSnapshot> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(PlaceSnapshot snapshot, CancellationToken cancellationToken = default);
}

public sealed class PlaceLibrary
{
    public const int RecentLimit = 50;
    public const int PathHistoryLimit = 30;
    private readonly IPlaceStore _store;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private PlaceSnapshot _snapshot = new([], []);

    public PlaceLibrary(IPlaceStore store) => _store = store;

    public event EventHandler? Changed;
    public IReadOnlyList<SavedPlace> Favorites => _snapshot.Favorites;
    public IReadOnlyList<SavedPlace> Recent => _snapshot.Recent;
    public IReadOnlyList<PathHistoryEntry> PathHistory => _snapshot.PathHistory;

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var loaded = await _store.LoadAsync(cancellationToken);
            _snapshot = new PlaceSnapshot(
                Distinct(loaded.Favorites).ToList(),
                Distinct(loaded.Recent).Take(RecentLimit).ToList())
            {
                PathHistory = LimitHistory(loaded.PathHistory)
            };
        }
        finally { _gate.Release(); }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public bool IsFavorite(Guid connectionId, string? bucket, string prefix) =>
        Favorites.Any(item => Same(item, connectionId, bucket, prefix));

    public Task ToggleFavoriteAsync(Guid connectionId, string? bucket, string prefix,
        CancellationToken cancellationToken = default) => ChangeAsync(snapshot =>
    {
        var items = snapshot.Favorites.ToList();
        var index = items.FindIndex(item => Same(item, connectionId, bucket, prefix));
        if (index >= 0)
            items.RemoveAt(index);
        else
            items.Insert(0, New(connectionId, bucket, prefix));
        return snapshot with { Favorites = items };
    }, cancellationToken);

    public Task RemoveFavoriteAsync(SavedPlace place, CancellationToken cancellationToken = default) =>
        ChangeAsync(snapshot => snapshot with
        {
            Favorites = snapshot.Favorites.Where(item => !Same(item, place.ConnectionId,
                place.Bucket, place.Prefix)).ToList()
        }, cancellationToken);

    public Task RecordVisitAsync(Guid connectionId, string? bucket, string prefix,
        CancellationToken cancellationToken = default, PathHistoryEntry? path = null) => ChangeAsync(snapshot =>
    {
        var items = snapshot.Recent.Where(item => !Same(item, connectionId, bucket, prefix))
            .Prepend(New(connectionId, bucket, prefix)).Take(RecentLimit).ToList();
        return snapshot with
        {
            Recent = items,
            PathHistory = path is null ? snapshot.PathHistory : LimitHistory(snapshot.PathHistory.Prepend(path))
        };
    }, cancellationToken);

    public Task RemoveRecentAsync(SavedPlace place, CancellationToken cancellationToken = default) =>
        ChangeAsync(snapshot => snapshot with
        {
            Recent = snapshot.Recent.Where(item => !Same(item, place.ConnectionId,
                place.Bucket, place.Prefix)).ToList()
        }, cancellationToken);

    public Task ClearRecentAsync(CancellationToken cancellationToken = default) =>
        ChangeAsync(snapshot => snapshot with { Recent = [] }, cancellationToken);

    public Task ClearPathHistoryAsync(Guid connectionId, CancellationToken cancellationToken = default) =>
        ChangeAsync(snapshot => snapshot with
        {
            PathHistory = snapshot.PathHistory.Where(item => item.ConnectionId != connectionId).ToList()
        }, cancellationToken);

    public Task PruneConnectionsAsync(IReadOnlySet<Guid> activeIds,
        CancellationToken cancellationToken = default) => ChangeAsync(snapshot => new PlaceSnapshot(
            snapshot.Favorites.Where(item => activeIds.Contains(item.ConnectionId)).ToList(),
            snapshot.Recent.Where(item => activeIds.Contains(item.ConnectionId)).ToList())
        {
            PathHistory = snapshot.PathHistory.Where(item => activeIds.Contains(item.ConnectionId)).ToList()
        },
            cancellationToken);

    private static IReadOnlyList<PathHistoryEntry> LimitHistory(IEnumerable<PathHistoryEntry> history) =>
        history.Where(item => item is not null && item.ConnectionId != Guid.Empty
                && !string.IsNullOrWhiteSpace(item.Bucket) && item.Key is not null
                && !string.IsNullOrWhiteSpace(item.Input.Text))
            .DistinctBy(item => (item.ConnectionId, item.Bucket, item.Key, item.IsFile))
            .GroupBy(item => item.ConnectionId)
            .SelectMany(group => group.Take(PathHistoryLimit))
            .ToList();

    private async Task ChangeAsync(Func<PlaceSnapshot, PlaceSnapshot> change,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var next = change(_snapshot);
            await _store.SaveAsync(next, cancellationToken);
            _snapshot = next;
        }
        finally { _gate.Release(); }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static SavedPlace New(Guid connectionId, string? bucket, string prefix) =>
        new(connectionId, bucket, bucket is null ? "" : prefix, DateTimeOffset.UtcNow);

    private static bool Same(SavedPlace item, Guid connectionId, string? bucket, string prefix) =>
        item.ConnectionId == connectionId
        && string.Equals(item.Bucket, bucket, StringComparison.Ordinal)
        && string.Equals(item.Prefix, bucket is null ? "" : prefix, StringComparison.Ordinal);

    private static IEnumerable<SavedPlace> Distinct(IEnumerable<SavedPlace> places)
    {
        var seen = new HashSet<(Guid, string?, string)>();
        foreach (var item in places)
            if (item.ConnectionId != Guid.Empty && seen.Add((item.ConnectionId, item.Bucket,
                item.Bucket is null ? "" : item.Prefix)))
                yield return item;
    }
}
