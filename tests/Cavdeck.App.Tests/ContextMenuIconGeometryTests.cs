using System.Windows;
using System.Windows.Media;
using System.Xml.Linq;
using Xunit;

namespace Cavdeck.App.Tests;

public sealed class ContextMenuIconGeometryTests
{
    private static readonly XNamespace Wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Theory]
    [InlineData("IconShare", 5, 3)] // Two links and three complete circular nodes.
    [InlineData("IconCut", 4, 2)] // Two connected blades and two complete finger loops.
    [InlineData("IconPaste", 4, 1)] // Clipboard body, closed clip, and two text lines.
    [InlineData("IconOpenNewTab", 4, 1)] // Window, title bar, diagonal arrow and arrow head.
    public void Menu_action_glyphs_have_complete_figures_and_safe_16px_bounds(
        string iconKey, int figureCount, int closedFigureCount)
    {
        var geometry = Read(iconKey);
        var figures = PathGeometry.CreateFromGeometry(geometry).Figures;
        Assert.Equal(figureCount, figures.Count);
        Assert.Equal(closedFigureCount, figures.Count(figure => figure.IsClosed));

        var bounds = geometry.Bounds;
        Assert.True(bounds.Width >= 7 && bounds.Height >= 7, iconKey + ": too small");
        // Stroke is 1.5 DIP (0.75 DIP beyond the geometry on each side).
        Assert.True(bounds.Left >= 2.0 && bounds.Top >= 2.0, iconKey + ": stroke touches top/left edge");
        Assert.True(bounds.Right <= 14 && bounds.Bottom <= 14, iconKey + ": stroke touches bottom/right edge");
    }

    [Theory]
    [InlineData("IconShare", 2.7, 8.0)]
    [InlineData("IconShare", 13.35, 4.4)]
    [InlineData("IconShare", 13.35, 11.6)]
    [InlineData("IconShare", 8.0, 6.2)]
    [InlineData("IconShare", 8.0, 9.8)]
    [InlineData("IconCut", 2.6, 4.6)]
    [InlineData("IconCut", 2.6, 11.4)]
    [InlineData("IconCut", 12.35, 3.25)]
    [InlineData("IconCut", 12.35, 12.75)]
    [InlineData("IconPaste", 3.4, 9.0)]
    [InlineData("IconPaste", 12.6, 9.0)]
    [InlineData("IconPaste", 8.0, 5.25)]
    [InlineData("IconPaste", 8.0, 8.1)]
    [InlineData("IconPaste", 8.0, 10.4)]
    [InlineData("IconOpenNewTab", 3.0, 3.6)]
    [InlineData("IconOpenNewTab", 13.0, 12.8)]
    [InlineData("IconOpenNewTab", 8.0, 5.9)]
    [InlineData("IconOpenNewTab", 10.3, 7.8)]
    [InlineData("IconOpenNewTab", 7.1, 11.0)]
    public void Required_icon_strokes_are_not_missing(string iconKey, double x, double y)
    {
        var geometry = Read(iconKey);
        Assert.True(geometry.StrokeContains(new Pen(Brushes.Black, 1.5), new Point(x, y)),
            iconKey + ": expected line missing near (" + x + ", " + y + ")");
    }

    private static Geometry Read(string iconKey)
    {
        var dictionary = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "FileIconGeometries.xaml"));
        var resource = dictionary.Descendants(Wpf + "StreamGeometry")
            .Single(node => (string?)node.Attribute(Xaml + "Key") == iconKey);
        return Geometry.Parse(resource.Value);
    }
}
