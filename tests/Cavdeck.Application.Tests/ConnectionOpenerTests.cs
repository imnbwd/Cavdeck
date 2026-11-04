using Cavdeck.Application.Shell;
using Xunit;

namespace Cavdeck.Application.Tests;

public sealed class ConnectionOpenerTests
{
    private static readonly Guid Connection = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid Other = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid Recent = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Older = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public void Remembered_tab_wins_when_it_still_shows_the_connection()
    {
        var explorers = new[]
        {
            new OpenExplorer(Older, Connection),
            new OpenExplorer(Recent, Connection)
        };
        var decision = ConnectionOpener.Choose(
            Connection, explorers, Recent, new Dictionary<Guid, int> { [Older] = 1, [Recent] = 2 },
            openTabCount: 2, maxTabs: 12, forceNew: false);

        Assert.Equal(ConnectionOpenKind.Activate, decision.Kind);
        Assert.Equal(Recent, decision.TabId);
    }

    [Fact]
    public void Stale_memory_falls_back_to_the_latest_activation()
    {
        var explorers = new[]
        {
            new OpenExplorer(Older, Connection),
            new OpenExplorer(Recent, Other)
        };
        var decision = ConnectionOpener.Choose(
            Connection, explorers, Recent, new Dictionary<Guid, int> { [Older] = 4, [Recent] = 9 },
            openTabCount: 2, maxTabs: 12, forceNew: false);

        Assert.Equal(ConnectionOpenKind.Activate, decision.Kind);
        Assert.Equal(Older, decision.TabId);
    }

    [Fact]
    public void Missing_explorer_asks_for_a_new_tab()
    {
        var decision = ConnectionOpener.Choose(
            Connection, [new OpenExplorer(Recent, Other)], null, new Dictionary<Guid, int>(),
            openTabCount: 1, maxTabs: 12, forceNew: false);

        Assert.Equal(ConnectionOpenKind.Create, decision.Kind);
        Assert.Null(decision.TabId);
    }

    [Fact]
    public void Full_session_rejects_a_new_tab()
    {
        var decision = ConnectionOpener.Choose(
            Connection, [], null, new Dictionary<Guid, int>(),
            openTabCount: 12, maxTabs: 12, forceNew: false);

        Assert.Equal(ConnectionOpenKind.Rejected, decision.Kind);
        Assert.Null(decision.TabId);
    }

    [Fact]
    public void Force_new_does_not_reuse_an_open_explorer()
    {
        var decision = ConnectionOpener.Choose(
            Connection, [new OpenExplorer(Recent, Connection)], Recent,
            new Dictionary<Guid, int> { [Recent] = 1 },
            openTabCount: 1, maxTabs: 12, forceNew: true);

        Assert.Equal(ConnectionOpenKind.Create, decision.Kind);
        Assert.Null(decision.TabId);
    }

    [Fact]
    public void Force_new_is_rejected_when_the_session_is_full()
    {
        var decision = ConnectionOpener.Choose(
            Connection, [new OpenExplorer(Recent, Connection)], Recent,
            new Dictionary<Guid, int> { [Recent] = 1 },
            openTabCount: 12, maxTabs: 12, forceNew: true);

        Assert.Equal(ConnectionOpenKind.Rejected, decision.Kind);
    }

    [Fact]
    public void Log_remembers_the_latest_tab_and_forgets_it_when_closed()
    {
        var log = new ExplorerActivationLog();
        log.NoteActive(Older, Connection);
        log.NoteActive(Recent, Connection);

        Assert.Equal(Recent, log.RememberedTab(Connection));
        Assert.True(log.ActivatedAt[Recent] > log.ActivatedAt[Older]);

        log.Forget(Recent);
        Assert.Null(log.RememberedTab(Connection));
        Assert.False(log.ActivatedAt.ContainsKey(Recent));
    }

    [Fact]
    public void Noting_a_tab_without_a_connection_does_not_clear_another_connection()
    {
        var log = new ExplorerActivationLog();
        log.NoteActive(Older, Connection);
        log.NoteActive(Recent, null);

        Assert.Equal(Older, log.RememberedTab(Connection));
        Assert.True(log.ActivatedAt.ContainsKey(Recent));
    }
}
