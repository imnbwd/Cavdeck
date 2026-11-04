using System.Xml.Linq;
using Xunit;

namespace Cavdeck.App.Tests;

public sealed class ContextMenuStyleTests
{
    private static readonly XNamespace Wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void Main_and_nested_menus_size_to_content_without_internal_scrollbars()
    {
        var dictionary = Load("CavdeckMenus.xaml");
        foreach (var target in new[] { "MenuItem", "ContextMenu" })
        {
            var style = dictionary.Descendants(Wpf + "Style")
                .Single(element => (string?)element.Attribute("TargetType") == target);
            var template = style.Descendants(Wpf + "ControlTemplate")
                .First(element => (string?)element.Attribute("TargetType") == target);
            Assert.Empty(template.Descendants(Wpf + "ScrollViewer"));
            Assert.DoesNotContain(template.DescendantsAndSelf(),
                element => element.Attribute("MaxHeight") is not null);
            Assert.NotEmpty(template.Descendants(Wpf + "ItemsPresenter"));
        }
    }

    [Fact]
    public void Menu_items_and_tray_actions_share_the_same_horizontal_padding()
    {
        var dictionary = Load("CavdeckMenus.xaml");
        var item = dictionary.Descendants(Wpf + "Style")
            .Single(element => (string?)element.Attribute("TargetType") == "MenuItem");
        Assert.Equal("13,0", Setter(item, "Padding"));

        var tray = Load("CavdeckTrayMenu.xaml");
        var trayItem = tray.Descendants(Wpf + "Style")
            .Single(element => (string?)element.Attribute(Xaml + "Key") == "TrayMenuItemStyle");
        Assert.Equal("13,0", Setter(trayItem, "Padding"));
    }

    [Fact]
    public void Context_menu_dividers_use_explicit_inset_rule_in_every_menu()
    {
        var dictionary = Load("CavdeckMenus.xaml");
        var separatorStyle = dictionary.Descendants(Wpf + "Style")
            .Single(element => (string?)element.Attribute(Xaml + "Key") == "ContextMenuSeparatorStyle");
        Assert.Equal("12,4", Setter(separatorStyle, "Margin"));
        Assert.Equal("{DynamicResource BorderBrush}", Setter(separatorStyle, "Background"));
        Assert.Equal("False", Setter(separatorStyle, "Focusable"));
        var line = separatorStyle.Descendants(Wpf + "Border").Single();
        Assert.Equal("1", (string?)line.Attribute("Height"));

        var total = 0;
        foreach (var name in new[]
                 {
                     "CavdeckExplorerView.xaml",
                     "CavdeckPlacesView.xaml",
                     "CavdeckDesignCatalog.xaml",
                     "CavdeckTrayMenu.xaml"
                 })
        {
            var document = Load(name);
            foreach (var separator in document.Descendants(Wpf + "ContextMenu")
                         .SelectMany(menu => menu.Elements(Wpf + "Separator")))
            {
                // The tray brand/status header is intentionally a custom Separator template.
                if ((string?)separator.Attribute("Style") == "{StaticResource TrayHeaderStyle}")
                    continue;
                // Tray menu resources can be resolved after the detached context menu
                // is created; other in-window menus keep their static references.
                var expected = name == "CavdeckTrayMenu.xaml"
                    ? "{DynamicResource ContextMenuSeparatorStyle}"
                    : "{StaticResource ContextMenuSeparatorStyle}";
                Assert.Equal(expected, (string?)separator.Attribute("Style"));
                Assert.Null(separator.Attribute("Margin"));
                total++;
            }
        }

        Assert.Equal(15, total);
    }

    private static XDocument Load(string filename) =>
        XDocument.Load(Path.Combine(AppContext.BaseDirectory, filename));

    private static string? Setter(XElement style, string property) =>
        (string?)style.Elements(Wpf + "Setter")
            .Single(element => (string?)element.Attribute("Property") == property)
            .Attribute("Value");
}
