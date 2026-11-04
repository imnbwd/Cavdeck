using System.Windows.Media;
using System.Xml.Linq;
using Cavdeck.App.Features.Transfers;
using Xunit;

namespace Cavdeck.App.Tests;

public sealed class ActionIconConsistencyTests
{
    private static readonly XNamespace Wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Theory]
    [InlineData("IconRefresh")]
    [InlineData("IconGridDensity")]
    [InlineData("IconPause")]
    [InlineData("IconResume")]
    [InlineData("IconInfo")]
    public void Refined_action_glyphs_have_safe_16_dip_bounds(string key)
    {
        var geometry = Geometry.Parse(Icon(key).Value);
        var bounds = geometry.Bounds;
        Assert.True(bounds.Width >= 5 && bounds.Height >= 6, key + ": too small");
        Assert.True(bounds.Left >= 1.5 && bounds.Top >= 1.5, key + ": clipped top or left");
        Assert.True(bounds.Right <= 14.5 && bounds.Bottom <= 14.5, key + ": clipped bottom or right");
    }

    [Fact]
    public void Refresh_uses_one_arrow_and_density_has_six_complete_tiles()
    {
        var refresh = PathGeometry.CreateFromGeometry(Geometry.Parse(Icon("IconRefresh").Value));
        Assert.Equal(2, refresh.Figures.Count); // One circular stroke and one arrowhead.
        var density = PathGeometry.CreateFromGeometry(Geometry.Parse(Icon("IconGridDensity").Value));
        Assert.Equal(6, density.Figures.Count);
        Assert.All(density.Figures, figure => Assert.True(figure.IsClosed));
    }

    [Fact]
    public void Grid_density_button_uses_its_own_semantic_glyph()
    {
        var explorer = Load("CavdeckExplorerView.xaml");
        var density = explorer.Descendants(Wpf + "Button").Single(button =>
            (string?)button.Attribute("Command") == "{Binding Explorer.ToggleGridDensityCommand}");
        Assert.Equal("{StaticResource IconGridDensity}",
            (string?)Assert.Single(density.Elements(Wpf + "Path")).Attribute("Data"));
    }

    [Fact]
    public void Transfers_reuse_shared_action_icons_and_show_job_directions()
    {
        var transfers = Load("CavdeckTransfersView.xaml");
        Assert.DoesNotContain(transfers.Descendants(Wpf + "Path"),
            path => ((string?)path.Attribute("Data"))?.StartsWith("M", StringComparison.Ordinal) == true);
        var dataSetters = transfers.Descendants(Wpf + "Setter")
            .Where(setter => (string?)setter.Attribute("Property") == "Data")
            .Select(setter => (string?)setter.Attribute("Value")).ToArray();
        foreach (var key in new[] { "IconChevronDown", "IconChevronRight" })
            Assert.Contains("{StaticResource " + key + "}", dataSetters);
        foreach (var key in new[] { "IconPause", "IconResume" })
            Assert.Contains(transfers.Descendants(Wpf + "Path"),
                path => (string?)path.Attribute("Data") == "{StaticResource " + key + "}");

        var icon = transfers.Descendants(Wpf + "Path").Single(path =>
            (string?)path.Attribute("Grid.Column") == "0" &&
            (string?)path.Attribute("Margin") == "0,0,10,0");
        var triggers = icon.Descendants(Wpf + "DataTrigger").ToArray();
        foreach (var (kind, glyph) in new[]
                 {
                     ("Upload", "IconUpload"), ("Download", "IconDownload"), ("Copy", "IconCopy")
                 })
        {
            var trigger = Assert.Single(triggers, item =>
                (string?)item.Attribute("Binding") == "{Binding Kind}" &&
                (string?)item.Attribute("Value") == "{x:Static domain:TransferKind." + kind + "}");
            Assert.Contains(trigger.Elements(Wpf + "Setter"), setter =>
                (string?)setter.Attribute("Property") == "Data" &&
                (string?)setter.Attribute("Value") == "{StaticResource " + glyph + "}");
        }
        Assert.NotNull(typeof(TransferRowViewModel).GetProperty(nameof(TransferRowViewModel.Kind)));
        Assert.Contains(transfers.Descendants(Wpf + "Button"), button =>
            ((string?)button.Attribute("Command"))?.Contains("Transfers.PauseCommand") == true);
        Assert.Contains(transfers.Descendants(Wpf + "Button"), button =>
            ((string?)button.Attribute("Command"))?.Contains("Transfers.ResumeCommand") == true);
    }

    [Fact]
    public void Empty_states_share_the_theme_aware_large_glyph_style()
    {
        var surfaces = Load("CavdeckSurfaces.xaml");
        var style = surfaces.Descendants(Wpf + "Style").Single(item =>
            (string?)item.Attribute(Xaml + "Key") == "PageEmptyGlyph");
        Assert.Equal("{StaticResource IconGlyph}", (string?)style.Attribute("BasedOn"));
        Assert.Contains(style.Elements(Wpf + "Setter"), setter =>
            (string?)setter.Attribute("Property") == "StrokeThickness" &&
            (string?)setter.Attribute("Value") == "2.25");
        Assert.Contains(style.Elements(Wpf + "Setter"), setter =>
            (string?)setter.Attribute("Property") == "Stroke" &&
            (string?)setter.Attribute("Value") == "{DynamicResource AccentBrush}");

        foreach (var filename in new[]
                 {
                     "CavdeckConnectionsView.xaml", "CavdeckExplorerView.xaml",
                     "CavdeckPlacesView.xaml", "CavdeckTransfersView.xaml"
                 })
        {
            var view = Load(filename);
            var icons = view.Descendants(Wpf + "Border")
                .Where(border => (string?)border.Attribute("Style") == "{StaticResource PageEmptyIcon}")
                .SelectMany(border => border.Descendants(Wpf + "Path")).ToArray();
            Assert.NotEmpty(icons);
            foreach (var icon in icons)
            {
                var direct = (string?)icon.Attribute("Style") == "{StaticResource PageEmptyGlyph}";
                var inherited = icon.Descendants(Wpf + "Style").Any(item =>
                    (string?)item.Attribute("BasedOn") == "{StaticResource PageEmptyGlyph}");
                Assert.True(direct || inherited, filename + ": inconsistent empty-state glyph");
            }
        }
    }

    [Fact]
    public void Place_icons_inherit_shared_stroke_and_alignment_rules()
    {
        var view = Load("CavdeckPlacesView.xaml");
        var style = view.Descendants(Wpf + "Style").Single(item =>
            (string?)item.Attribute(Xaml + "Key") == "PlaceGlyph");
        Assert.Equal("{StaticResource IconGlyph}", (string?)style.Attribute("BasedOn"));
    }

    private static XElement Icon(string key) => Load("FileIconGeometries.xaml")
        .Descendants(Wpf + "StreamGeometry").Single(item => (string?)item.Attribute(Xaml + "Key") == key);

    private static XDocument Load(string filename) =>
        XDocument.Load(Path.Combine(AppContext.BaseDirectory, filename));
}
