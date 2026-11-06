using Cavdeck.App.Controls;
using Cavdeck.App.Features.Explorer;
using Cavdeck.App.Localization;
using Cavdeck.Application.Abstractions;
using Cavdeck.Application.Connections;
using Cavdeck.Application.Explorer;
using Cavdeck.Application.Places;
using Cavdeck.Application.Preview;
using Cavdeck.Application.Settings;
using Cavdeck.Application.Transfers;
using Cavdeck.Domain.Storage;
using Xunit;

namespace Cavdeck.App.Tests;

public sealed partial class ExplorerConnectionReconnectTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Staying_with_a_draft_cannot_reenable_the_old_listing(bool anotherMetadataUpdate)
    {
        var old = new StorageConnection(Guid.NewGuid(), "Original", StorageProviderKind.Minio,
            "http://old:9000", "us-east-1", "cred", RestrictedBucket: "assets");
        var updated = old with { Endpoint = "http://new:9000" };
        var provider = new Provider();
        var texts = new LocalizationService();
        var persistence = new Persistence();
        var connections = new Connections(old);
        await using var engine = new TransferEngine(null!, new MemoryTransferJournal(), connections);
        var preview = new PreviewService(provider);
        using var explorer = new ExplorerViewModel(new ExplorerService(provider), engine, texts,
            new DialogService(texts), new ToastService(texts), new ApplicationState(), persistence,
            new PlaceLibrary(persistence), null!, preview, provider, new ExplorerClipboard(), connections);
        await explorer.OpenAsync(old);
        var oldRow = Assert.Single(explorer.Rows);
        await explorer.OpenEntryAsync(oldRow);
        explorer.Preview.EditCommand.Execute(null);
        explorer.Preview.EditorText = "unsaved draft";
        Assert.True(explorer.HasUnsavedChanges);

        connections.Current = updated;
        await explorer.ApplyConnectionChangeAsync(new ConnectionChange(old, updated));
        explorer.Preview.StayCommand.Execute(null);
        if (anotherMetadataUpdate)
        {
            var renamed = updated with { Name = "Renamed" };
            connections.Current = renamed;
            await explorer.ApplyConnectionChangeAsync(new ConnectionChange(updated, renamed));
            explorer.Preview.StayCommand.Execute(null);
        }
        Assert.Empty(explorer.Rows);
        Assert.Empty(explorer.SelectedRows);
        Assert.False(explorer.CanWrite);
        Assert.False(explorer.CanDelete);
        Assert.False(explorer.CanOpen);
        Assert.False(explorer.CanCopyTo);
        Assert.False(explorer.Preview.CanSave);
        Assert.Equal("unsaved draft", explorer.Preview.EditorText);
        Assert.True(explorer.ShowSide);
        await explorer.OpenEntryAsync(oldRow);
        Assert.Equal("unsaved draft", explorer.Preview.EditorText);
        Assert.Single(provider.BrowsedEndpoints);

        // Retry must offer the draft decision again after Stay cleared the ordinary callback.
        explorer.RefreshCommand.Execute(null);
        Assert.True(explorer.Preview.ShowLeaveBar);
        explorer.Preview.DiscardLeaveCommand.Execute(null);
        Assert.False(explorer.HasUnsavedChanges);
        Assert.True(explorer.CanWrite);
        Assert.Single(explorer.Rows);
        Assert.Equal(new[] { old.Endpoint, updated.Endpoint }, provider.BrowsedEndpoints);
    }

    private sealed class Connections(StorageConnection current) : ITransferConnections
    {
        public StorageConnection Current { get; set; } = current;
        public Task<StorageConnection?> FindAsync(Guid id, CancellationToken cancellationToken) =>
            Task.FromResult<StorageConnection?>(Current.Id == id ? Current : null);
    }

    private sealed class Persistence : ISettingsStore, IPlaceStore
    {
        public Task<UserPreferences> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new UserPreferences());
        public Task SaveAsync(UserPreferences preferences, CancellationToken cancellationToken = default) => Task.CompletedTask;
        Task<PlaceSnapshot> IPlaceStore.LoadAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new PlaceSnapshot([], []));
        public Task SaveAsync(PlaceSnapshot snapshot, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class Provider : IObjectStorageProvider, IObjectStorageProviderFactory
    {
        public List<string> BrowsedEndpoints { get; } = [];
        public List<(string Prefix, string? Token)> BrowsedPages { get; } = [];
        public List<string> BrowsedBuckets { get; } = [];
        public Func<string, string?, CancellationToken, Task<PrefixPage>>? Browse { get; init; }
        public Func<CancellationToken, Task<StoragePage<StorageObject>>>? Search { get; init; }
        public StorageProviderKind Kind => StorageProviderKind.Minio;
        public ProviderCapabilities Capabilities => ConnectionPlanner.Capabilities(Kind);
        public IObjectStorageProvider Create(StorageProviderKind kind) => this;
        private static ObjectHead Head(string bucket, string key) => new(bucket, key, 4, "etag", "text/plain");
        public Task<PrefixPage> ListPrefixAsync(StorageConnection connection, string bucket, string prefix,
            string? continuationToken, int pageSize, CancellationToken cancellationToken)
        {
            BrowsedEndpoints.Add(connection.Endpoint);
            BrowsedPages.Add((prefix, continuationToken));
            BrowsedBuckets.Add(bucket);
            if (Browse is not null)
                return Browse(prefix, continuationToken, cancellationToken);
            return Task.FromResult(new PrefixPage([], [new StorageObject(bucket, prefix + "notes.txt",
                4, DateTimeOffset.UnixEpoch, "etag", "text/plain")], null));
        }
        public Task<ObjectHead> HeadObjectAsync(StorageConnection connection, string bucket, string key,
            CancellationToken cancellationToken) => Task.FromResult(Head(bucket, key));
        public Task<ObjectBody> ReadObjectAsync(StorageConnection connection, string bucket, string key,
            int maxBytes, CancellationToken cancellationToken) =>
            Task.FromResult(new ObjectBody(Head(bucket, key), "text"u8.ToArray(), false));
        public Task<IReadOnlyList<StorageBucket>> ListBucketsAsync(StorageConnection connection,
            CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<StoragePage<StorageObject>> ListObjectsAsync(StorageConnection connection, string bucket,
            string prefix, string? continuationToken, int pageSize, CancellationToken cancellationToken) =>
            Search?.Invoke(cancellationToken) ?? throw new NotSupportedException();
        public Task<bool> ObjectExistsAsync(StorageConnection connection, string bucket, string key,
            CancellationToken cancellationToken) => Task.FromResult(false);
        public Task CopyObjectAsync(StorageConnection connection, string sourceBucket, string sourceKey,
            string destinationBucket, string destinationKey, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task UploadAsync(StorageConnection connection, string bucket, string key, Stream content,
            string? contentType, IProgress<long>? progress, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DownloadAsync(StorageConnection connection, string bucket, string key, Stream destination,
            IProgress<long>? progress, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DeleteAsync(StorageConnection connection, string bucket, string key,
            CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ObjectHead> ReplaceObjectAsync(StorageConnection connection, string bucket, string key,
            Stream content, string? contentType, string? ifMatchETag, CancellationToken cancellationToken) =>
            Task.FromResult(new ObjectHead(bucket, key, content.Length, "saved-etag", contentType));
    }
}
