using System.Xml.Linq;
using Xunit;

namespace Cavdeck.App.Tests;

public sealed class InputFieldPaddingTests
{
    private static readonly XNamespace Wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static XDocument Styles() =>
        XDocument.Load(Path.Combine(AppContext.BaseDirectory, "CavdeckInputs.xaml"));

    [Fact]
    public void Text_and_password_fields_share_compact_horizontal_padding()
    {
        var styles = Styles();
        var sharedChrome = styles.Descendants(Wpf + "Style").Single(style =>
            (string?)style.Attribute(Xaml + "Key") == "InputChrome");

        Assert.Equal("8,0", SetterValue(sharedChrome, "Padding"));

        foreach (var targetType in new[] { "TextBox", "PasswordBox" })
        {
            var style = styles.Descendants(Wpf + "Style").Single(element =>
                (string?)element.Attribute("TargetType") == targetType &&
                element.Attribute(Xaml + "Key") is null);

            Assert.Equal("{StaticResource InputChrome}", (string?)style.Attribute("BasedOn"));
            Assert.DoesNotContain(style.Elements(Wpf + "Setter"),
                setter => (string?)setter.Attribute("Property") == "Padding");

            var template = style.Descendants(Wpf + "ControlTemplate").Single(element =>
                (string?)element.Attribute("TargetType") == targetType);
            var contentHost = template.Descendants(Wpf + "ScrollViewer").Single(element =>
                (string?)element.Attribute(Xaml + "Name") == "PART_ContentHost");
            var placeholder = template.Descendants(Wpf + "TextBlock").Single(element =>
                (string?)element.Attribute(Xaml + "Name") == "Placeholder");

            Assert.Equal("{TemplateBinding Padding}", (string?)contentHost.Attribute("Margin"));
            Assert.Equal("{TemplateBinding Padding}", (string?)placeholder.Attribute("Margin"));
        }
    }

    [Fact]
    public void Multiline_text_fields_keep_the_same_horizontal_inset()
    {
        var textBox = Styles().Descendants(Wpf + "Style").Single(style =>
            (string?)style.Attribute("TargetType") == "TextBox" &&
            style.Attribute(Xaml + "Key") is null);
        var multiline = textBox.Descendants(Wpf + "Trigger").Single(trigger =>
            (string?)trigger.Attribute("Property") == "AcceptsReturn" &&
            (string?)trigger.Attribute("Value") == "True");

        Assert.Equal("8,8", SetterValue(multiline, "Padding"));
        Assert.Equal("72", SetterValue(multiline, "MinHeight"));
        Assert.Equal("Top", SetterValue(multiline, "VerticalContentAlignment"));
    }

    private static string? SetterValue(XElement owner, string property) =>
        (string?)owner.Elements(Wpf + "Setter").Single(setter =>
            (string?)setter.Attribute("Property") == property).Attribute("Value");
}
