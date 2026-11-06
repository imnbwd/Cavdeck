using System.Xml.Linq;
using Xunit;

namespace Cavdeck.App.Tests;

public sealed class ContextMenuIconTests
{
    private static readonly XNamespace Wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Theory]
    [InlineData("CavdeckExplorerView.xaml", 42)]
    [InlineData("CavdeckShellWindow.xaml", 6)]
    [InlineData("CavdeckPlacesView.xaml", 3)]
    [InlineData("CavdeckDesignCatalog.xaml", 3)]
    public void Menu_icons_are_selective_and_use_shared_theme_aware_glyphs(string filename, int expectedIconCount)
    {
        var actions = Actions(filename);
        var iconActions = actions.Where(action => action.Element(Wpf + "MenuItem.Icon") is not null).ToArray();
        Assert.Equal(expectedIconCount, iconActions.Length);
        Assert.True(iconActions.Length <= actions.Length);

        var geometryKeys = Load("FileIconGeometries.xaml")
            .Descendants(Wpf + "StreamGeometry")
            .Select(geometry => (string?)geometry.Attribute(Xaml + "Key"))
            .ToHashSet(StringComparer.Ordinal);

        foreach (var action in iconActions)
        {
            var icon = action.Element(Wpf + "MenuItem.Icon")!;
            var path = Assert.Single(icon.Elements(Wpf + "Path"));
            var isDanger = (string?)action.Attribute(XName.Get("MenuAssist.IsDanger", "clr-namespace:Cavdeck.App.Controls")) == "True";
            var expectedStyle = isDanger ? "ContextMenuDangerIconStyle" : "ContextMenuIconStyle";
            Assert.Equal("{StaticResource " + expectedStyle + "}", (string?)path.Attribute("Style"));
            var resource = (string?)path.Attribute("Data");
            Assert.StartsWith("{StaticResource ", resource);
            var key = resource!["{StaticResource ".Length..^1];
            Assert.Contains(key, geometryKeys);
            Assert.Null(path.Attribute("Stroke")); // The dedicated menu style resolves the theme brush.
        }

        var iconStyle = Load("FileIconGeometries.xaml").Descendants(Wpf + "Style")
            .Single(style => (string?)style.Attribute(Xaml + "Key") == "IconGlyph");
        Assert.Equal("{Binding Foreground, RelativeSource={RelativeSource AncestorType=Control}}",
            Setter(iconStyle, "Stroke"));
        Assert.Equal("16", Setter(iconStyle, "Width"));
        Assert.Equal("16", Setter(iconStyle, "Height"));

        var menuStyles = Load("CavdeckMenus.xaml").Descendants(Wpf + "Style").ToArray();
        var standard = menuStyles.Single(s => (string?)s.Attribute(Xaml + "Key") == "ContextMenuIconStyle");
        var danger = menuStyles.Single(s => (string?)s.Attribute(Xaml + "Key") == "ContextMenuDangerIconStyle");
        Assert.Equal("{StaticResource IconGlyph}", (string?)standard.Attribute("BasedOn"));
        Assert.Equal("{DynamicResource PrimaryTextBrush}", Setter(standard, "Stroke"));
        Assert.Equal("{StaticResource ContextMenuIconStyle}", (string?)danger.Attribute("BasedOn"));
        Assert.Equal("{DynamicResource DangerTextBrush}", Setter(danger, "Stroke"));
    }

    [Theory]
    [InlineData("CavdeckExplorerView.xaml", "Texts[Explorer_Open]", "IconTrayOpen", 2)]
    [InlineData("CavdeckExplorerView.xaml", "Texts[Places_OpenNewTab]", "IconOpenNewTab", 3)]
    [InlineData("CavdeckExplorerView.xaml", "Texts[Share_Menu]", "IconShare", 3)]
    [InlineData("CavdeckExplorerView.xaml", "Explorer.FolderFavoriteTip", "IconStar", 2)]
    [InlineData("CavdeckExplorerView.xaml", "PlacementTarget.DataContext.FavoriteActionText", "IconStar", 1)]
    [InlineData("CavdeckExplorerView.xaml", "Texts[Explorer_Copy]", "IconCopy", 2)]
    [InlineData("CavdeckExplorerView.xaml", "Texts[Explorer_Cut]", "IconCut", 3)]
    [InlineData("CavdeckExplorerView.xaml", "Texts[Explorer_Paste]", "IconPaste", 4)]
    [InlineData("CavdeckExplorerView.xaml", "Texts[Transfer_Download]", "IconDownload", 2)]
    [InlineData("CavdeckExplorerView.xaml", "Texts[Explorer_Delete]", "IconTrash", 3)]
    [InlineData("CavdeckShellWindow.xaml", "DataContext.Texts[Connection_OpenNewTab]", "IconOpenNewTab", 2)]
    [InlineData("CavdeckShellWindow.xaml", "DataContext.Texts[Connection_Delete]", "IconTrash", 2)]
    [InlineData("CavdeckPlacesView.xaml", "{Binding OpenNewTabLabel}", "IconOpenNewTab", 1)]
    [InlineData("CavdeckPlacesView.xaml", "{Binding RemoveLabel}", "IconTrash", 1)]
    public void Key_actions_have_semantically_consistent_icons(
        string filename, string headerSnippet, string expectedKey, int expectedOccurrences)
    {
        var matchingActions = Actions(filename)
            .Where(action => ((string?)action.Attribute("Header"))?.Contains(headerSnippet, StringComparison.Ordinal) == true)
            .ToArray();

        Assert.Equal(expectedOccurrences, matchingActions.Length);
        foreach (var action in matchingActions)
            Assert.Equal("{StaticResource " + expectedKey + "}",
                (string?)action.Element(Wpf + "MenuItem.Icon")?.Element(Wpf + "Path")?.Attribute("Data"));
    }

    [Theory]
    [InlineData("CavdeckExplorerView.xaml", "Texts[Explorer_CopyKey]", 3)]
    [InlineData("CavdeckExplorerView.xaml", "Texts[Explorer_CopyUri]", 3)]
    [InlineData("CavdeckExplorerView.xaml", "Texts[Explorer_CopyTo]", 2)]
    [InlineData("CavdeckExplorerView.xaml", "Texts[Explorer_Rename]", 3)]
    [InlineData("CavdeckExplorerView.xaml", "Texts[Explorer_FilterLoaded]", 1)]
    [InlineData("CavdeckShellWindow.xaml", "DataContext.Texts[Connection_Edit]", 2)]
    [InlineData("CavdeckShellWindow.xaml", "DataContext.Texts[Connection_Test]", 2)]
    [InlineData("CavdeckDesignCatalog.xaml", "Copy key", 1)]
    public void Secondary_or_option_only_actions_remain_text_only(string filename, string headerSnippet, int expectedOccurrences)
    {
        var matchingActions = Actions(filename)
            .Where(action => ((string?)action.Attribute("Header"))?.Contains(headerSnippet, StringComparison.Ordinal) == true)
            .ToArray();

        Assert.Equal(expectedOccurrences, matchingActions.Length);
        Assert.All(matchingActions, action => Assert.Null(action.Element(Wpf + "MenuItem.Icon")));
    }

    [Fact]
    public void Existing_menu_icon_gutter_stays_shared_across_icon_and_text_rows()
    {
        var menus = Load("CavdeckMenus.xaml");
        Assert.Contains(menus.Descendants(Wpf + "Setter"),
            setter => (string?)setter.Attribute("Property") == "Grid.IsSharedSizeScope"
                && (string?)setter.Attribute("Value") == "True");
        Assert.Contains(menus.Descendants(Wpf + "ColumnDefinition"),
            column => (string?)column.Attribute("SharedSizeGroup") == "MenuGlyph");
        Assert.Contains(menus.Descendants(Wpf + "Setter"),
            setter => (string?)setter.Attribute("Property") == "Padding"
                && (string?)setter.Attribute("Value") == "13,0");
    }

    private static XElement[] Actions(string filename) =>
        Load(filename).Descendants(Wpf + "ContextMenu")
            .SelectMany(menu => menu.Descendants(Wpf + "MenuItem"))
            .ToArray();

    private static XDocument Load(string filename) =>
        XDocument.Load(Path.Combine(AppContext.BaseDirectory, filename));

    private static string? Setter(XElement style, string property) =>
        (string?)style.Elements(Wpf + "Setter")
            .Single(element => (string?)element.Attribute("Property") == property)
            .Attribute("Value");
}
