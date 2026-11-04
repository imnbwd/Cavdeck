using System.ComponentModel;
using System.Xml.Linq;
using Cavdeck.App.Features.Explorer;
using Cavdeck.Domain.Storage;
using Xunit;

namespace Cavdeck.App.Tests;

public sealed class ExplorerSortIndicatorTests
{
    private static readonly XNamespace Wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Theory]
    [InlineData("Name", ListingSort.Name, false, ListSortDirection.Ascending)]
    [InlineData("Name", ListingSort.Name, true, ListSortDirection.Descending)]
    [InlineData("SizeBytes", ListingSort.Size, false, ListSortDirection.Ascending)]
    [InlineData("Modified", ListingSort.Modified, true, ListSortDirection.Descending)]
    public void Only_active_column_shows_its_actual_sort_direction(
        string member, ListingSort active, bool descending, ListSortDirection expected)
    {
        Assert.Equal(expected, ExplorerSortDirection.DirectionFor(member, active, descending));
        foreach (var other in new[] { "Name", "SizeBytes", "Modified" }.Where(s => s != member))
            Assert.Null(ExplorerSortDirection.DirectionFor(other, active, descending));
    }

    [Fact]
    public void Default_name_sort_is_ascending_and_first_click_reverses_it()
    {
        Assert.Equal(ListSortDirection.Ascending,
            ExplorerSortDirection.DirectionFor("Name", ListingSort.Name, descending: false));
        Assert.True(ExplorerSortDirection.NextDescending("Name", ListSortDirection.Ascending));
        Assert.Null(ExplorerSortDirection.DirectionFor("Kind", ListingSort.Name, descending: false));
    }

    [Fact]
    public void Non_sortable_type_column_never_displays_an_arrow()
    {
        Assert.Null(ExplorerSortDirection.SortFor("Kind"));
        Assert.Null(ExplorerSortDirection.DirectionFor("Kind", ListingSort.Name, false));
    }

    [Theory]
    [InlineData("Name", false)]
    [InlineData("SizeBytes", true)]
    [InlineData("Modified", true)]
    public void First_click_preserves_existing_column_defaults(string member, bool descending)
    {
        Assert.Equal(descending, ExplorerSortDirection.NextDescending(member, current: null));
        Assert.False(ExplorerSortDirection.NextDescending(member, ListSortDirection.Descending));
        Assert.True(ExplorerSortDirection.NextDescending(member, ListSortDirection.Ascending));
    }

    [Fact]
    public void Object_header_has_its_own_sort_glyph_and_preserves_column_resize_grippers()
    {
        var xml = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "CavdeckExplorerView.xaml"));
        var objects = xml.Descendants(Wpf + "DataGrid").Single(grid =>
            (string?)grid.Attribute(Xaml + "Name") == "ObjectsGrid");
        var buckets = xml.Descendants(Wpf + "DataGrid").Single(grid =>
            (string?)grid.Attribute(Xaml + "Name") == "BucketsGrid");

        Assert.Equal("{StaticResource SortableListColumnHeader}",
            (string?)objects.Attribute("ColumnHeaderStyle"));
        Assert.Null(buckets.Attribute("ColumnHeaderStyle"));

        var type = objects.Element(Wpf + "DataGrid.Columns")!
            .Elements().Single(column =>
                (string?)column.Attribute("Binding") == "{Binding Kind}");
        Assert.Equal("False", (string?)type.Attribute("CanUserSort"));

        var styles = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "CavdeckDataGrid.xaml"));
        var header = styles.Descendants(Wpf + "Style").Single(style =>
            (string?)style.Attribute(Xaml + "Key") == "SortableListColumnHeader");
        var thumbs = header.Descendants(Wpf + "Thumb")
            .Select(thumb => (string?)thumb.Attribute(Xaml + "Name")).ToArray();
        Assert.Contains("PART_LeftHeaderGripper", thumbs);
        Assert.Contains("PART_RightHeaderGripper", thumbs);

        var template = Assert.Single(header.Descendants(Wpf + "ControlTemplate"));
        var path = Assert.Single(template.Descendants(Wpf + "Path"));
        Assert.Equal("SortGlyph", (string?)path.Attribute(Xaml + "Name"));
        Assert.Equal("1", (string?)path.Attribute("Grid.Column"));
        Assert.Equal("{TemplateBinding Foreground}", (string?)path.Attribute("Stroke"));
        var headerGrid = Assert.Single(template.Descendants(Wpf + "Grid"), grid =>
            grid.Element(Wpf + "Grid.ColumnDefinitions") is not null);
        Assert.Equal(new[] { "*", "14" }, headerGrid.Element(Wpf + "Grid.ColumnDefinitions")!
            .Elements(Wpf + "ColumnDefinition")
            .Select(column => (string?)column.Attribute("Width")));
        var values = template.Descendants(Wpf + "Trigger")
            .Where(trigger => trigger.Descendants(Wpf + "Setter").Any(setter =>
                (string?)setter.Attribute("TargetName") == "SortGlyph"))
            .Select(trigger => (string?)trigger.Attribute("Value")).ToArray();
        Assert.Contains("Ascending", values);
        Assert.Contains("Descending", values);
        // Sorting changes only the glyph and accessibility status, never header text styling.
        var sortStyleTriggers = header.Element(Wpf + "Style.Triggers")!
            .Elements(Wpf + "Trigger").Where(trigger =>
                (string?)trigger.Attribute("Property") == "SortDirection");
        Assert.Equal(2, sortStyleTriggers.Count());
        Assert.All(sortStyleTriggers, trigger =>
            Assert.DoesNotContain(trigger.Elements(Wpf + "Setter"), setter =>
                (string?)setter.Attribute("Property") == "Foreground"));
        Assert.Contains(header.Descendants(Wpf + "Setter"), setter =>
            (string?)setter.Attribute("Property") == "AutomationProperties.ItemStatus" &&
            ((string?)setter.Attribute("Value"))?.Contains("Explorer_SortedAscending") == true);
        Assert.Contains(header.Descendants(Wpf + "Setter"), setter =>
            (string?)setter.Attribute("Property") == "AutomationProperties.ItemStatus" &&
            ((string?)setter.Attribute("Value"))?.Contains("Explorer_SortedDescending") == true);
    }

    [Fact]
    public void Places_reuse_the_same_sort_header_with_their_own_sort_members()
    {
        var styles = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "CavdeckDataGrid.xaml"));
        Assert.Contains(styles.Descendants(Wpf + "Style"), style =>
            (string?)style.Attribute(Xaml + "Key") == "SortableListColumnHeader");

        var places = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "CavdeckPlacesView.xaml"));
        var grid = Assert.Single(places.Descendants(Wpf + "DataGrid"));
        Assert.Equal("{StaticResource SortableListColumnHeader}",
            (string?)grid.Attribute("ColumnHeaderStyle"));
        Assert.Equal("PlacesGrid_Sorting", (string?)grid.Attribute("Sorting"));
        var columns = grid.Element(Wpf + "DataGrid.Columns")!.Elements().ToArray();
        Assert.Equal(new[] { "Title", "ConnectionName", "Location", "VisitedAt" },
            columns.Select(column => (string?)column.Attribute("SortMemberPath")));
    }

    [Fact]
    public void Resize_handles_are_invisible_hit_targets_with_a_single_subtle_divider()
    {
        var xml = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "CavdeckDataGrid.xaml"));
        var thumbStyle = xml.Descendants(Wpf + "Style").Single(style =>
            (string?)style.Attribute(Xaml + "Key") == "SortableListHeaderResizeThumb");
        Assert.Equal("{x:Type Thumb}", (string?)thumbStyle.Attribute("TargetType"));

        var setters = thumbStyle.Elements(Wpf + "Setter").ToArray();
        Assert.Contains(setters, setter =>
            (string?)setter.Attribute("Property") == "Width" &&
            (string?)setter.Attribute("Value") == "8");
        Assert.Contains(setters, setter =>
            (string?)setter.Attribute("Property") == "Cursor" &&
            (string?)setter.Attribute("Value") == "SizeWE");
        Assert.Contains(setters, setter =>
            (string?)setter.Attribute("Property") == "Focusable" &&
            (string?)setter.Attribute("Value") == "False");

        var thumbTemplate = Assert.Single(thumbStyle.Descendants(Wpf + "ControlTemplate"));
        var hitSurface = Assert.Single(thumbTemplate.Elements(Wpf + "Border"));
        Assert.Equal("Transparent", (string?)hitSurface.Attribute("Background"));
        Assert.Empty(thumbTemplate.Descendants(Wpf + "Path"));

        var headerStyle = xml.Descendants(Wpf + "Style").Single(style =>
            (string?)style.Attribute(Xaml + "Key") == "SortableListColumnHeader");
        var grippers = headerStyle.Descendants(Wpf + "Thumb").ToArray();
        Assert.Equal(2, grippers.Length);
        Assert.All(grippers, thumb => Assert.Equal("{StaticResource SortableListHeaderResizeThumb}",
            (string?)thumb.Attribute("Style")));

        var divider = Assert.Single(headerStyle.Descendants(Wpf + "Border"), border =>
            (string?)border.Attribute(Xaml + "Name") == "HeaderDivider");
        Assert.Equal("1", (string?)divider.Attribute("Width"));
        Assert.Equal("0,8,0,8", (string?)divider.Attribute("Margin"));
        Assert.Equal("{DynamicResource BorderBrush}", (string?)divider.Attribute("Background"));
        Assert.Equal("False", (string?)divider.Attribute("IsHitTestVisible"));

        var glyph = Assert.Single(headerStyle.Descendants(Wpf + "ControlTemplate").Descendants(Wpf + "Path"));
        Assert.Equal("9", (string?)glyph.Attribute("Width"));
        Assert.Equal("9", (string?)glyph.Attribute("Height"));
        Assert.Equal("1", (string?)glyph.Attribute("Grid.Column"));
        Assert.Equal("{TemplateBinding Foreground}", (string?)glyph.Attribute("Stroke"));
    }
}
