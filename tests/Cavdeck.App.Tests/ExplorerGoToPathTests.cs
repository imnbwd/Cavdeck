using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Cavdeck.App.Controls;
using Cavdeck.App.Features.Explorer;
using Cavdeck.App.Localization;
using Cavdeck.App.ViewModels;
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
    internal static void VerifyGoToPathHeaderLayout()
    {
        var session = PathSession.OpenAsync(new Provider(), readOnly: true).GetAwaiter().GetResult();
        try
        {
            var view = new ExplorerView
            {
                DataContext = new ExplorerWorkspace(session.Explorer, session.Texts, new RelayCommand(_ => { }))
            };
            try
            {
                foreach (var width in new[] { 460, 900 })
                {
                    view.Measure(new Size(width, 420));
                    view.Arrange(new Rect(0, 0, width, 420));
                    view.UpdateLayout();

                    var button = Assert.Single(FindButtons(view), item =>
                        ReferenceEquals(item.Command, session.Explorer.GoToPathCommand));
                    var crumbs = Assert.IsType<ScrollViewer>(view.FindName("CrumbScroll"));
                    var buttonBounds = button.TransformToAncestor(view).TransformBounds(new Rect(button.RenderSize));
                    var crumbBounds = crumbs.TransformToAncestor(view).TransformBounds(new Rect(crumbs.RenderSize));
                    Assert.True(button.IsEnabled);
                    Assert.True(button.ActualWidth >= 32);
                    Assert.True(buttonBounds.Right <= crumbBounds.Left);
                    Assert.True(crumbBounds.Width >= 180);
                }
            }
            finally
            {
                view.DataContext = null;
            }
        }
        finally
        {
            session.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        static IEnumerable<Button> FindButtons(DependencyObject node)
        {
            if (node is Button button)
                yield return button;
            for (var index = 0; index < VisualTreeHelper.GetChildrenCount(node); index++)
                foreach (var child in FindButtons(VisualTreeHelper.GetChild(node, index)))
                    yield return child;
        }
    }

    [Fact]
    public async Task Cross_bucket_navigation_keeps_the_target_metadata_when_the_source_draft_is_saved()
    {
        var provider = new Provider();
        await using var session = await PathSession.OpenAsync(provider, allowCrossBucket: true);
        var explorer = session.Explorer;
        await explorer.OpenEntryAsync(Assert.Single(explorer.Rows));
        explorer.Preview.EditCommand.Execute(null);
        explorer.Preview.EditorText = "new source content";

        var pending = NavigateAsync(explorer, "s3://other/scope/notes.txt");
        Assert.True(explorer.Preview.ShowLeaveBar);
        Assert.Equal("assets", explorer.Place?.Bucket);
        Assert.Empty(session.Places.PathHistory);
        await ((AsyncRelayCommand)explorer.Preview.SaveCommand).ExecuteAsync(null);
        await pending;

        Assert.Equal("other", explorer.Place?.Bucket);
        Assert.Equal("etag", Assert.Single(explorer.SelectedRows).Entry.ETag);
        Assert.Equal(new[] { "assets", "other" }, provider.BrowsedBuckets);
        Assert.Equal("other", Assert.Single(session.Places.PathHistory).Bucket);
    }

    [Fact]
    public async Task Explicit_bucket_mode_and_history_recall_use_the_original_destination()
    {
        var provider = new Provider();
        await using var session = await PathSession.OpenAsync(provider, allowCrossBucket: true);
        var explorer = session.Explorer;

        await NavigateAsync(explorer, "notes.txt");
        await explorer.GoToPathAsync(async (_, validate, token) =>
        {
            Assert.Null(await validate(new ExplorerPathInput("other/folder/", BucketPath: true), token));
            return true;
        });
        Assert.Equal("other", explorer.Place?.Bucket);
        Assert.Equal("scope/folder/", explorer.Place?.Prefix);

        await explorer.GoToPathAsync(async (data, validate, token) =>
        {
            Assert.Equal("/folder/", data.Initial);
            var previousBucket = Assert.Single(data.Recent);
            Assert.Equal(new ExplorerPathInput("s3://assets/scope/"), previousBucket.Input);
            var file = Assert.Single(data.History, entry => entry.Target == "s3://assets/scope/notes.txt");
            Assert.Equal(new ExplorerPathInput("s3://assets/scope/notes.txt", FileOnly: true), file.Input);
            Assert.Null(await validate(file.Input, token));
            return true;
        });

        Assert.Equal("assets", explorer.Place?.Bucket);
        Assert.Equal("scope/notes.txt", Assert.Single(explorer.SelectedRows).Entry.Key);
        Assert.Equal(2, session.Places.PathHistory.Count);
        Assert.Equal("assets", session.Places.PathHistory[0].Bucket);
        await explorer.GoToPathAsync(async (data, _, _) =>
        {
            Assert.NotNull(data.ClearHistory);
            Assert.True(await data.ClearHistory());
            return false;
        });
        Assert.Empty(session.Places.PathHistory);
        Assert.Equal(2, session.Places.Recent.Count);
    }

    [Theory]
    [InlineData(Key.G, ModifierKeys.Control, false, false, true)]
    [InlineData(Key.G, ModifierKeys.Control, true, true, true)]
    [InlineData(Key.G, ModifierKeys.Control, true, false, false)]
    [InlineData(Key.G, ModifierKeys.Control | ModifierKeys.Shift, false, false, false)]
    [InlineData(Key.G, ModifierKeys.Control | ModifierKeys.Alt, true, true, false)]
    [InlineData(Key.V, ModifierKeys.Control, false, false, true)]
    [InlineData(Key.V, ModifierKeys.Control, true, true, false)]
    [InlineData(Key.V, ModifierKeys.Control, true, false, false)]
    [InlineData(Key.V, ModifierKeys.Control | ModifierKeys.Shift, false, false, false)]
    [InlineData(Key.V, ModifierKeys.None, false, false, false)]
    [InlineData(Key.F, ModifierKeys.Control, false, false, false)]
    public void Workspace_shortcuts_require_exact_gestures_and_respect_text_editing(
        Key key, ModifierKeys modifiers, bool editing, bool search, bool expected) =>
        Assert.Equal(expected, ExplorerView.IsWorkspaceShortcut(key, modifiers, editing, search));

    [Fact]
    public async Task Go_to_path_browses_read_only_scope_and_clears_the_filter()
    {
        var provider = new Provider();
        await using var session = await PathSession.OpenAsync(provider, readOnly: true);
        var explorer = session.Explorer;
        explorer.Filter = "old filter";
        var commandStates = new List<bool>();
        explorer.GoToPathCommand.CanExecuteChanged += (_, _) =>
            commandStates.Add(explorer.GoToPathCommand.CanExecute(null));
        Assert.True(explorer.GoToPathCommand.CanExecute(null));

        await explorer.GoToPathAsync(async (initial, validate, token) =>
        {
            Assert.Equal("/", initial.Initial);
            Assert.False(explorer.CanGoToPath);
            Assert.False(explorer.GoToPathCommand.CanExecute(null));
            Assert.Null(await validate(new ExplorerPathInput("sub/"), token));
            Assert.Equal("scope/", explorer.Place?.Prefix);
            Assert.Equal("old filter", explorer.Filter);
            return true;
        });

        Assert.Equal("scope/sub/", explorer.Place?.Prefix);
        Assert.Empty(explorer.Filter);
        Assert.Equal("scope/sub/notes.txt", Assert.Single(explorer.Rows).Entry.Key);
        Assert.Equal("scope/sub/", explorer.Crumbs.Last().Location.Prefix);
        Assert.Equal(new[] { ("scope/", (string?)null), ("scope/sub/", (string?)null) }, provider.BrowsedPages);
        Assert.Equal("scope/sub/", session.Places.Recent[0].Prefix);
        Assert.False(commandStates[0]);
        Assert.True(commandStates[^1]);

        await explorer.GoToPathAsync((initial, _, _) =>
        {
            Assert.Equal("/sub/", initial.Initial);
            return Task.FromResult(false);
        });
    }

    [Fact]
    public async Task Go_to_path_preserves_an_active_search_until_a_destination_is_accepted()
    {
        var result = new TaskCompletionSource<StoragePage<StorageObject>>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken searchToken = default;
        var provider = new Provider { Search = token => { searchToken = token; return result.Task; } };
        await using var session = await PathSession.OpenAsync(provider);
        var explorer = session.Explorer;
        explorer.SearchScopeIndex = 3;
        explorer.Filter = "notes";
        var search = ((AsyncRelayCommand)explorer.RunSearchCommand).ExecuteAsync(null);
        Assert.True(explorer.ShowSearchLoading);

        await explorer.GoToPathAsync(async (_, validate, token) =>
        {
            Assert.Equal(session.Texts["ExplorerPath_Invalid"], await validate(new ExplorerPathInput("../outside"), token));
            Assert.False(searchToken.IsCancellationRequested);
            Assert.True(explorer.ShowSearchLoading);
            Assert.Null(await validate(new ExplorerPathInput("/destination/"), token));
            Assert.False(searchToken.IsCancellationRequested);
            return true;
        });

        Assert.True(searchToken.IsCancellationRequested);
        Assert.False(explorer.ShowSearchStatus);
        Assert.Empty(explorer.Filter);
        result.SetResult(new StoragePage<StorageObject>([Item("scope/notes.txt")], null));
        await search;
        Assert.Equal("scope/destination/notes.txt", Assert.Single(explorer.Rows).Entry.Key);
    }

    [Theory]
    [InlineData("missing/", "ExplorerPath_NotFound")]
    [InlineData("../secret", "ExplorerPath_Invalid")]
    [InlineData("denied/", "Connection_Failure_AccessDenied")]
    public async Task Failed_go_to_path_keeps_the_location_rows_filter_selection_and_paging(
        string input, string messageKey)
    {
        var provider = new Provider
        {
            Browse = (prefix, _, _) => prefix switch
            {
                "scope/" => Task.FromResult(Page([Item("scope/notes.txt")], "next")),
                "scope/denied/" => Task.FromException<PrefixPage>(new StorageException(
                    new StorageFailure(StorageFailureKind.AccessDenied, "AccessDenied", 403, "request-id",
                        "provider private details"))),
                _ => Task.FromResult(Page([]))
            }
        };
        await using var session = await PathSession.OpenAsync(provider);
        var explorer = session.Explorer;
        explorer.Filter = "notes";
        var row = Assert.Single(explorer.Rows);
        explorer.SetSelection([row]);
        var crumbs = explorer.Crumbs.ToArray();

        await explorer.GoToPathAsync(async (_, validate, token) =>
        {
            var issue = await validate(new ExplorerPathInput(input), token);
            Assert.StartsWith(session.Texts[messageKey], issue);
            Assert.DoesNotContain("provider private details", issue);
            return false;
        });

        Assert.Equal("scope/", explorer.Place?.Prefix);
        Assert.Equal("notes", explorer.Filter);
        Assert.Same(row, Assert.Single(explorer.Rows));
        Assert.Same(row, Assert.Single(explorer.SelectedRows));
        Assert.Equal(crumbs, explorer.Crumbs);
        Assert.True(explorer.ShowLoadMore);
        Assert.Empty(session.Places.PathHistory);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Go_to_path_selects_exact_file_and_paging_deduplicates_it(bool beyondFirstPage)
    {
        const string target = "scope/folder/middle.txt";
        var provider = new Provider
        {
            Browse = (prefix, token, _) => Task.FromResult((prefix, token) switch
            {
                ("scope/folder/", null) => Page(beyondFirstPage
                    ? [Item(prefix + "aaa.txt")] : [Item(prefix + "aaa.txt"), Item(target)], "next"),
                ("scope/folder/", "next") => Page(beyondFirstPage
                    ? [Item(target), Item(prefix + "zzz.txt")] : [Item(prefix + "zzz.txt")]),
                _ => Page([])
            })
        };
        await using var session = await PathSession.OpenAsync(provider);
        var explorer = session.Explorer;
        var selectionSyncs = 0;
        explorer.SelectionSyncRequested += (_, _) => selectionSyncs++;

        await NavigateAsync(explorer, "/folder/middle.txt");

        var selected = Assert.Single(explorer.SelectedRows);
        Assert.Equal(target, selected.Entry.Key);
        Assert.Equal(beyondFirstPage ? null : DateTimeOffset.UnixEpoch, selected.Entry.LastModified);
        Assert.Equal(target, explorer.Detail?.Key);
        Assert.False(explorer.Preview.IsOpen);
        Assert.Equal(1, selectionSyncs);
        Assert.Equal(new[] { ("scope/", (string?)null), ("scope/folder/", (string?)null) }, provider.BrowsedPages);
        await ((AsyncRelayCommand)explorer.LoadMoreCommand).ExecuteAsync(null);

        Assert.Equal(new[] { "aaa.txt", "middle.txt", "zzz.txt" }, explorer.Rows.Select(row => row.Name));
        Assert.Same(selected, explorer.Rows[1]);
        Assert.Same(selected, Assert.Single(explorer.SelectedRows));
        Assert.Equal(("scope/folder/", "next"), provider.BrowsedPages.Last());
        Assert.False(explorer.ShowLoadMore);
        explorer.ApplySort(ListingSort.Size, descending: true);
        Assert.Equal(new[] { "aaa.txt", "middle.txt", "zzz.txt" }, explorer.Rows.Select(row => row.Name));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Go_to_path_waits_for_the_existing_dirty_preview_decision(bool discard)
    {
        var provider = new Provider();
        await using var session = await PathSession.OpenAsync(provider);
        var explorer = session.Explorer;
        await explorer.OpenEntryAsync(Assert.Single(explorer.Rows));
        explorer.Preview.EditCommand.Execute(null);
        explorer.Preview.EditorText = "draft";

        var pending = NavigateAsync(explorer, "destination/");

        Assert.Equal("scope/", explorer.Place?.Prefix);
        Assert.True(explorer.Preview.ShowLeaveBar);
        Assert.Equal("draft", explorer.Preview.EditorText);
        Assert.Equal("scope/destination/", provider.BrowsedPages.Last().Prefix);
        (discard ? explorer.Preview.DiscardLeaveCommand : explorer.Preview.StayCommand).Execute(null);
        await pending;

        Assert.Equal(discard ? "scope/destination/" : "scope/", explorer.Place?.Prefix);
        Assert.Equal(!discard, explorer.HasUnsavedChanges);
        Assert.Equal(!discard, explorer.Preview.IsOpen);
        Assert.Equal(2, provider.BrowsedPages.Count);
        Assert.Equal(discard ? 1 : 0, session.Places.PathHistory.Count);
    }

    [Fact]
    public async Task Saving_the_current_file_applies_its_new_metadata_to_the_preloaded_listing()
    {
        var provider = new Provider();
        await using var session = await PathSession.OpenAsync(provider);
        var explorer = session.Explorer;
        await explorer.OpenEntryAsync(Assert.Single(explorer.Rows));
        explorer.Preview.EditCommand.Execute(null);
        explorer.Preview.EditorText = "updated";

        var pending = NavigateAsync(explorer, "notes.txt");
        Assert.True(explorer.Preview.ShowLeaveBar);
        await ((AsyncRelayCommand)explorer.Preview.SaveCommand).ExecuteAsync(null);
        await pending;

        var selected = Assert.Single(explorer.SelectedRows);
        Assert.Equal("scope/notes.txt", selected.Entry.Key);
        Assert.Equal(7, selected.Entry.SizeBytes);
        Assert.Equal("saved-etag", selected.Entry.ETag);
        Assert.Null(selected.Entry.LastModified);
        Assert.False(explorer.Preview.IsOpen);
        Assert.Equal(2, provider.BrowsedPages.Count);
    }

    [Fact]
    public async Task Switching_tabs_during_leave_confirmation_preserves_the_draft()
    {
        var provider = new Provider();
        await using var session = await PathSession.OpenAsync(provider);
        var explorer = session.Explorer;
        await explorer.OpenEntryAsync(Assert.Single(explorer.Rows));
        explorer.Preview.EditCommand.Execute(null);
        explorer.Preview.EditorText = "draft";

        var pending = NavigateAsync(explorer, "destination/");
        Assert.True(explorer.Preview.ShowLeaveBar);
        explorer.CancelGoToPath();
        await pending;

        Assert.Equal("scope/", explorer.Place?.Prefix);
        Assert.Equal("draft", explorer.Preview.EditorText);
        Assert.True(explorer.HasUnsavedChanges);
        Assert.False(explorer.Preview.ShowLeaveBar);
    }

    [Theory]
    [InlineData("dialog")]
    [InlineData("tab")]
    [InlineData("navigation")]
    [InlineData("reconnect")]
    [InlineData("dispose")]
    public async Task Obsolete_go_to_path_results_cannot_change_the_active_location(string supersededBy)
    {
        var result = new TaskCompletionSource<PrefixPage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new Provider
        {
            Browse = (prefix, _, _) => prefix == "scope/slow/" ? result.Task : Task.FromResult(Page([]))
        };
        await using var session = await PathSession.OpenAsync(provider);
        var explorer = session.Explorer;
        using var dialog = new CancellationTokenSource();
        var pending = explorer.GoToPathAsync(async (_, validate, _) =>
        {
            try
            {
                return await validate(new ExplorerPathInput("slow/"), dialog.Token) is null;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        });

        Assert.Equal("scope/slow/", provider.BrowsedPages.Last().Prefix);
        switch (supersededBy)
        {
            case "dialog": dialog.Cancel(); break;
            case "tab": explorer.CancelGoToPath(); break;
            case "dispose": explorer.Dispose(); break;
            case "navigation":
                await explorer.OpenPlaceAsync(explorer.Place!.Value with { Prefix = "scope/newer/" });
                break;
            case "reconnect":
                var before = explorer.Place!.Value.Connection;
                var changed = before with { Endpoint = "http://new-server:9000" };
                await explorer.ApplyConnectionChangeAsync(new Cavdeck.Application.Connections.ConnectionChange(before, changed));
                Assert.Equal(changed, explorer.Place?.Connection);
                break;
        }
        result.SetResult(Page([Item("scope/slow/file.txt")]));
        await pending;

        Assert.Equal(supersededBy == "navigation" ? "scope/newer/" : "scope/", explorer.Place?.Prefix);
        Assert.Empty(explorer.Rows);
        Assert.Empty(explorer.SelectedRows);
    }

    private static Task NavigateAsync(ExplorerViewModel explorer, string input) =>
        explorer.GoToPathAsync(async (_, validate, token) =>
        {
            Assert.Null(await validate(new ExplorerPathInput(input), token));
            return true;
        });

    private static StorageObject Item(string key) => new("assets", key, 4, DateTimeOffset.UnixEpoch, "etag", "text/plain");

    private static PrefixPage Page(IReadOnlyList<StorageObject> objects, string? token = null) => new([], objects, token);

    private sealed record PathSession(
        ExplorerViewModel Explorer, TransferEngine Engine, PlaceLibrary Places, LocalizationService Texts) : IAsyncDisposable
    {
        public static async Task<PathSession> OpenAsync(Provider provider, bool readOnly = false,
            bool allowCrossBucket = false)
        {
            var connection = new StorageConnection(Guid.NewGuid(), "Local", StorageProviderKind.Minio,
                "http://localhost:9000", "us-east-1", "test", readOnly, allowCrossBucket ? null : "assets", "scope/");
            var connections = new Connections(connection);
            var store = new Persistence();
            var places = new PlaceLibrary(store);
            var texts = new LocalizationService();
            var engine = new TransferEngine(null!, new MemoryTransferJournal(), connections);
            var explorer = new ExplorerViewModel(new ExplorerService(provider), engine, texts,
                new DialogService(texts), new ToastService(texts), new ApplicationState(), store,
                places, null!, new PreviewService(provider), provider, new ExplorerClipboard(), connections);
            if (allowCrossBucket)
                await explorer.OpenPlaceAsync(new ExplorerViewModel.ExplorerPlace(connection, "assets", "scope/", false));
            else
                await explorer.OpenAsync(connection);
            return new PathSession(explorer, engine, places, texts);
        }

        public async ValueTask DisposeAsync()
        {
            Explorer.Dispose();
            await Engine.DisposeAsync();
        }
    }
}
