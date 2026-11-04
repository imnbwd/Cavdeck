using System.Text.Json;
using Cavdeck.Application.Shell;
using Xunit;

namespace Cavdeck.Application.Tests;

public sealed class TabLocationPersistenceTests
{
    [Fact]
    public void Explorer_locations_roundtrip_independently_of_each_other()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var layout = new WindowLayout
        {
            Tabs = [ShellPages.Connections, ShellPages.Explorer, ShellPages.Explorer],
            ActiveTab = 2,
            TabLocations =
            [
                null,
                new RestorableTabLocation { ConnectionId = first, Bucket = "assets", Prefix = "photos/2026/" },
                new RestorableTabLocation { ConnectionId = second, Bucket = "backups", Prefix = "daily/" }
            ]
        };

        var json = JsonSerializer.Serialize(layout);
        Assert.DoesNotContain("AccessKey", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Secret", json, StringComparison.OrdinalIgnoreCase);

        var restored = JsonSerializer.Deserialize<WindowLayout>(json)!;
        restored.Clamp();
        Assert.Equal(2, restored.ActiveTab);
        Assert.Equal(3, restored.TabLocations.Count);
        Assert.Null(restored.TabLocations[0]);
        Assert.Equal(first, restored.TabLocations[1]!.ConnectionId);
        Assert.Equal("photos/2026/", restored.TabLocations[1]!.Prefix);
        Assert.Equal(second, restored.TabLocations[2]!.ConnectionId);
        Assert.Equal("backups", restored.TabLocations[2]!.Bucket);
    }

    [Fact]
    public void Startup_tab_restore_is_optional_without_mutating_saved_layout()
    {
        var layout = new WindowLayout
        {
            Tabs = [ShellPages.Connections, ShellPages.Explorer, ShellPages.Transfers],
            ActiveTab = 1,
            TabLocations = [null, new RestorableTabLocation { ConnectionId = Guid.NewGuid(), Bucket = "assets" }, null],
            Width = 1234
        };
        var restored = ShellSession.ForStartup(layout, restorePreviousTabs: true);
        Assert.Equal(3, restored.Count);
        Assert.Equal(ShellPage.Explorer, restored.Active);
        var fresh = ShellSession.ForStartup(layout, restorePreviousTabs: false);
        Assert.Single(fresh.TabIds);
        Assert.Equal(ShellPage.Connections, fresh.Active);
        Assert.Equal(3, layout.Tabs.Count);
        Assert.Equal(1234, layout.Width);
    }

    [Fact]
    public void Legacy_settings_without_location_data_remain_compatible()
    {
        var layout = JsonSerializer.Deserialize<WindowLayout>(
            "{\"Tabs\":[\"connections\",\"explorer\"],\"ActiveTab\":1}")!;
        layout.Clamp();

        Assert.Equal(1, layout.ActiveTab);
        Assert.Empty(layout.TabLocations);
        Assert.Equal(ShellPage.Explorer, ShellSession.From(layout.Tabs, layout.ActiveTab).Active);
    }

    [Fact]
    public void Broken_or_oversized_saved_location_arrays_are_normalized()
    {
        var layout = new WindowLayout
        {
            Tabs = [ShellPages.Explorer],
            TabLocations = Enumerable.Range(0, ShellSession.MaxTabs + 8)
                .Select(_ => (RestorableTabLocation?)new RestorableTabLocation { ConnectionId = Guid.NewGuid(), Prefix = null! })
                .ToList()
        };
        layout.Clamp();

        Assert.Equal(ShellSession.MaxTabs, layout.TabLocations.Count);
        Assert.All(layout.TabLocations, item => Assert.Equal("", item!.Prefix));
    }
}
