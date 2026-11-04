using Cavdeck.App.Controls;
using Xunit;

namespace Cavdeck.App.Tests;

public sealed class TabStripLayoutTests
{
    [Fact]
    public void Few_tabs_keep_a_comfortable_readable_width()
    {
        var result = TabStripLayout.Calculate(900, 36, 3);
        Assert.Equal(TabStripLayout.MaxTabWidth, result.TabWidth);
        Assert.False(result.ShowOverflow);
        Assert.Equal(900 - 36 - TabStripLayout.DragAreaWidth, result.ViewportWidth);
    }

    [Fact]
    public void Tabs_shrink_uniformly_before_they_overflow()
    {
        var result = TabStripLayout.Calculate(900, 36, 6);
        Assert.InRange(result.TabWidth, TabStripLayout.MinTabWidth, TabStripLayout.MaxTabWidth - 1);
        Assert.False(result.ShowOverflow);
        Assert.True(6 * (result.TabWidth + TabStripLayout.TabGap) <= result.ViewportWidth);
    }

    [Fact]
    public void Crowded_tabs_scroll_at_readable_minimum_width()
    {
        var result = TabStripLayout.Calculate(900, 36, 12);
        Assert.Equal(TabStripLayout.MinTabWidth, result.TabWidth);
        Assert.True(result.ShowOverflow);
        Assert.Equal(900 - 36 - TabStripLayout.DragAreaWidth - TabStripLayout.OverflowButtonWidth,
            result.ViewportWidth);
    }

    [Fact]
    public void Resizing_across_the_threshold_keeps_geometry_consistent()
    {
        var fits = TabStripLayout.Calculate(532, 36, 4);
        var overflows = TabStripLayout.Calculate(530, 36, 4);
        Assert.False(fits.ShowOverflow);
        Assert.True(overflows.ShowOverflow);
        Assert.Equal(TabStripLayout.MinTabWidth, fits.TabWidth);
        Assert.Equal(TabStripLayout.MinTabWidth, overflows.TabWidth);
    }

    [Fact]
    public void Pointer_close_holds_width_until_mouse_exits()
    {
        var six = TabStripLayout.Calculate(900, 36, 6);
        var five = TabStripLayout.Calculate(900, 36, 5);
        Assert.True(five.TabWidth > six.TabWidth);
        Assert.Equal(six.TabWidth, TabStripLayout.WidthWhileClosing(five, six.TabWidth));
        Assert.Equal(five.TabWidth, TabStripLayout.WidthWhileClosing(five, null));
        Assert.Equal(five.TabWidth, TabStripLayout.WidthWhileClosing(five, double.NaN));
    }

    [Fact]
    public void Empty_and_invalid_inputs_are_safe()
    {
        var empty = TabStripLayout.Calculate(double.NaN, double.PositiveInfinity, 0);
        Assert.Equal(TabStripLayout.MaxTabWidth, empty.TabWidth);
        Assert.Equal(0, empty.ViewportWidth);
        Assert.False(empty.ShowOverflow);

        var tiny = TabStripLayout.Calculate(0, 0, 1);
        Assert.Equal(TabStripLayout.MinTabWidth, tiny.TabWidth);
        Assert.True(tiny.ShowOverflow);
        Assert.Equal(0, tiny.ViewportWidth);
        Assert.Throws<ArgumentOutOfRangeException>(() => TabStripLayout.Calculate(900, 36, -1));
    }
}
