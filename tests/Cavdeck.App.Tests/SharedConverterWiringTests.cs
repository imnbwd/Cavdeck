using System.Xml.Linq;
using Cavdeck.App.Controls.Converters;
using Xunit;

namespace Cavdeck.App.Tests;

public sealed class SharedConverterWiringTests
{
    private const string ConverterNamespace = "clr-namespace:Cavdeck.App.Controls.Converters";

    [Fact]
    public void Shared_converters_use_the_controls_converter_namespace()
    {
        var converters = new[]
        {
            typeof(BooleanToVisibilityConverter),
            typeof(StringToVisibilityConverter),
            typeof(ConnectionMarkConverter),
            typeof(ConnectionPresentConverter)
        };

        Assert.All(converters, converter =>
            Assert.Equal("Cavdeck.App.Controls.Converters", converter.Namespace));
    }

    [Theory]
    [InlineData("CavdeckLoadingView.xaml")]
    [InlineData("CavdeckConnectionsView.xaml")]
    [InlineData("CavdeckExplorerView.xaml")]
    [InlineData("CavdeckPlacesView.xaml")]
    [InlineData("CavdeckPreviewPane.xaml")]
    [InlineData("CavdeckSettings.xaml")]
    [InlineData("CavdeckTransfersView.xaml")]
    [InlineData("CavdeckMenus.xaml")]
    [InlineData("CavdeckShellWindow.xaml")]
    public void Xaml_shared_converter_resources_use_the_same_namespace(string fileName)
    {
        var document = XDocument.Load(Path.Combine(AppContext.BaseDirectory, fileName));
        var xmlNamespace = document.Root!.GetNamespaceOfPrefix("converters");
        Assert.Equal(ConverterNamespace, xmlNamespace?.NamespaceName);

        var expectedTypes = new HashSet<string>
        {
            nameof(BooleanToVisibilityConverter),
            nameof(StringToVisibilityConverter),
            nameof(ConnectionMarkConverter),
            nameof(ConnectionPresentConverter)
        };
        var converterElements = document.Descendants()
            .Where(element => element.Name.NamespaceName == ConverterNamespace)
            .ToArray();

        Assert.NotEmpty(converterElements);
        Assert.All(converterElements, element =>
            Assert.Contains(element.Name.LocalName, expectedTypes));
    }
}
