using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Xml.Linq;
using Xunit;

namespace Cavdeck.App.Tests;

public sealed class FileIconGeometryTests
{
    [Fact]
    public void Every_file_type_glyph_has_the_same_visible_bounds()
    {
        var geometryXml = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "FileIconGeometries.xaml"));
        var styleXml = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "FileGlyphStyles.xaml"));
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

        var geometries = geometryXml.Descendants()
            .Where(element => element.Name.LocalName == "StreamGeometry")
            .ToDictionary(element => (string)element.Attribute(xaml + "Key")!, element => element.Value);
        var referencedKeys = styleXml.Descendants()
            .Where(element => element.Name.LocalName == "Setter")
            .Select(element => Regex.Match((string?)element.Attribute("Value") ?? "",
                @"^\{DynamicResource (Icon\w+)\}$"))
            .Where(match => match.Success)
            .Select(match => match.Groups[1].Value)
            .Distinct();

        foreach (var key in referencedKeys)
        {
            Assert.True(geometries.TryGetValue(key, out var path), $"Missing geometry: {key}");
            Assert.Equal(new Rect(3, 3, 10, 10), Geometry.Parse(path!).Bounds);
        }
    }

    [Fact]
    public void Grid_file_glyph_uses_density_aware_dimensions_without_changing_list_glyphs()
    {
        var viewXml = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "CavdeckExplorerView.xaml"));
        var glyphStylesXml = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "FileGlyphStyles.xaml"));
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

        var gridStyle = viewXml.Descendants().Single(element =>
            element.Name.LocalName == "Style" &&
            (string?)element.Attribute(xaml + "Key") == "GridThumbnailFallbackGlyph");

        Assert.Equal("{StaticResource ThumbnailFallbackGlyph}", (string?)gridStyle.Attribute("BasedOn"));

        var setters = gridStyle.Elements()
            .Where(element => element.Name.LocalName == "Setter")
            .ToDictionary(element => (string)element.Attribute("Property")!);

        const string densityBinding =
            "{Binding DataContext.Explorer.TileIconSize, RelativeSource={RelativeSource AncestorType=ListBox}}";
        Assert.Equal(densityBinding, (string?)setters["Width"].Attribute("Value"));
        Assert.Equal(densityBinding, (string?)setters["Height"].Attribute("Value"));

        var iconList = viewXml.Descendants().Single(element =>
            element.Name.LocalName == "ListBox" &&
            (string?)element.Attribute(xaml + "Name") == "Icons");
        var gridGlyph = iconList.Descendants().Single(element =>
            element.Name.LocalName == "Path" &&
            (string?)element.Attribute("Style") == "{StaticResource GridThumbnailFallbackGlyph}");
        Assert.NotNull(gridGlyph);

        // The shared fallback remains 16x16 so compact List View glyphs do not grow.
        var sharedGlyphStyle = glyphStylesXml.Descendants().Single(element =>
            element.Name.LocalName == "Style" &&
            (string?)element.Attribute(xaml + "Key") == "FileGlyph");
        var sharedSetters = sharedGlyphStyle.Elements()
            .Where(element => element.Name.LocalName == "Setter")
            .ToDictionary(element => (string)element.Attribute("Property")!);
        Assert.Equal("16", (string?)sharedSetters["Width"].Attribute("Value"));
        Assert.Equal("16", (string?)sharedSetters["Height"].Attribute("Value"));
    }

    [Fact]
    public void Unknown_file_fallback_has_folding_and_an_interior_mark()
    {
        var styleXml = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "FileGlyphStyles.xaml"));
        var geometryXml = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "FileIconGeometries.xaml"));
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

        var style = styleXml.Descendants().Single(element =>
            element.Name.LocalName == "Style" &&
            (string?)element.Attribute(xaml + "Key") == "FileGlyph");
        var fallback = style.Elements().Single(element =>
            element.Name.LocalName == "Setter" &&
            (string?)element.Attribute("Property") == "Data");
        Assert.Equal("{DynamicResource IconGenericFile}", (string?)fallback.Attribute("Value"));

        var path = geometryXml.Descendants().Single(element =>
            element.Name.LocalName == "StreamGeometry" &&
            (string?)element.Attribute(xaml + "Key") == "IconGenericFile").Value;
        var figures = PathGeometry.CreateFromGeometry(Geometry.Parse(path)).Figures;
        Assert.Equal(3, figures.Count); // File outline, folded corner, and short interior mark.
        Assert.Single(figures, figure => figure.IsClosed);
        Assert.Equal(new Rect(3, 3, 10, 10), Geometry.Parse(path).Bounds);
    }
}
