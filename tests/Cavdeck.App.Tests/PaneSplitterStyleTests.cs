using System.Xml.Linq;
using Xunit;

namespace Cavdeck.App.Tests;

public sealed class PaneSplitterStyleTests
{
    private static readonly XNamespace Wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static XDocument View(string name) =>
        XDocument.Load(Path.Combine(AppContext.BaseDirectory, name));

    private static XElement NamedElement(XDocument document, string element, string name) =>
        document.Descendants(Wpf + element).Single(e => (string?)e.Attribute(Xaml + "Name") == name);

    [Fact]
    public void Both_shell_and_explorer_use_the_same_shared_pane_splitter()
    {
        var shell = View("CavdeckShellWindow.xaml");
        var explorer = View("CavdeckExplorerView.xaml");

        var sidebar = NamedElement(shell, "GridSplitter", "SidebarSplitter");
        var details = NamedElement(explorer, "GridSplitter", "SideSplitter");

        Assert.Equal("{StaticResource PaneSplitter}", (string?)sidebar.Attribute("Style"));
        Assert.Equal("{StaticResource PaneSplitter}", (string?)details.Attribute("Style"));
        // Only the sidebar has a differently colored leading pane. Explorer
        // uses the shared SurfaceBrush default on both sides.
        Assert.Equal("{DynamicResource SidebarBackgroundBrush}", (string?)sidebar.Attribute("Background"));
        Assert.Null(details.Attribute("Background"));
        Assert.DoesNotContain(shell.Descendants(Wpf + "Style"),
            s => (string?)s.Attribute(Xaml + "Key") == "PaneSplitter");
        Assert.DoesNotContain(explorer.Descendants(Wpf + "Style"),
            s => (string?)s.Attribute(Xaml + "Key") == "SideSplitter");

        // The splitter owns the single separator rule. Do not add a second line on the pane.
        var pane = NamedElement(explorer, "Border", "SidePane");
        Assert.Equal("{DynamicResource SurfaceBrush}", (string?)pane.Attribute("Background"));
        Assert.Null(pane.Attribute("BorderThickness"));
        Assert.Null(pane.Attribute("BorderBrush"));

        // Preserve the drag-complete persistence and visibility of the details pane.
        Assert.Equal("SideSplitter_DragCompleted", (string?)details.Attribute("DragCompleted"));
        Assert.Equal("Collapsed", (string?)details.Attribute("Visibility"));
        Assert.Contains("Explorer.ShowSide", (string?)pane.Attribute("Visibility"));
        var sideColumn = NamedElement(explorer, "ColumnDefinition", "SideColumn");
        Assert.Equal("0", (string?)sideColumn.Attribute("Width"));
    }

    [Fact]
    public void Shared_splitter_has_one_rule_in_the_narrow_resizable_gutter()
    {
        var resources = View("CavdeckSurfaces.xaml");
        var style = resources.Descendants(Wpf + "Style").Single(s =>
            (string?)s.Attribute(Xaml + "Key") == "PaneSplitter");
        Assert.Equal("GridSplitter", (string?)style.Attribute("TargetType"));

        var setters = style.Elements(Wpf + "Setter").ToDictionary(
            s => (string)s.Attribute("Property")!,
            s => s.Attribute("Value")?.Value ?? string.Empty);
        Assert.Equal("4", setters["Width"]);
        Assert.Equal("{DynamicResource SurfaceBrush}", setters["Background"]);
        Assert.Equal("SizeWE", setters["Cursor"]);
        Assert.Equal("PreviousAndNext", setters["ResizeBehavior"]);
        Assert.Equal("Columns", setters["ResizeDirection"]);
        Assert.Equal("False", setters["Focusable"]);
        Assert.Equal("False", setters["IsTabStop"]);

        var template = style.Descendants(Wpf + "ControlTemplate").Single();
        var hitTarget = template.Elements(Wpf + "Grid").Single();
        Assert.Equal("{DynamicResource SurfaceBrush}", (string?)hitTarget.Attribute("Background"));
        Assert.Equal("True", (string?)hitTarget.Attribute("SnapsToDevicePixels"));
        Assert.Equal(new[] { "*", "2" }, hitTarget.Element(Wpf + "Grid.ColumnDefinitions")!
            .Elements(Wpf + "ColumnDefinition")
            .Select(column => (string?)column.Attribute("Width")).ToArray());

        var leadingHalf = hitTarget.Elements(Wpf + "Border").Single(b =>
            (string?)b.Attribute("Grid.Column") == "0");
        Assert.Equal("{TemplateBinding Background}", (string?)leadingHalf.Attribute("Background"));

        var line = hitTarget.Elements(Wpf + "Border").Single(b =>
            (string?)b.Attribute(Xaml + "Name") == "Line");
        Assert.Equal("1", (string?)line.Attribute("Grid.Column"));
        Assert.Equal("1", (string?)line.Attribute("Width"));
        Assert.Equal("Center", (string?)line.Attribute("HorizontalAlignment"));
        Assert.Equal("{DynamicResource BorderBrush}", (string?)line.Attribute("Background"));
        Assert.Equal("True", (string?)line.Attribute("SnapsToDevicePixels"));
    }

    [Theory]
    [InlineData("Colors.Light.xaml")]
    [InlineData("Colors.Dark.xaml")]
    public void Splitter_halves_and_interaction_brushes_resolve_in_both_themes(string themeFile)
    {
        var theme = View(themeFile);
        var brushes = theme.Descendants(Wpf + "SolidColorBrush").ToDictionary(
            b => (string)b.Attribute(Xaml + "Key")!,
            b => (string)b.Attribute("Color")!);

        foreach (var key in new[] { "SidebarBackgroundBrush", "SurfaceBrush",
                     "BorderBrush", "BorderStrongBrush", "AccentBrush" })
            Assert.False(string.IsNullOrWhiteSpace(brushes[key]));

        // Both themes have distinct sidebar and content backgrounds, so the
        // gutter needs per-pane fills rather than a single full-width stripe.
        Assert.NotEqual(brushes["SidebarBackgroundBrush"], brushes["SurfaceBrush"]);

        var resources = View("CavdeckSurfaces.xaml");
        var style = resources.Descendants(Wpf + "Style").Single(s =>
            (string?)s.Attribute(Xaml + "Key") == "PaneSplitter");
        var template = style.Descendants(Wpf + "ControlTemplate").Single();
        var grid = template.Elements(Wpf + "Grid").Single();
        Assert.Equal("{DynamicResource SurfaceBrush}", (string?)grid.Attribute("Background"));
        Assert.Equal("{TemplateBinding Background}",
            (string?)grid.Elements(Wpf + "Border").Single(b =>
                (string?)b.Attribute("Grid.Column") == "0").Attribute("Background"));
    }

    [Fact]
    public void Hover_and_drag_both_highlight_the_single_rule()
    {
        var resources = View("CavdeckSurfaces.xaml");
        var style = resources.Descendants(Wpf + "Style").Single(s =>
            (string?)s.Attribute(Xaml + "Key") == "PaneSplitter");
        var template = style.Descendants(Wpf + "ControlTemplate").Single();
        var triggers = template.Descendants(Wpf + "Trigger").ToArray();
        Assert.Equal(2, triggers.Length);

        foreach (var (state, brush) in new[]
        {
            ("IsMouseOver", "{DynamicResource BorderStrongBrush}"),
            ("IsDragging", "{DynamicResource AccentBrush}")
        })
        {
            var trigger = triggers.Single(t => (string?)t.Attribute("Property") == state);
            Assert.Equal("True", (string?)trigger.Attribute("Value"));
            var setters = trigger.Elements(Wpf + "Setter").ToArray();
            Assert.Contains(setters, s =>
                (string?)s.Attribute("TargetName") == "Line" &&
                (string?)s.Attribute("Property") == "Background" &&
                (string?)s.Attribute("Value") == brush);
            Assert.Contains(setters, s =>
                (string?)s.Attribute("TargetName") == "Line" &&
                (string?)s.Attribute("Property") == "Width" &&
                (string?)s.Attribute("Value") == "2");
        }
    }
}
