using System.Text.Json;
using Cavdeck.Application.Settings;
using Cavdeck.Application.Shell;
using Xunit;

namespace Cavdeck.Application.Tests;

public sealed class WindowPlacementTests
{
    private static readonly ScreenArea Desktop = new(0, 0, 1920, 1080);

    [Fact]
    public void Clamp_pulls_size_and_sidebar_into_range()
    {
        var layout = new WindowLayout
        {
            Width = 20,
            Height = double.NaN,
            SidebarWidth = 900,
            Left = double.PositiveInfinity,
            Tabs = []
        };

        layout.Clamp();

        Assert.Equal(WindowLayout.CompactMinWidth, layout.Width);
        Assert.Equal(WindowLayout.DefaultHeight, layout.Height);
        Assert.Equal(WindowLayout.MaxSidebarWidth, layout.SidebarWidth);
        Assert.Null(layout.Left);
        Assert.Equal(ShellPages.Connections, Assert.Single(layout.Tabs));
    }

    [Fact]
    public void Settings_is_removed_from_old_saved_layout_without_shifting_explorer_locations()
    {
        var place = new RestorableTabLocation
        {
            ConnectionId = Guid.NewGuid(),
            Bucket = "documents",
            Prefix = "reports/"
        };
        var layout = new WindowLayout
        {
            Tabs = [ShellPages.Connections, ShellPages.Settings, ShellPages.Explorer, ShellPages.Recent],
            TabLocations = [null, null, place, null],
            ActiveTab = 1
        };

        layout.ExcludeSettingsFromStartup();

        Assert.Equal([ShellPages.Connections, ShellPages.Explorer, ShellPages.Recent], layout.Tabs);
        Assert.Equal(0, layout.ActiveTab);
        Assert.Equal(3, layout.TabLocations.Count);
        Assert.Same(place, layout.TabLocations[1]);
    }

    [Fact]
    public void Settings_restoration_prefers_last_work_tab_even_when_not_adjacent()
    {
        var layout = new WindowLayout
        {
            Tabs = [ShellPages.Connections, ShellPages.Explorer, ShellPages.Settings, ShellPages.Recent],
            ActiveTab = 0 // The active index has already been resolved to the last work tab.
        };

        layout.ExcludeSettingsFromStartup();

        Assert.Equal([ShellPages.Connections, ShellPages.Explorer, ShellPages.Recent], layout.Tabs);
        Assert.Equal(0, layout.ActiveTab);
    }

    [Fact]
    public void Settings_only_layout_falls_back_to_connections()
    {
        var layout = new WindowLayout
        {
            Tabs = [ShellPages.Settings],
            TabLocations = [null],
            ActiveTab = 0
        };

        layout.ExcludeSettingsFromStartup();

        Assert.Equal(ShellPages.Connections, Assert.Single(layout.Tabs));
        Assert.Empty(layout.TabLocations);
        Assert.Equal(0, layout.ActiveTab);
    }

    [Fact]
    public void Ordinary_work_layout_is_unchanged()
    {
        var tabs = new List<string> { ShellPages.Connections, ShellPages.Explorer };
        var locations = new List<RestorableTabLocation?> { null };
        var layout = new WindowLayout { Tabs = tabs, TabLocations = locations, ActiveTab = 1 };

        layout.ExcludeSettingsFromStartup();

        Assert.Same(tabs, layout.Tabs);
        Assert.Same(locations, layout.TabLocations);
        Assert.Equal(1, layout.ActiveTab);
    }

    [Fact]
    public void On_screen_placement_is_restored()
    {
        var layout = new WindowLayout
        {
            Left = 1800,
            Top = 1000,
            Width = 1120,
            Height = 740,
            IsMaximized = true
        };

        var restored = WindowPlacement.Resolve(layout, [Desktop]);

        Assert.Equal(1800, restored.Left);
        Assert.Equal(1000, restored.Top);
        Assert.True(restored.IsMaximized);
    }

    [Fact]
    public void Off_screen_placement_centers_and_does_not_maximize()
    {
        var layout = new WindowLayout
        {
            Left = -2000,
            Top = 40,
            IsMaximized = true
        };

        var restored = WindowPlacement.Resolve(layout, [Desktop]);

        Assert.Null(restored.Left);
        Assert.Null(restored.Top);
        Assert.False(restored.IsMaximized);
        Assert.Equal(WindowLayout.DefaultWidth, restored.Width);
    }

    [Fact]
    public void A_sliver_under_the_minimum_visible_size_is_rejected()
    {
        var layout = new WindowLayout { Left = 1900, Top = 100, Width = 1120, Height = 740 };
        var restored = WindowPlacement.Resolve(layout, [Desktop]);
        Assert.Null(restored.Left);
    }

    [Fact]
    public void Missing_monitor_list_keeps_the_saved_position()
    {
        var layout = new WindowLayout { Left = -40, Top = 12, IsMaximized = true };
        var restored = WindowPlacement.Resolve(layout, []);
        Assert.Equal(-40, restored.Left);
        Assert.True(restored.IsMaximized);
    }

    [Fact]
    public void Old_settings_json_gains_a_default_layout()
    {
        const string json = """{"Language":"zh-CN","Theme":"Dark"}""";
        var preferences = JsonSerializer.Deserialize<UserPreferences>(json);
        Assert.NotNull(preferences);
        Assert.Equal("zh-CN", preferences.Language);
        Assert.Equal(AppTheme.Dark, preferences.Theme);

        preferences.Layout = null!;
        preferences.Normalize();
        Assert.Equal(WindowLayout.DefaultSidebarWidth, preferences.Layout.SidebarWidth);
        Assert.False(preferences.Layout.SidebarCollapsed);
        Assert.Equal(ShellPages.Connections, Assert.Single(preferences.Layout.Tabs));
    }

    [Fact]
    public void Clamp_pulls_a_narrow_sidebar_up_to_the_minimum()
    {
        var layout = new WindowLayout { SidebarWidth = 120, SidebarCollapsed = true };
        layout.Clamp();
        Assert.Equal(WindowLayout.MinSidebarWidth, layout.SidebarWidth);
        Assert.True(layout.SidebarCollapsed);
        Assert.Equal(200, WindowLayout.MinSidebarWidth);
        Assert.Equal(340, WindowLayout.MaxSidebarWidth);
        Assert.Equal(250, WindowLayout.DefaultSidebarWidth);
        Assert.Equal(56, WindowLayout.CollapsedSidebarWidth);
    }
    [Fact]
    public void Native_coordinates_are_consistent_across_mixed_DPI_monitors()
    {
        // Physical desktops are adjacent even when the secondary display is 150%.
        var screens = new[]
        {
            new ScreenArea(0, 0, 1920, 1040, 1, IsPrimary: true),
            new ScreenArea(1920, 0, 2560, 1400, 1.5)
        };
        var layout = new WindowLayout
        {
            PhysicalLeft = 2330, PhysicalTop = 120,
            Left = 2330 / 1.5, Top = 80, Width = 1120, Height = 740
        };

        var result = NativeWindowPlacement.Resolve(layout, screens);

        Assert.Equal(2330, result.Left);
        Assert.Equal(120, result.Top);
        Assert.Equal(1120, result.Width);
        Assert.Equal(740, result.Height);
        Assert.Equal(1680, result.DpiWidth);
        Assert.Equal(1110, result.DpiHeight);
    }

    [Fact]
    public void Legacy_logical_coordinates_migrate_to_primary_monitor()
    {
        var layout = new WindowLayout { Left = 1200, Top = 350 };
        var screens = new[]
        {
            new ScreenArea(1920, 0, 2560, 1400, 1.5),
            new ScreenArea(0, 0, 1920, 1040, 1, IsPrimary: true)
        };

        var result = NativeWindowPlacement.Resolve(layout, screens);

        Assert.Equal(400, result.Left);
        Assert.Equal(150, result.Top);
        Assert.Equal(1120, result.Width);
        Assert.Equal(740, result.Height);
    }

    [Fact]
    public void Smaller_high_DPI_work_area_constrains_window_to_screen()
    {
        // 1920x1080 at 200% with taskbar occupying the last 80px.
        var screen = new ScreenArea(0, 0, 1920, 1000, 2, IsPrimary: true);
        var result = NativeWindowPlacement.Resolve(new WindowLayout(), [screen]);

        Assert.Equal(952, result.Width);
        Assert.Equal(492, result.Height);
        Assert.Equal(1904, result.DpiWidth);
        Assert.Equal(984, result.DpiHeight);
        Assert.InRange(result.Left, 0, 1920 - result.DpiWidth);
        Assert.InRange(result.Top, 0, 1000 - result.DpiHeight);
    }

    [Fact]
    public void Removed_monitor_moves_window_onto_the_nearest_surviving_screen()
    {
        var saved = new WindowLayout { PhysicalLeft = 4500, PhysicalTop = 600, IsMaximized = true };
        var result = NativeWindowPlacement.Resolve(saved,
        [
            new ScreenArea(-1920, 0, 1920, 1040, 1.25),
            new ScreenArea(0, 0, 1920, 1040, 1, IsPrimary: true)
        ]);

        Assert.True(result.IsMaximized);
        Assert.InRange(result.Left, 0, 1920 - result.DpiWidth);
        Assert.InRange(result.Top, 0, 1040 - result.DpiHeight);
    }

    [Fact]
    public void Almost_offscreen_native_window_is_clamped_into_work_area()
    {
        var saved = new WindowLayout
        {
            PhysicalLeft = 1800, PhysicalTop = 900,
            Width = 1120, Height = 740
        };
        var result = NativeWindowPlacement.Resolve(saved,
            [new ScreenArea(0, 0, 1920, 1040, 1)]);

        Assert.Equal(792, result.Left);
        Assert.Equal(292, result.Top);
    }

    [Fact]
    public void Negative_monitor_origin_remains_valid()
    {
        var saved = new WindowLayout { PhysicalLeft = -1600, PhysicalTop = 60 };
        var result = NativeWindowPlacement.Resolve(saved,
        [
            new ScreenArea(-1920, 0, 1920, 1080, 1.25),
            new ScreenArea(0, 0, 1920, 1040, 1, IsPrimary: true)
        ]);
        Assert.Equal(-1600, result.Left);
        Assert.Equal(60, result.Top);
        Assert.Equal(1400, result.DpiWidth);
    }

    [Fact]
    public void Corrupted_native_coordinates_fall_back_to_primary()
    {
        var saved = new WindowLayout
        {
            PhysicalLeft = double.PositiveInfinity,
            PhysicalTop = double.NaN
        };
        var result = NativeWindowPlacement.Resolve(saved,
            [new ScreenArea(0, 0, 1920, 1040, 1, IsPrimary: true)]);

        Assert.Equal(400, result.Left);
        Assert.Equal(150, result.Top);
        Assert.Null(saved.PhysicalLeft);
        Assert.Null(saved.PhysicalTop);
    }

    [Fact]
    public void Persisted_native_coordinates_round_trip_with_legacy_layout()
    {
        var original = new WindowLayout
        {
            PhysicalLeft = -1430, PhysicalTop = 24, Left = -1200,
            Top = 20, Width = 980, Height = 620
        };
        var copy = JsonSerializer.Deserialize<WindowLayout>(JsonSerializer.Serialize(original));
        Assert.NotNull(copy);
        Assert.Equal(original.PhysicalLeft, copy.PhysicalLeft);
        Assert.Equal(original.PhysicalTop, copy.PhysicalTop);
        Assert.Equal(original.Width, copy.Width);
    }

}
