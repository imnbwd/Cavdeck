namespace Cavdeck.Application.Shell;

public sealed class WindowLayout
{
    public const double DefaultWidth = 1120;
    public const double DefaultHeight = 740;
    public const double MinWidth = 880;
    public const double MinHeight = 580;
    // Compact viewport for high DPI / smaller displays. The preferred size remains 880x580.
    public const double CompactMinWidth = 720;
    public const double CompactMinHeight = 420;
    public const double DefaultSidebarWidth = 250;
    public const double MinSidebarWidth = 200;
    public const double MaxSidebarWidth = 340;
    public const double CollapsedSidebarWidth = 56;

    public double? Left { get; set; }
    public double? Top { get; set; }
    public double Width { get; set; } = DefaultWidth;
    public double Height { get; set; } = DefaultHeight;
    // Physical screen coordinates: unlike WPF Left/Top DIPs, these share one
    // unambiguous virtual-desktop coordinate system across mixed-DPI monitors.
    // Nullable for settings written by older versions.
    public double? PhysicalLeft { get; set; }
    public double? PhysicalTop { get; set; }
    public bool IsMaximized { get; set; }
    public double SidebarWidth { get; set; } = DefaultSidebarWidth;
    public bool SidebarCollapsed { get; set; }
    public List<string> Tabs { get; set; } = [ShellPages.Connections];
    // Parallel to Tabs; no credentials or live object contents are ever persisted.
    public List<RestorableTabLocation?> TabLocations { get; set; } = [];
    public int ActiveTab { get; set; }

    // Settings is a temporary tool tab, not a startup destination.
    // Keep saved locations aligned with their tabs when removing it.
    public void ExcludeSettingsFromStartup()
    {
        if (Tabs is null)
            return;

        var retained = Enumerable.Range(0, Tabs.Count)
            .Where(index => !string.Equals(Tabs[index], ShellPages.Settings,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (retained.Length == Tabs.Count)
            return;
        if (retained.Length == 0)
        {
            Tabs = [ShellPages.Connections];
            TabLocations = [];
            ActiveTab = 0;
            return;
        }

        var restoredIndex = Array.IndexOf(retained, ActiveTab);
        if (restoredIndex < 0)
            restoredIndex = Array.FindLastIndex(retained, index => index < ActiveTab);
        if (restoredIndex < 0)
            restoredIndex = 0;

        var originalTabs = Tabs;
        var originalLocations = TabLocations ?? [];
        Tabs = retained.Select(index => originalTabs[index]).ToList();
        TabLocations = retained.Select(index =>
            index < originalLocations.Count ? originalLocations[index] : null).ToList();
        ActiveTab = restoredIndex;
    }

    public void Clamp()
    {
        Width = Sanitize(Width, DefaultWidth, CompactMinWidth, 16000);
        Height = Sanitize(Height, DefaultHeight, CompactMinHeight, 16000);
        SidebarWidth = Sanitize(SidebarWidth, DefaultSidebarWidth, MinSidebarWidth, MaxSidebarWidth);
        if (!IsFinite(Left))
            Left = null;
        if (!IsFinite(Top))
            Top = null;
        if (!IsFinite(PhysicalLeft))
            PhysicalLeft = null;
        if (!IsFinite(PhysicalTop))
            PhysicalTop = null;
        if (Tabs is null || Tabs.Count == 0)
            Tabs = [ShellPages.Connections];
        TabLocations ??= [];
        if (TabLocations.Count > ShellSession.MaxTabs)
            TabLocations = TabLocations.Take(ShellSession.MaxTabs).ToList();
        foreach (var location in TabLocations)
            if (location is not null)
                location.Prefix ??= "";
        ActiveTab = Math.Clamp(ActiveTab, 0, Tabs.Count - 1);
    }

    private static bool IsFinite(double? value) =>
        value is double number && double.IsFinite(number);

    private static double Sanitize(double value, double fallback, double min, double max)
    {
        if (!double.IsFinite(value))
            return fallback;
        return Math.Clamp(value, min, max);
    }
}

public sealed class RestorableTabLocation
{
    public Guid ConnectionId { get; set; }
    public string? Bucket { get; set; }
    public string Prefix { get; set; } = "";
}

// These work-area rectangles are ALWAYS physical pixels, never per-monitor DIPs.
public readonly record struct ScreenArea(
    double Left, double Top, double Width, double Height,
    double DpiScale = 1, bool IsPrimary = false);

public readonly record struct RestoredWindow(
    double? Left,
    double? Top,
    double Width,
    double Height,
    bool IsMaximized);

public static class WindowPlacement
{
    public const double MinimumVisible = 48;

    public static RestoredWindow Resolve(WindowLayout layout, IReadOnlyList<ScreenArea> workAreas)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(workAreas);
        layout.Clamp();

        if (layout.Left is not double left || layout.Top is not double top)
            return new RestoredWindow(null, null, layout.Width, layout.Height, false);

        // No monitor list means the caller could not ask the system. Keep the saved place.
        if (workAreas.Count > 0 && !IsOnScreen(left, top, layout.Width, layout.Height, workAreas))
            return new RestoredWindow(null, null, layout.Width, layout.Height, false);

        return new RestoredWindow(left, top, layout.Width, layout.Height, layout.IsMaximized);
    }

    public static bool IsOnScreen(
        double left, double top, double width, double height, IReadOnlyList<ScreenArea> workAreas)
    {
        foreach (var area in workAreas)
        {
            if (area.Width <= 0 || area.Height <= 0)
                continue;
            var overlapWidth = Math.Min(left + width, area.Left + area.Width) - Math.Max(left, area.Left);
            var overlapHeight = Math.Min(top + height, area.Top + area.Height) - Math.Max(top, area.Top);
            if (overlapWidth >= MinimumVisible && overlapHeight >= MinimumVisible)
                return true;
        }

        return false;
    }
}


/// <summary>A restored placement in physical pixels plus its DPI-independent WPF size.</summary>
public readonly record struct NativeRestoredWindow(
    double Left, double Top, double Width, double Height,
    double DpiWidth, double DpiHeight, bool IsMaximized);

public static class NativeWindowPlacement
{
    // Keep a small inset to prevent windows from touching the work-area edges.
    private const double EdgeMargin = 8;

    public static NativeRestoredWindow Resolve(WindowLayout layout, IReadOnlyList<ScreenArea> workAreas)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(workAreas);
        layout.Clamp();

        var screens = workAreas.Where(area =>
            double.IsFinite(area.Left) && double.IsFinite(area.Top)
            && double.IsFinite(area.Width) && double.IsFinite(area.Height)
            && area.Width > 0 && area.Height > 0
            && double.IsFinite(area.DpiScale) && area.DpiScale > 0).ToArray();
        if (screens.Length == 0)
            return new NativeRestoredWindow(0, 0, layout.Width, layout.Height,
                layout.Width, layout.Height, layout.IsMaximized);

        var hasPosition = layout.PhysicalLeft is double && layout.PhysicalTop is double;
        ScreenArea screen;
        if (hasPosition)
        {
            // Favor the monitor containing the normal-window top-left corner.
            // For disconnected displays, choose the nearest remaining screen.
            var x = layout.PhysicalLeft!.Value;
            var y = layout.PhysicalTop!.Value;
            screen = screens.MinBy(area => DistanceSquared(x, y, area));
        }
        else
        {
            // Settings from old versions used monitor-local WPF DIPs and cannot
            // safely be interpreted as physical coordinates on mixed-DPI systems.
            screen = screens.FirstOrDefault(area => area.IsPrimary, screens[0]);
        }

        var scale = screen.DpiScale;
        var availableWidth = Math.Max(1, screen.Width - EdgeMargin * 2) / scale;
        var availableHeight = Math.Max(1, screen.Height - EdgeMargin * 2) / scale;
        var width = Math.Min(layout.Width, availableWidth);
        var height = Math.Min(layout.Height, availableHeight);

        // There is no way to fit the app's hard minimum on an even smaller
        // working area. Constrain to the available space instead of putting
        // the title bar / confirmation controls outside the visible desktop.
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        var pixelWidth = width * scale;
        var pixelHeight = height * scale;
        var left = hasPosition ? layout.PhysicalLeft!.Value
            : screen.Left + (screen.Width - pixelWidth) / 2;
        var top = hasPosition ? layout.PhysicalTop!.Value
            : screen.Top + (screen.Height - pixelHeight) / 2;
        left = Math.Clamp(left, screen.Left + EdgeMargin,
            Math.Max(screen.Left + EdgeMargin, screen.Left + screen.Width - EdgeMargin - pixelWidth));
        top = Math.Clamp(top, screen.Top + EdgeMargin,
            Math.Max(screen.Top + EdgeMargin, screen.Top + screen.Height - EdgeMargin - pixelHeight));

        return new NativeRestoredWindow(left, top, width, height, pixelWidth, pixelHeight, layout.IsMaximized);
    }

    private static double DistanceSquared(double x, double y, ScreenArea area)
    {
        var dx = Math.Max(Math.Max(area.Left - x, x - (area.Left + area.Width)), 0);
        var dy = Math.Max(Math.Max(area.Top - y, y - (area.Top + area.Height)), 0);
        return dx * dx + dy * dy;
    }
}
