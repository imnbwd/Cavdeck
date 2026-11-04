using Cavdeck.Application.Shell;
using Xunit;

namespace Cavdeck.Application.Tests;

public sealed class ShellSessionTests
{
    [Fact]
    public void Explorer_tab_roundtrips()
    {
        var session = ShellSession.From(["explorer"], 0);
        Assert.Equal(ShellPage.Explorer, session.Active);
        Assert.Equal(["explorer"], session.TabIds);
    }

    [Fact]
    public void Settings_opens_once_and_closes_back_to_the_previous_tab()
    {
        var session = ShellSession.From(["connections", "recent"], 0);
        var previousId = session.IdAt(0);

        Assert.True(session.ActivateOrOpen(ShellPage.Settings));
        Assert.Equal(ShellPage.Settings, session.Active);
        Assert.Equal(["connections", "recent", "settings"], session.TabIds);
        Assert.False(session.ActivateOrOpen(ShellPage.Settings));
        Assert.Equal(3, session.Count);

        Assert.True(session.CloseAt(session.ActiveIndex));
        Assert.Equal(previousId, session.IdAt(session.ActiveIndex));
        Assert.Equal(ShellPage.Connections, session.Active);
    }

    [Fact]
    public void Settings_restoration_remembers_the_last_active_work_tab()
    {
        var session = ShellSession.From([ShellPages.Connections, ShellPages.Explorer,
            ShellPages.Recent], 0);
        Assert.True(session.Activate(1));
        Assert.True(session.ActivateOrOpen(ShellPage.Settings));

        Assert.Equal(ShellPage.Settings, session.Active);
        Assert.Equal(1, session.RestorableActiveIndex);

        Assert.True(session.Activate(2));
        Assert.Equal(2, session.RestorableActiveIndex);
    }

    [Fact]
    public void Settings_without_a_previous_work_tab_uses_startup_fallback()
    {
        var session = ShellSession.From([ShellPages.Settings], 0);

        Assert.Equal(0, session.RestorableActiveIndex);
    }

    [Fact]
    public void Tool_tab_is_reused_and_saved_singletons_are_deduplicated()
    {
        var session = ShellSession.From(["connections", "transfers", "transfers", "settings", "settings"], 0);
        Assert.Equal(["connections", "transfers", "settings"], session.TabIds);
        Assert.True(session.ActivateOrOpen(ShellPage.Transfers));
        Assert.Equal(3, session.Count);
        Assert.Equal(ShellPage.Transfers, session.Active);

        var restored = ShellSession.From(["connections", "recent", "recent", "settings"], 2);
        Assert.Equal(ShellPage.Recent, restored.Active);
    }

    [Fact]
    public void Closing_the_last_tab_returns_to_connections()
    {
        var session = ShellSession.From(["settings"], 0);
        Assert.True(session.CloseAt(0));
        Assert.Equal(["connections"], session.TabIds);
        Assert.False(session.CloseAt(0));
    }

    [Fact]
    public void Unknown_tabs_fall_back_to_connections()
    {
        var session = ShellSession.From(["nope", ""], 4);
        Assert.Equal(ShellPage.Connections, session.Active);
        Assert.Equal(0, session.ActiveIndex);
    }

    [Fact]
    public void Saved_tabs_keep_order_and_clamp_the_active_index()
    {
        var session = ShellSession.From(["recent", "favorites", "nope"], 9);
        Assert.Equal(2, session.Count);
        Assert.Equal(ShellPage.Favorites, session.Active);
        Assert.Equal(["recent", "favorites"], session.TabIds);
    }

    [Fact]
    public void Navigate_changes_only_the_active_tab()
    {
        var session = ShellSession.From(["connections", "recent"], 1);
        Assert.False(session.Navigate(ShellPage.Recent));
        Assert.True(session.Navigate(ShellPage.Transfers));
        Assert.Equal(ShellPage.Transfers, session.PageAt(1));
        Assert.Equal(ShellPage.Connections, session.PageAt(0));
    }

    [Fact]
    public void Open_activates_the_new_tab_until_the_cap()
    {
        var session = ShellSession.CreateDefault();
        for (var i = 0; i < ShellSession.MaxTabs - 1; i++)
            Assert.True(session.TryOpen(ShellPage.Favorites));

        Assert.Equal(ShellSession.MaxTabs, session.Count);
        Assert.False(session.TryOpen(ShellPage.Recent));
        Assert.Equal(ShellPage.Favorites, session.Active);
    }

    [Fact]
    public void At_capacity_existing_tool_tabs_activate_but_new_tool_tabs_are_rejected()
    {
        var ids = new[] { ShellPages.Connections, ShellPages.Transfers }
            .Concat(Enumerable.Repeat(ShellPages.Explorer, ShellSession.MaxTabs - 2));
        var session = ShellSession.From(ids, 0);

        Assert.Equal(ShellSession.MaxTabs, session.Count);
        Assert.True(session.ActivateOrOpen(ShellPage.Transfers));
        Assert.Equal(ShellPage.Transfers, session.Active);
        Assert.False(session.ActivateOrOpen(ShellPage.Settings));
        Assert.Equal(ShellSession.MaxTabs, session.Count);
        Assert.Equal(ShellPage.Transfers, session.Active);
    }

    [Fact]
    public void Close_selects_the_neighbor_and_resets_the_last_tab()
    {
        var session = ShellSession.From(["connections", "recent", "transfers"], 2);
        Assert.True(session.CloseAt(0));
        Assert.Equal(ShellPage.Transfers, session.Active);
        Assert.Equal(1, session.ActiveIndex);

        Assert.True(session.CloseAt(1));
        Assert.Equal(ShellPage.Recent, session.Active);
        Assert.True(session.CloseAt(0));
        Assert.Equal(ShellPage.Connections, session.Active);
        Assert.Equal(1, session.Count);
    }

    [Fact]
    public void Cycle_wraps()
    {
        var session = ShellSession.From(["connections", "recent", "favorites"], 0);
        Assert.True(session.Cycle(-1));
        Assert.Equal(2, session.ActiveIndex);
        Assert.True(session.Cycle(1));
        Assert.Equal(0, session.ActiveIndex);
        Assert.False(ShellSession.CreateDefault().Cycle(1));
    }

    [Fact]
    public void Move_keeps_the_active_tab_with_its_page()
    {
        var session = ShellSession.From(["connections", "recent", "favorites"], 0);
        Assert.True(session.Move(0, 2));
        Assert.Equal(ShellPage.Connections, session.Active);
        Assert.Equal(["recent", "favorites", "connections"], session.TabIds);

        Assert.True(session.Move(2, 0));
        Assert.Equal(0, session.ActiveIndex);
        Assert.Equal(ShellPage.Connections, session.Active);
        Assert.False(session.Move(0, 0));
    }

    [Fact]
    public void Tabs_keep_distinct_ids_when_one_is_navigated()
    {
        var session = ShellSession.From(["connections", "explorer"], 0);
        var first = session.IdAt(0);
        var second = session.IdAt(1);

        Assert.NotEqual(first, second);
        Assert.True(session.Navigate(ShellPage.Explorer));
        Assert.Equal(first, session.IdAt(0));
        Assert.Equal(second, session.IdAt(1));
        Assert.Equal(ShellPage.Explorer, session.PageAt(0));
        Assert.Equal(ShellPage.Explorer, session.PageAt(1));
    }

    [Fact]
    public void Open_assigns_a_new_id_without_changing_existing_tabs()
    {
        var session = ShellSession.From(["connections"], 0);
        var original = session.IdAt(0);

        Assert.True(session.TryOpen(ShellPage.Explorer));
        Assert.Equal(original, session.IdAt(0));
        Assert.NotEqual(original, session.IdAt(1));
        Assert.Equal(ShellPage.Connections, session.PageAt(0));
        Assert.Equal(ShellPage.Explorer, session.PageAt(1));
    }

    [Fact]
    public void Move_keeps_each_tab_id_with_its_page()
    {
        var session = ShellSession.From(["connections", "recent", "favorites"], 0);
        var ids = new[] { session.IdAt(0), session.IdAt(1), session.IdAt(2) };

        Assert.True(session.Move(0, 2));
        Assert.Equal(ids[1], session.IdAt(0));
        Assert.Equal(ids[2], session.IdAt(1));
        Assert.Equal(ids[0], session.IdAt(2));
        Assert.Equal(ShellPage.Recent, session.PageAt(0));
        Assert.Equal(ShellPage.Connections, session.Active);
    }

    [Fact]
    public void Close_removes_only_that_tab_id()
    {
        var session = ShellSession.From(["connections", "recent", "transfers"], 1);
        var keepLeft = session.IdAt(0);
        var closing = session.IdAt(1);
        var keepRight = session.IdAt(2);

        Assert.True(session.CloseAt(1));
        Assert.Equal(keepLeft, session.IdAt(0));
        Assert.Equal(keepRight, session.IdAt(1));
        Assert.DoesNotContain(closing, new[] { session.IdAt(0), session.IdAt(1) });
        Assert.Equal(ShellPage.Transfers, session.Active);
    }
}
