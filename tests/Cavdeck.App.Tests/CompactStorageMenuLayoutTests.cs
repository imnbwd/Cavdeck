using System.Xml.Linq;
using Xunit;

namespace Cavdeck.App.Tests;

public sealed class CompactStorageMenuLayoutTests
{
    private static readonly XNamespace Wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static XDocument Shell() =>
        XDocument.Load(Path.Combine(AppContext.BaseDirectory, "CavdeckShellWindow.xaml"));

    [Fact]
    public void Compact_rows_have_two_legible_lines_inside_a_predictable_popup_width()
    {
        var shell = Shell();
        var popup = shell.Descendants(Wpf + "Popup")
            .Single(node => (string?)node.Attribute(Xaml + "Name") == "StorageMenu");
        var border = popup.Element(Wpf + "Border")!;
        Assert.Equal("288", (string?)border.Attribute("Width"));
        Assert.Equal("360", (string?)border.Attribute("MaxHeight"));
        Assert.Equal("6", (string?)border.Attribute("Padding"));
        Assert.Equal("4,4,12,14", (string?)border.Attribute("Margin"));
        Assert.Equal("True", (string?)border.Attribute("UseLayoutRounding"));
        Assert.Equal("{DynamicResource RadiusMedium}", (string?)border.Attribute("CornerRadius"));
        var shadow = border.Element(Wpf + "Border.Effect")?.Element(Wpf + "DropShadowEffect");
        Assert.NotNull(shadow);
        Assert.Equal("16", (string?)shadow.Attribute("BlurRadius"));
        Assert.Equal("0.16", (string?)shadow.Attribute("Opacity"));
        Assert.Equal("None", (string?)popup.Attribute("PopupAnimation"));

        var style = shell.Descendants(Wpf + "Style").Single(node =>
            (string?)node.Attribute(Xaml + "Key") == "StorageMenuConnectionButton");
        Assert.Contains(style.Elements(Wpf + "Setter"), setter =>
            (string?)setter.Attribute("Property") == "Height"
            && (string?)setter.Attribute("Value") == "52");
        Assert.Contains(style.Elements(Wpf + "Setter"), setter =>
            (string?)setter.Attribute("Property") == "HorizontalContentAlignment"
            && (string?)setter.Attribute("Value") == "Stretch");

        var button = ConnectionButton(popup);
        Assert.Equal("{StaticResource StorageMenuConnectionButton}", (string?)button.Attribute("Style"));
        var grid = button.Element(Wpf + "Grid")!;
        var columns = grid.Element(Wpf + "Grid.ColumnDefinitions")!
            .Elements(Wpf + "ColumnDefinition").Select(x => (string?)x.Attribute("Width")).ToArray();
        Assert.Equal(new[] { "2", "12", "16", "10", "*" }, columns);

        var glyph = grid.Elements().Single(element => element.Name.LocalName == "ProviderGlyph");
        Assert.Equal("2", (string?)glyph.Attribute("Grid.Column"));
        Assert.Equal("{Binding Connection.Provider}", (string?)glyph.Attribute("Provider"));

        var textGrid = grid.Elements(Wpf + "Grid").Single();
        var labels = textGrid.Elements(Wpf + "TextBlock").ToArray();
        Assert.Equal(2, labels.Length);
        Assert.Equal("{Binding Name}", (string?)labels[0].Attribute("Text"));
        Assert.Equal("{Binding ProviderLabel}", (string?)labels[1].Attribute("Text"));
        Assert.All(labels, label =>
            Assert.Equal("CharacterEllipsis", (string?)label.Attribute("TextTrimming")));
        Assert.Equal("SemiBold", (string?)labels[0].Attribute("FontWeight"));
        Assert.Equal("13", (string?)labels[0].Attribute("FontSize"));
        Assert.Equal("12", (string?)labels[1].Attribute("FontSize"));
    }

    [Fact]
    public void Current_connection_mark_and_original_actions_remain_intact()
    {
        var popup = Shell().Descendants(Wpf + "Popup")
            .Single(node => (string?)node.Attribute(Xaml + "Name") == "StorageMenu");
        var button = ConnectionButton(popup);
        Assert.Equal("{Binding Id}", (string?)button.Attribute("Tag"));
        Assert.Equal("StorageConnection_PreviewMouseDown", (string?)button.Attribute("PreviewMouseDown"));
        Assert.Equal("StorageMenuRow_PreviewMouseRightButtonDown",
            (string?)button.Attribute("PreviewMouseRightButtonDown"));
        Assert.Contains("OpenConnectionCommand", (string?)button.Attribute("Command"));

        var marker = button.Element(Wpf + "Grid")!.Elements(Wpf + "Border").Single();
        Assert.Equal("2", (string?)marker.Attribute("Width"));
        Assert.Equal("28", (string?)marker.Attribute("Height"));
        Assert.Contains(marker.Descendants(Wpf + "Binding"),
            binding => ((string?)binding.Attribute("Path"))?.Contains("ActiveConnectionId") == true);
        var menu = button.Element(Wpf + "Button.ContextMenu")!.Element(Wpf + "ContextMenu")!;
        Assert.Equal(5, menu.Elements(Wpf + "MenuItem").Count());
    }

    [Fact]
    public void Full_names_are_accessible_and_add_action_is_visually_separated()
    {
        var popup = Shell().Descendants(Wpf + "Popup")
            .Single(node => (string?)node.Attribute(Xaml + "Name") == "StorageMenu");
        var button = ConnectionButton(popup);
        Assert.Equal("{Binding Name}", (string?)button.Attribute("AutomationProperties.Name"));
        Assert.Equal("{Binding ProviderLabel}", (string?)button.Attribute("AutomationProperties.HelpText"));

        var tooltip = button.Element(Wpf + "Button.ToolTip")!
            .Element(Wpf + "StackPanel")!;
        Assert.Equal(2, tooltip.Elements(Wpf + "TextBlock").Count());

        var add = popup.Descendants(Wpf + "Button")
            .Single(node => (string?)node.Attribute(Xaml + "Name") == "StorageMenuAddButton");
        Assert.Equal("{StaticResource GhostButton}", (string?)add.Attribute("Style"));
        Assert.Equal("Left", (string?)add.Attribute("HorizontalContentAlignment"));
        Assert.Equal("12,0", (string?)add.Attribute("Padding"));
        var content = add.Element(Wpf + "StackPanel")!;
        Assert.Equal("10,0,0,0", (string?)content.Elements(Wpf + "Path").Single().Attribute("Margin"));
        Assert.Equal("10,0,0,0", (string?)content.Elements(Wpf + "TextBlock").Single().Attribute("Margin"));
        Assert.Equal("0,6,0,0", (string?)add.Parent!.Attribute("Margin"));
        Assert.Contains(add.Parent!.Elements(Wpf + "Separator"), separator =>
            (string?)separator.Attribute("Style") == "{StaticResource ContextMenuSeparatorStyle}"
            && (string?)separator.Attribute("Margin") == "10,0,10,6");
    }

    private static XElement ConnectionButton(XElement popup)
    {
        var list = popup.Descendants(Wpf + "ItemsControl")
            .Single(node => (string?)node.Attribute(Xaml + "Name") == "StorageMenuList");
        return list.Element(Wpf + "ItemsControl.ItemTemplate")!
            .Element(Wpf + "DataTemplate")!.Element(Wpf + "Button")!;
    }
}
