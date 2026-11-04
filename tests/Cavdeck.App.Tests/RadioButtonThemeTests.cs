using System.Xml.Linq;
using Xunit;

namespace Cavdeck.App.Tests;

public sealed class RadioButtonThemeTests
{
    [Fact]
    public void Radio_buttons_use_dynamic_theme_brushes_for_labels_and_indicators()
    {
        var xml = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "CavdeckInputs.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var style = Assert.Single(xml.Descendants(), element =>
            element.Name.LocalName == "Style"
            && (string?)element.Attribute("TargetType") == "RadioButton"
            && element.Attribute(x + "Key") is null);

        var foreground = Assert.Single(style.Elements(), element =>
            element.Name.LocalName == "Setter"
            && (string?)element.Attribute("Property") == "Foreground");
        Assert.Equal("{DynamicResource PrimaryTextBrush}", (string?)foreground.Attribute("Value"));

        var template = Assert.Single(style.Descendants(), element =>
            element.Name.LocalName == "ControlTemplate"
            && (string?)element.Attribute("TargetType") == "RadioButton");
        var ring = Assert.Single(template.Descendants(), element =>
            (string?)element.Attribute(x + "Name") == "Ring");
        var dot = Assert.Single(template.Descendants(), element =>
            (string?)element.Attribute(x + "Name") == "Dot");

        Assert.Equal("{TemplateBinding Background}", (string?)ring.Attribute("Fill"));
        Assert.Equal("{TemplateBinding BorderBrush}", (string?)ring.Attribute("Stroke"));
        Assert.Equal("{DynamicResource AccentBrush}", (string?)dot.Attribute("Fill"));
        Assert.Equal("Collapsed", (string?)dot.Attribute("Visibility"));

        Assert.Contains(template.Descendants(), element =>
            element.Name.LocalName == "Trigger"
            && (string?)element.Attribute("Property") == "IsChecked"
            && (string?)element.Attribute("Value") == "True"
            && element.Elements().Any(setter => (string?)setter.Attribute("TargetName") == "Dot"
                && (string?)setter.Attribute("Property") == "Visibility"
                && (string?)setter.Attribute("Value") == "Visible"));

        Assert.Contains(template.Descendants(), element =>
            element.Name.LocalName == "Trigger"
            && (string?)element.Attribute("Property") == "IsKeyboardFocused");
        Assert.Contains(template.Descendants(), element =>
            element.Name.LocalName == "Trigger"
            && (string?)element.Attribute("Property") == "IsEnabled"
            && (string?)element.Attribute("Value") == "False");
    }

    [Theory]
    [InlineData("Colors.Light.xaml")]
    [InlineData("Colors.Dark.xaml")]
    public void Radio_button_text_and_selected_dot_have_sufficient_theme_contrast(string theme)
    {
        var xml = XDocument.Load(Path.Combine(AppContext.BaseDirectory, theme));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var brushes = xml.Descendants()
            .Where(element => element.Name.LocalName == "SolidColorBrush")
            .ToDictionary(element => (string)element.Attribute(x + "Key")!,
                element => (string)element.Attribute("Color")!);

        Assert.True(Contrast(brushes["PrimaryTextBrush"], brushes["SurfaceBrush"]) >= 4.5);
        Assert.True(Contrast(brushes["AccentBrush"], brushes["InputBackgroundBrush"]) >= 3);
    }

    private static double Contrast(string a, string b)
    {
        static double Luminance(string hex)
        {
            var rgb = new[] { 1, 3, 5 }.Select(index =>
            {
                var channel = Convert.ToInt32(hex.Substring(index, 2), 16) / 255d;
                return channel <= 0.04045 ? channel / 12.92
                    : Math.Pow((channel + 0.055) / 1.055, 2.4);
            }).ToArray();
            return rgb[0] * 0.2126 + rgb[1] * 0.7152 + rgb[2] * 0.0722;
        }

        var left = Luminance(a);
        var right = Luminance(b);
        return (Math.Max(left, right) + 0.05) / (Math.Min(left, right) + 0.05);
    }
}
