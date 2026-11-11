using System.Xml.Linq;
using Xunit;

namespace Cavdeck.App.Tests;

public sealed class ThumbnailLayoutTests
{
    private static readonly XNamespace Wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly XNamespace Explorer = "clr-namespace:Cavdeck.App.Features.Explorer";

    [Fact]
    public void Both_virtualized_file_views_load_only_realized_image_elements()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "CavdeckExplorerView.xaml");
        var xaml = XDocument.Load(path);
        var images = xaml.Descendants(Wpf + "Image")
            .Where(image => ((string?)image.Attribute("Source"))?.Contains("{Binding Thumbnail}") == true)
            .ToArray();

        Assert.Equal(2, images.Length);
        foreach (var image in images)
        {
            Assert.Equal("True", (string?)image.Attribute(Explorer + "ThumbnailLoader.Enabled"));
            Assert.Null(image.Attribute("Loaded"));
            Assert.Null(image.Attribute("Unloaded"));
            Assert.Equal("False", (string?)image.Attribute("IsHitTestVisible"));
            Assert.Equal("Uniform", (string?)image.Attribute("Stretch"));
        }

        var style = xaml.Descendants(Wpf + "Style")
            .Single(item => (string?)item.Attribute(Xaml + "Key") == "ThumbnailFallbackGlyph");
        Assert.Contains("FileGlyph", (string?)style.Attribute("BasedOn"));
        Assert.Contains(style.Descendants(Wpf + "DataTrigger"), trigger =>
            (string?)trigger.Attribute("Binding") == "{Binding HasThumbnail}" &&
            (string?)trigger.Attribute("Value") == "True");
    }

    [Fact]
    public void Icon_grid_remains_virtualized_and_list_keeps_its_regular_columns()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "CavdeckExplorerView.xaml");
        var xaml = XDocument.Load(path);
        var iconGrid = xaml.Descendants(Wpf + "ListBox").Single(item =>
            (string?)item.Attribute(Xaml + "Name") == "ObjectsIcons");
        Assert.Equal("True", (string?)iconGrid.Attribute("VirtualizingPanel.IsVirtualizing"));
        Assert.Contains(iconGrid.Descendants(), item => item.Name.LocalName == "VirtualizingTilePanel");

        var table = xaml.Descendants(Wpf + "DataGrid").Single(item =>
            (string?)item.Attribute(Xaml + "Name") == "ObjectsGrid");
        Assert.Equal(4, table.Descendants(Wpf + "DataGridTemplateColumn").Count() +
            table.Descendants(Wpf + "DataGridTextColumn").Count());
    }
}
