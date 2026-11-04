using System.Xml.Linq;
using Xunit;

namespace Cavdeck.App.Tests;

public sealed class ConnectionAdvancedToggleStyleTests
{
    private static readonly XNamespace Wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static XDocument View() =>
        XDocument.Load(Path.Combine(AppContext.BaseDirectory, "CavdeckConnectionsView.xaml"));

    [Fact]
    public void Advanced_toggle_uses_compact_secondary_button_chrome_without_changing_its_command()
    {
        var button = View().Descendants(Wpf + "Button").Single(element =>
            (string?)element.Attribute(Xaml + "Name") == "AdvancedToggleButton");

        Assert.Equal("{StaticResource SecondaryButton}", (string?)button.Attribute("Style"));
        Assert.Equal("{DynamicResource ControlHeightCompact}", (string?)button.Attribute("MinHeight"));
        Assert.Equal("{DynamicResource ControlPaddingCompact}", (string?)button.Attribute("Padding"));
        Assert.Equal("Left", (string?)button.Attribute("HorizontalAlignment"));
        Assert.Equal("{Binding Editor.ToggleAdvancedCommand}", (string?)button.Attribute("Command"));
        Assert.Equal("{Binding Editor.AdvancedLabel}", (string?)button.Attribute("AutomationProperties.Name"));
        Assert.Contains(button.Descendants(Wpf + "TextBlock"),
            label => (string?)label.Attribute("Text") == "{Binding Editor.AdvancedLabel}");
        Assert.Contains(button.Descendants(Wpf + "Path"),
            chevron => (string?)chevron.Attribute("Data") == "{StaticResource IconChevronDown}" &&
                       (string?)chevron.Attribute("Style") == "{StaticResource AdvancedToggleChevron}");
    }

    [Fact]
    public void Advanced_chevron_reflects_expanded_state_using_shared_icon_style()
    {
        var style = View().Descendants(Wpf + "Style").Single(element =>
            (string?)element.Attribute(Xaml + "Key") == "AdvancedToggleChevron");

        Assert.Equal("{StaticResource IconGlyph}", (string?)style.Attribute("BasedOn"));
        var expanded = style.Descendants(Wpf + "DataTrigger").Single();
        Assert.Equal("{Binding Editor.ShowAdvanced}", (string?)expanded.Attribute("Binding"));
        Assert.Equal("True", (string?)expanded.Attribute("Value"));
        Assert.Contains(expanded.Descendants(Wpf + "RotateTransform"),
            transform => (string?)transform.Attribute("Angle") == "180");
    }
}
