using System.Xml.Linq;
using Xunit;

namespace Cavdeck.App.Tests;

public sealed class ExpandedSidebarConnectionLayoutTests
{
    private static readonly XNamespace Wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void Active_connection_marker_is_full_height_without_depending_on_text_measurement()
    {
        var primary = ConnectionPrimary();
        var grid = primary.Elements(Wpf + "Grid").Single();
        var marker = grid.Elements(Wpf + "Border").Single();

        Assert.Equal("3", (string?)marker.Attribute("Width"));
        Assert.Equal("24", (string?)marker.Attribute("Height"));
        Assert.Equal("Center", (string?)marker.Attribute("VerticalAlignment"));
        Assert.Equal("1.5", (string?)marker.Attribute("CornerRadius"));
        Assert.Equal("-8,0,0,0", (string?)marker.Attribute("Margin"));
        Assert.Contains(marker.Descendants(Wpf + "Binding"), binding =>
            ((string?)binding.Attribute("Path"))?.Contains("ActiveConnectionId", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void Provider_label_is_tooltip_only_and_action_button_never_overlaps_text()
    {
        var row = ConnectionRow();
        var primary = row.Elements(Wpf + "Button")
            .Single(element => (string?)element.Attribute(Xaml + "Name") == "ConnectionPrimaryButton");
        var more = row.Elements(Wpf + "Button")
            .Single(element => (string?)element.Attribute("Click") == "SidebarConnectionMore_Click");

        var content = primary.Elements(Wpf + "Grid").Single();
        Assert.Equal("0,0,32,0", (string?)content.Attribute("Margin"));
        var columns = content.Elements(Wpf + "Grid").Single()
            .Element(Wpf + "Grid.ColumnDefinitions")!.Elements(Wpf + "ColumnDefinition")
            .Select(element => (string?)element.Attribute("Width")).ToArray();
        Assert.Equal(new[] { "Auto", "*" }, columns);

        var glyph = content.Descendants()
            .Single(element => element.Name.LocalName == "ProviderGlyph");
        Assert.Equal("{Binding Connection.Provider}", (string?)glyph.Attribute("Provider"));
        var name = content.Descendants(Wpf + "TextBlock").Single();
        Assert.Equal("{Binding Name}", (string?)name.Attribute("Text"));
        Assert.Equal("CharacterEllipsis", (string?)name.Attribute("TextTrimming"));
        Assert.DoesNotContain(content.Descendants(Wpf + "TextBlock"), label =>
            (string?)label.Attribute("Text") == "{Binding ProviderLabel}");

        var tooltip = primary.Element(Wpf + "Button.ToolTip")!.Element(Wpf + "StackPanel")!;
        Assert.Equal(new[] { "{Binding Name}", "{Binding ProviderLabel}" },
            tooltip.Elements(Wpf + "TextBlock").Select(element => (string?)element.Attribute("Text")));
        Assert.Equal("{Binding Name}", (string?)primary.Attribute("AutomationProperties.Name"));
        Assert.Equal("{Binding ProviderLabel}", (string?)primary.Attribute("AutomationProperties.HelpText"));

        Assert.Equal("28", (string?)more.Attribute("Width"));
        Assert.Equal("{Binding ElementName=ConnectionPrimaryButton}", (string?)more.Attribute("Tag"));
        Assert.NotNull(primary.Element(Wpf + "Button.ContextMenu"));
    }

    [Fact]
    public void Collapsed_sidebar_storage_popup_retains_two_line_provider_description()
    {
        var root = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "CavdeckShellWindow.xaml")).Root!;
        var popup = root.Descendants(Wpf + "Popup").Single(element =>
            (string?)element.Attribute(Xaml + "Name") == "StorageMenu");
        Assert.Contains(popup.Descendants(Wpf + "TextBlock"), label =>
            (string?)label.Attribute("Text") == "{Binding ProviderLabel}");
    }

    private static XElement ConnectionRow()
    {
        var root = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "CavdeckShellWindow.xaml")).Root!;
        return root.Descendants(Wpf + "Grid").Single(element =>
            (string?)element.Attribute(Xaml + "Name") == "ConnectionRow");
    }

    private static XElement ConnectionPrimary() => ConnectionRow().Elements(Wpf + "Button")
        .Single(element => (string?)element.Attribute(Xaml + "Name") == "ConnectionPrimaryButton");
}
