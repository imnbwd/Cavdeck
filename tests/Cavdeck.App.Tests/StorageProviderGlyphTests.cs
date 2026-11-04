using System.Windows.Media;
using System.Xml.Linq;
using Cavdeck.App.Controls;
using Cavdeck.Domain.Storage;
using Xunit;

namespace Cavdeck.App.Tests;

public sealed class StorageProviderGlyphTests
{
    private static readonly XNamespace Wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Theory]
    [InlineData("IconProviderAwsS3")]
    [InlineData("IconProviderCloudflareR2")]
    [InlineData("IconProviderMinio")]
    [InlineData("IconProviderS3Compatible")]
    public void Every_provider_icon_has_visible_strokes_inside_the_icon_box(string key)
    {
        var resource = Icons().Descendants(Wpf + "StreamGeometry")
            .Single(element => (string?)element.Attribute(Xaml + "Key") == key);
        var geometry = Geometry.Parse(resource.Value);
        var bounds = geometry.Bounds;
        Assert.True(bounds.Width >= 9 && bounds.Height >= 8.5, key + ": too small");
        Assert.True(bounds.Left >= 2 && bounds.Top >= 2, key + ": stroke touches top/left edge");
        Assert.True(bounds.Right <= 14 && bounds.Bottom <= 14, key + ": stroke touches bottom/right edge");
        Assert.NotEmpty(PathGeometry.CreateFromGeometry(geometry).Figures);
    }

    [Fact]
    public void Distinct_provider_triggers_keep_generic_endpoint_as_fallback()
    {
        var icons = Icons();
        var style = icons.Descendants(Wpf + "Style")
            .Single(element => (string?)element.Attribute(Xaml + "Key") == "ProviderGlyphStyle");
        var path = style.Descendants(Wpf + "Path").Single();
        Assert.Equal("{StaticResource IconProviderS3Compatible}", (string?)path.Attribute("Data"));
        Assert.Equal("{TemplateBinding Foreground}", (string?)path.Attribute("Stroke"));

        var expected = new Dictionary<StorageProviderKind, string>
        {
            [StorageProviderKind.AwsS3] = "IconProviderAwsS3",
            [StorageProviderKind.CloudflareR2] = "IconProviderCloudflareR2",
            [StorageProviderKind.Minio] = "IconProviderMinio"
        };
        var triggers = style.Descendants(Wpf + "Trigger").ToArray();
        Assert.Equal(expected.Count, triggers.Length);
        foreach (var (provider, icon) in expected)
        {
            var trigger = Assert.Single(triggers, element =>
                (string?)element.Attribute("Value") == "{x:Static domain:StorageProviderKind." + provider + "}");
            Assert.Contains(trigger.Elements(Wpf + "Setter"), setter =>
                (string?)setter.Attribute("Property") == "Data"
                && (string?)setter.Attribute("Value") == "{StaticResource " + icon + "}");
        }
        var shapes = expected.Values.Append("IconProviderS3Compatible").Select(key =>
            icons.Descendants(Wpf + "StreamGeometry")
                .Single(element => (string?)element.Attribute(Xaml + "Key") == key).Value);
        Assert.Equal(4, shapes.Distinct().Count());
        var bucket = icons.Descendants(Wpf + "StreamGeometry")
            .Single(element => (string?)element.Attribute(Xaml + "Key") == "IconBucket").Value;
        Assert.DoesNotContain(bucket, shapes);
        Assert.Equal(StorageProviderKind.S3Compatible,
            ProviderGlyph.ProviderProperty.GetMetadata(typeof(ProviderGlyph)).DefaultValue);
    }

    [Fact]
    public void Every_connection_surface_uses_the_same_glyph_mapping()
    {
        var sidebar = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "CavdeckShellWindow.xaml"));
        var management = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "CavdeckConnectionsView.xaml"));
        var sidebarIcons = sidebar.Descendants()
            .Where(element => element.Name.LocalName == "ProviderGlyph").ToArray();
        var managementIcons = management.Descendants()
            .Where(element => element.Name.LocalName == "ProviderGlyph").ToArray();

        Assert.Equal(2, sidebarIcons.Length); // Expanded and compact menus.
        Assert.Equal(2, managementIcons.Length); // Connection cards and provider selection.
        Assert.Equal(3, sidebarIcons.Concat(managementIcons).Count(
            element => (string?)element.Attribute("Provider") == "{Binding Connection.Provider}"));
        Assert.Contains(managementIcons,
            element => (string?)element.Attribute("Provider") == "{Binding Kind}");
        Assert.All(sidebarIcons.Concat(managementIcons), element =>
            Assert.Equal("{StaticResource ProviderGlyphStyle}", (string?)element.Attribute("Style")));
    }

    private static XDocument Icons() =>
        XDocument.Load(Path.Combine(AppContext.BaseDirectory, "FileIconGeometries.xaml"));
}
