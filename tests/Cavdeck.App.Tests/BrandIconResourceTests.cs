using System.Windows.Media;
using System.Xml.Linq;
using Xunit;

namespace Cavdeck.App.Tests;

public sealed class BrandIconResourceTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void Brand_image_uses_the_v2_vector_paths_not_an_ico_bitmap()
    {
        var document = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "CavdeckBrand.xaml"));
        var image = Assert.Single(document.Descendants()
            , element => element.Name.LocalName == "DrawingImage" &&
                              (string?)element.Attribute(Xaml + "Key") == "CavdeckBrandImage");
        var drawings = image.Descendants().Where(element => element.Name.LocalName == "GeometryDrawing").ToArray();
        Assert.Equal(2, drawings.Length);

        var background = Assert.Single(image.Descendants()
            , element => element.Name.LocalName == "RectangleGeometry");
        Assert.Equal("0,0,800,800", (string?)background.Attribute("Rect"));
        Assert.Equal("171", (string?)background.Attribute("RadiusX"));

        var mark = Assert.Single(drawings, element => element.Attribute("Geometry") != null);
        Assert.Equal("#FFFFFF", (string?)mark.Attribute("Brush"));
        var geometry = Geometry.Parse((string)mark.Attribute("Geometry")!);
        Assert.True(geometry.Bounds.Width > 450, "Brand vector shape should not be empty");
        Assert.True(geometry.Bounds.Height > 500, "Brand vector should cover its expected design bounds");
    }

    [Fact]
    public void Both_ui_locations_share_the_vector_image_but_window_icon_remains_native()
    {
        var application = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "CavdeckApp.xaml"));
        Assert.Contains(application.Descendants(), element =>
            element.Name.LocalName == "ResourceDictionary" &&
            (string?)element.Attribute("Source") == "Themes/Brand.xaml");

        var window = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "CavdeckWindow.xaml"));
        var settings = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "CavdeckSettings.xaml"));

        foreach (var document in new[] { window, settings })
        {
            Assert.Contains(document.Descendants(), element =>
                element.Name.LocalName == "Image" &&
                (string?)element.Attribute("Source") == "{StaticResource CavdeckBrandImage}");
        }

        Assert.Contains(window.Descendants(), element =>
            element.Name.LocalName == "Setter" &&
            (string?)element.Attribute("Property") == "Icon" &&
            (string?)element.Attribute("Value") == "pack://application:,,,/Assets/Cavdeck.ico");
    }
}
