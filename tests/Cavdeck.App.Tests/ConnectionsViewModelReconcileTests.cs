using Cavdeck.App.Controls;
using Cavdeck.App.Features.Connections;
using Cavdeck.App.Localization;
using Cavdeck.Application.Abstractions;
using Cavdeck.Application.Connections;
using Cavdeck.Domain.Storage;
using Xunit;

namespace Cavdeck.App.Tests;

public sealed class ConnectionsViewModelReconcileTests
{
    [Fact]
    public void External_reconcile_updates_items_in_place_and_publishes_only_real_changes()
    {
        var texts = new LocalizationService();
        using var toasts = new ToastService(texts);
        var viewModel = new ConnectionsViewModel(
            new ConnectionService(new EmptyCatalog(), new EmptyVault(), new SuccessfulProbe()),
            texts, new DialogService(texts), toasts);

        var first = CreateConnection("first");
        var second = CreateConnection("second");
        viewModel.ReconcileExternal([first, second]);

        var secondItem = viewModel.Items.Single(item => item.Id == second.Id);
        viewModel.Select(second.Id);

        var updates = new List<ConnectionChange>();
        var deletions = new List<Guid>();
        viewModel.ConnectionUpdated += (_, change) => updates.Add(change);
        viewModel.ConnectionDeletedExternally += (_, id) => deletions.Add(id);

        var renamed = second with { Name = "renamed" };
        var third = CreateConnection("third");
        viewModel.ReconcileExternal([renamed, third]);

        Assert.Equal(new[] { renamed.Id, third.Id }, viewModel.Items.Select(item => item.Id));
        Assert.Same(secondItem, viewModel.Items[0]);
        Assert.Equal("renamed", secondItem.Name);
        Assert.True(secondItem.IsSelected);
        Assert.Equal(second.Id, viewModel.SelectedId);
        Assert.Equal(new[] { first.Id }, deletions);
        var change = Assert.Single(updates);
        Assert.Equal(second, change.Previous);
        Assert.Equal(renamed, change.Current);

        viewModel.ReconcileExternal([renamed, third]);

        Assert.Single(updates);
        Assert.Single(deletions);
        Assert.Equal(2, viewModel.Count);
    }

    private static StorageConnection CreateConnection(string name) =>
        new(Guid.NewGuid(), name, StorageProviderKind.Minio,
            $"http://{name}.example:9000", "us-east-1", $"credential-{name}");

    private sealed class EmptyCatalog : IConnectionCatalog
    {
        public Task<IReadOnlyList<StorageConnection>> ListAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<StorageConnection>>([]);

        public Task<IReadOnlyList<StorageConnection>> ListForMutationAsync(CancellationToken cancellationToken) =>
            ListAsync(cancellationToken);

        public Task UpsertAsync(StorageConnection connection, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class EmptyVault : ICredentialVault
    {
        public Task SaveAsync(string reference, string accessKeyId, string secretAccessKey,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<(string AccessKeyId, string SecretAccessKey)?> ReadAsync(
            string reference, CancellationToken cancellationToken) =>
            Task.FromResult<(string AccessKeyId, string SecretAccessKey)?>(null);

        public Task DeleteAsync(string reference, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class SuccessfulProbe : IStorageProbe
    {
        public Task<StorageProbeResult> ProbeAsync(StorageProbeRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(StorageProbeResult.Success(0, null));
    }
}
