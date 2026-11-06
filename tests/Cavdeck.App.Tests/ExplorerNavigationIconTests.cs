using System.Windows;
using System.Windows.Media;
using System.Xml.Linq;
using Xunit;

namespace Cavdeck.App.Tests;

public sealed class ExplorerNavigationIconTests
{
    private static readonly XNamespace Wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void Go_to_path_is_a_localized_navigation_button_with_a_distinct_icon()
    {
        var view = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "CavdeckExplorerView.xaml"));
        var button = Assert.Single(view.Descendants(Wpf + "Button"), element =>
            (string?)element.Attribute("Command") == "{Binding Explorer.GoToPathCommand}");
        Assert.Equal("{Binding Explorer.RefreshCommand}", (string?)button.ElementsBeforeSelf().Last().Attribute("Command"));
        Assert.Contains("Explorer.ShowObjects", (string?)button.Attribute("Visibility"));
        Assert.Equal("{Binding Texts[ExplorerPath_Tooltip]}", (string?)button.Attribute("ToolTip"));
        Assert.Equal("{Binding Texts[ExplorerPath_Title]}", (string?)button.Attribute("AutomationProperties.Name"));
        Assert.Equal("{StaticResource ExplorerTool}", (string?)button.Attribute("Style"));

        var glyph = Assert.Single(button.Elements(Wpf + "Path"));
        Assert.Equal("{StaticResource IconGlyph}", (string?)glyph.Attribute("Style"));
        Assert.Equal("{StaticResource IconGoToPath}", (string?)glyph.Attribute("Data"));
        var geometry = Read("IconGoToPath");
        var stroke = new Pen(Brushes.Black, 1.5);
        Assert.True(geometry.StrokeContains(stroke, new Point(10.7, 9)));
        Assert.NotEqual(Read("IconFolderUp").ToString(), geometry.ToString());
        Assert.True(new Rect(0, 0, 16, 16).Contains(geometry.GetRenderBounds(stroke)));
    }

    [Fact]
    public void Up_button_uses_a_parent_folder_icon_without_changing_its_navigation_command()
    {
        var view = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "CavdeckExplorerView.xaml"));
        var button = view.Descendants(Wpf + "Button").Single(b =>
            (string?)b.Attribute("Command") == "{Binding Explorer.UpCommand}");

        Assert.Equal("{Binding Explorer.CanGoUp}", (string?)button.Attribute("IsEnabled"));
        Assert.Equal("{Binding Texts[Explorer_Up]}", (string?)button.Attribute("ToolTip"));
        Assert.Equal("{Binding Texts[Explorer_Up]}", (string?)button.Attribute("AutomationProperties.Name"));
        Assert.Equal("{StaticResource ExplorerTool}", (string?)button.Attribute("Style"));

        var glyph = Assert.Single(button.Elements(Wpf + "Path"));
        Assert.Equal("{StaticResource IconGlyph}", (string?)glyph.Attribute("Style"));
        Assert.Equal("{StaticResource IconFolderUp}", (string?)glyph.Attribute("Data"));
    }

    [Fact]
    public void Parent_folder_icon_is_a_complete_folder_with_an_inside_up_arrow()
    {
        var geometry = Read("IconFolderUp");
        var figures = PathGeometry.CreateFromGeometry(geometry).Figures;
        Assert.Equal(3, figures.Count);
        Assert.Single(figures, figure => figure.IsClosed);

        var bounds = geometry.Bounds;
        Assert.InRange(bounds.Left, 2, 3);
        Assert.InRange(bounds.Top, 3, 4);
        Assert.InRange(bounds.Right, 13, 14);
        Assert.InRange(bounds.Bottom, 12, 13);

        var stroke = new Pen(Brushes.Black, 1.5);
        foreach (var point in new[]
                 {
                     new Point(2.5, 3.7), // Folder corner
                     new Point(6, 3.7),   // Folder tab
                     new Point(7.4, 5.3), // Folder shoulder
                     new Point(13.5, 10),// Folder side
                     new Point(8, 10.8),  // Up arrow stem
                     new Point(8, 7.7),   // Up arrow tip
                     new Point(6.5, 9.2), // Left arrow head
                     new Point(9.5, 9.2)  // Right arrow head
                 })
            Assert.True(geometry.StrokeContains(stroke, point),
                $"Missing parent-folder stroke at ({point.X}, {point.Y}).");

        // There is no top-spanning 'go to top' bar. The glyph also must not be
        // mistaken for the standalone Upload arrow.
        Assert.False(geometry.StrokeContains(stroke, new Point(11, 3.7)));
        Assert.NotEqual(Read("IconUp").ToString(), geometry.ToString());
        Assert.NotEqual(Read("IconUpload").ToString(), geometry.ToString());
    }

    private static Geometry Read(string iconKey)
    {
        var dictionary = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "FileIconGeometries.xaml"));
        var resource = dictionary.Descendants(Wpf + "StreamGeometry")
            .Single(e => (string?)e.Attribute(Xaml + "Key") == iconKey);
        return Geometry.Parse(resource.Value);
    }
}
