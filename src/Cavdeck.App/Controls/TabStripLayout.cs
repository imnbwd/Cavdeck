namespace Cavdeck.App.Controls;

// Keep the tab layout readable while reserving a non-interactive title-bar drag region.
internal readonly record struct TabStripMetrics(double TabWidth, double ViewportWidth, bool ShowOverflow);

internal static class TabStripLayout
{
    internal const double MinTabWidth = 108;
    internal const double MaxTabWidth = 200;
    internal const double TabGap = 4;
    internal const double DragAreaWidth = 48;
    internal const double OverflowButtonWidth = 36;

    internal static double WidthWhileClosing(TabStripMetrics metrics, double? widthAtPointerDown)
    {
        if (widthAtPointerDown is not double width || !double.IsFinite(width))
            return metrics.TabWidth;
        return Math.Clamp(width, MinTabWidth, metrics.TabWidth);
    }

    internal static TabStripMetrics Calculate(double hostWidth, double addButtonWidth, int tabCount)
    {
        if (tabCount < 0)
            throw new ArgumentOutOfRangeException(nameof(tabCount));

        var width = double.IsFinite(hostWidth) ? Math.Max(0, hostWidth) : 0;
        var addWidth = double.IsFinite(addButtonWidth) ? Math.Max(0, addButtonWidth) : 0;
        var available = Math.Max(0, width - addWidth - DragAreaWidth);
        var showOverflow = tabCount > 0 && tabCount * (MinTabWidth + TabGap) > available + 0.5;
        var viewport = Math.Max(0, available - (showOverflow ? OverflowButtonWidth : 0));
        var tabWidth = tabCount == 0 ? MaxTabWidth
            : Math.Clamp(viewport / tabCount - TabGap, MinTabWidth, MaxTabWidth);
        return new TabStripMetrics(tabWidth, viewport, showOverflow);
    }
}
