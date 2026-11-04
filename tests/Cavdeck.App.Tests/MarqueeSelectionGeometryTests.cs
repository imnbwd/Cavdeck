using System.Windows;
using System.Xml.Linq;
using Cavdeck.App.Features.Explorer;
using Xunit;

namespace Cavdeck.App.Tests;

public sealed class MarqueeSelectionGeometryTests
{
    private static readonly XNamespace Wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void Both_file_views_use_a_non_interactive_theme_aware_marquee_overlay()
    {
        var markup = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "CavdeckExplorerView.xaml"));
        var controls = markup.Descendants().ToArray();
        foreach (var name in new[] { "ObjectsGrid", "ObjectsIcons" })
        {
            var view = Assert.Single(controls, node => (string?)node.Attribute(Xaml + "Name") == name);
            Assert.Equal("Marquee_PreviewMouseLeftButtonDown", (string?)view.Attribute("PreviewMouseLeftButtonDown"));
            Assert.Equal("Marquee_PreviewMouseLeftButtonUp", (string?)view.Attribute("PreviewMouseLeftButtonUp"));
            Assert.Equal("Marquee_MouseMove", (string?)view.Attribute("PreviewMouseMove"));
            Assert.Equal("Marquee_LostMouseCapture", (string?)view.Attribute("LostMouseCapture"));
        }

        var overlay = Assert.Single(controls, node =>
            (string?)node.Attribute(Xaml + "Name") == "MarqueeLayer");
        Assert.Equal("False", (string?)overlay.Attribute("IsHitTestVisible"));
        Assert.Equal("True", (string?)overlay.Attribute("ClipToBounds"));

        var rectangle = Assert.Single(overlay.Elements(Wpf + "Rectangle"));
        Assert.Contains("DynamicResource AccentBrush", (string?)rectangle.Attribute("Stroke"));
        Assert.Contains("DynamicResource MarqueeSelectionFillBrush", (string?)rectangle.Attribute("Fill"));
        Assert.Null(rectangle.Attribute("Opacity")); // The border stays distinct from the translucent fill.
        Assert.Equal("Collapsed", (string?)rectangle.Attribute("Visibility"));
    }

    [Theory]
    [InlineData("Colors.Light.xaml", 0.15)]
    [InlineData("Colors.Dark.xaml", 0.20)]
    public void Marquee_fill_is_theme_matched_and_translucent_without_fading_its_border(
        string themeFile, double expectedOpacity)
    {
        var theme = XDocument.Load(Path.Combine(AppContext.BaseDirectory, themeFile));
        var brushes = theme.Descendants(Wpf + "SolidColorBrush");
        string ReadColor(string key) => (string?)brushes.Single(node =>
            (string?)node.Attribute(Xaml + "Key") == key).Attribute("Color")
            ?? throw new InvalidOperationException($"Missing color for {key}");

        var accent = ReadColor("AccentBrush");
        var fill = ReadColor("MarqueeSelectionFillBrush");
        Assert.Equal(7, accent.Length); // #RRGGBB
        Assert.Equal(9, fill.Length);    // #AARRGGBB: independent alpha channel
        Assert.Equal(accent[1..], fill[3..], ignoreCase: true);
        var alpha = Convert.ToByte(fill.Substring(1, 2), 16) / 255d;
        Assert.InRange(alpha, expectedOpacity - 0.01, expectedOpacity + 0.01);
    }

    [Fact]
    public void Dragging_in_any_direction_has_the_same_selection_rectangle()
    {
        var forward = MarqueeSelectionGeometry.Between(new Point(10, 20), new Point(90, 70));
        var reverse = MarqueeSelectionGeometry.Between(new Point(90, 70), new Point(10, 20));
        Assert.Equal(forward, reverse);
    }

    [Fact]
    public void A_list_marquee_selects_only_rows_that_intersect_its_vertical_span()
    {
        var area = new Rect(20, 45, 140, 70);
        Assert.Equal(new[] { 1, 2, 3 }, MarqueeSelectionGeometry.HitRows(area, 5000, 36, 400));
        Assert.Empty(MarqueeSelectionGeometry.HitRows(new Rect(450, 45, 10, 70), 20, 36, 400));
    }

    [Fact]
    public void Icon_marquee_uses_two_dimensional_geometry_instead_of_a_contiguous_range()
    {
        // Four columns of 100x80 tiles, separated by an 8px gap.
        var area = new Rect(80, 40, 80, 110);
        Assert.Equal(new[] { 0, 1, 4, 5 },
            MarqueeSelectionGeometry.HitTiles(area, 30, 4, 100, 80));
    }

    [Fact]
    public void Tile_hit_testing_does_not_select_gaps_or_indices_past_last_item()
    {
        var gap = new Rect(101, 1, 6, 10);
        Assert.Empty(MarqueeSelectionGeometry.HitTiles(gap, 20, 4, 100, 80));
        Assert.Equal(new[] { 9 },
            MarqueeSelectionGeometry.HitTiles(new Rect(110, 180, 30, 35), 10, 4, 100, 80));
    }

    [Fact]
    public void Selection_can_extend_into_offscreen_rows_without_realizing_item_containers()
    {
        var indices = MarqueeSelectionGeometry.HitTiles(
            new Rect(0, 0, 400, 2500), 10000, 3, 100, 80);
        Assert.Contains(0, indices);
        Assert.Contains(80, indices);
        Assert.DoesNotContain(90, indices);
        Assert.Equal(indices.Count, indices.Distinct().Count());
    }
}
