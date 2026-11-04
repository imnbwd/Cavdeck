using System.Xml.Linq;
using Xunit;

namespace Cavdeck.App.Tests;

public sealed class ExplorerKeyboardFocusTests
{
    private static readonly XNamespace Wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static XElement ObjectGrid(XDocument xml) => xml.Descendants(Wpf + "DataGrid")
        .Single(grid => (string?)grid.Attribute(Xaml + "Name") == "ObjectsGrid");

    [Fact]
    public void File_list_is_one_tab_stop_but_retains_native_row_interactions()
    {
        var xml = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "CavdeckExplorerView.xaml"));
        var objects = ObjectGrid(xml);
        var controls = xml.Root!.GetNamespaceOfPrefix("controls")!;
        var buckets = xml.Descendants(Wpf + "DataGrid").Single(grid =>
            (string?)grid.Attribute(Xaml + "Name") == "BucketsGrid");

        Assert.Equal("Once", (string?)objects.Attribute("KeyboardNavigation.TabNavigation"));
        Assert.Equal("True", (string?)objects.Attribute(controls + "FocusCue.RowFocusScope"));
        Assert.Equal("ObjectsGrid_PreviewKeyDown", (string?)objects.Attribute("PreviewKeyDown"));
        Assert.Equal("Objects_SelectionChanged", (string?)objects.Attribute("SelectionChanged"));
        Assert.Equal("Objects_Sorting", (string?)objects.Attribute("Sorting"));
        Assert.Equal("Objects_MouseDoubleClick", (string?)objects.Attribute("MouseDoubleClick"));

        // The S3 bucket table has button cells and keeps its own Tab handling.
        Assert.Null(buckets.Attribute("KeyboardNavigation.TabNavigation"));
        Assert.Null(buckets.Attribute(controls + "FocusCue.RowFocusScope"));
        Assert.Null(buckets.Attribute("PreviewKeyDown"));
        Assert.Null(buckets.Element(Wpf + "DataGrid.CellStyle"));
    }

    [Fact]
    public void Object_focus_cue_belongs_to_row_instead_of_name_cell()
    {
        var xml = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "CavdeckExplorerView.xaml"));
        var objects = ObjectGrid(xml);

        // FocusCue targets the row; the shared DataGridCell focus underline is never activated.
        Assert.Null(objects.Element(Wpf + "DataGrid.CellStyle"));
        var rowStyle = Assert.Single(objects.Element(Wpf + "DataGrid.RowStyle")!
            .Elements(Wpf + "Style"));
        var focusedRow = Assert.Single(rowStyle.Descendants(Wpf + "DataTrigger")
            , trigger => ((string?)trigger.Attribute("Binding"))?
                .Contains("controls:FocusCue.Visible") == true);
        Assert.Equal("True", (string?)focusedRow.Attribute("Value"));
        Assert.Contains(focusedRow.Elements(Wpf + "Setter"), setter =>
            (string?)setter.Attribute("Property") == "BorderBrush" &&
            (string?)setter.Attribute("Value") == "{DynamicResource FocusBorderBrush}");

        // Drag-over highlighting still has its own row trigger.
        Assert.Contains(rowStyle.Descendants(Wpf + "DataTrigger"), trigger =>
            (string?)trigger.Attribute("Binding") == "{Binding IsDropTarget}");
    }
}
