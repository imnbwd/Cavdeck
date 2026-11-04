using System.Xml.Linq;
using Xunit;

namespace Cavdeck.App.Tests;

public sealed class ExplorerDetailsPaneTests
{
    private static readonly XNamespace Wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static XDocument View() =>
        XDocument.Load(Path.Combine(AppContext.BaseDirectory, "CavdeckExplorerView.xaml"));

    [Fact]
    public void Contextual_copy_buttons_reveal_on_hover_and_keyboard_focus()
    {
        var view = View();
        var style = view.Descendants(Wpf + "Style").Single(s =>
            (string?)s.Attribute(Xaml + "Key") == "DetailCopyButton");
        Assert.Contains(style.Elements(Wpf + "Setter"), s =>
            (string?)s.Attribute("Property") == "Opacity" && (string?)s.Attribute("Value") == "0");
        Assert.DoesNotContain(style.Elements(Wpf + "Setter"), s =>
            (string?)s.Attribute("Property") == "Visibility");
        var triggers = style.Element(Wpf + "Style.Triggers")!;
        Assert.Contains(triggers.Elements(Wpf + "DataTrigger"), trigger =>
            ((string?)trigger.Attribute("Binding"))?.Contains("AncestorType={x:Type DockPanel}") == true);
        Assert.Contains(triggers.Elements(Wpf + "Trigger"), trigger =>
            (string?)trigger.Attribute("Property") == "IsKeyboardFocused"
            && (string?)trigger.Attribute("Value") == "True");
    }

    [Fact]
    public void All_five_detail_copy_actions_are_contextual_without_losing_commands()
    {
        var view = View();
        var buttons = view.Descendants(Wpf + "Button")
            .Where(b => ((string?)b.Attribute("Style"))?.Contains("DetailCopy") == true).ToArray();
        Assert.Equal(5, buttons.Length);
        Assert.All(buttons, button =>
            Assert.Equal("{StaticResource DetailCopyButton}", (string?)button.Attribute("Style")));
        Assert.All(buttons, button =>
            Assert.Contains("CopyDetailValueCommand", (string?)button.Attribute("Command")));
        Assert.All(buttons, button => Assert.NotNull(button.Attribute("CommandParameter")));
        Assert.All(buttons, b =>
        {
            Assert.NotNull(b.Attribute("ToolTip"));
            Assert.NotNull(b.Attribute("AutomationProperties.Name"));
            Assert.Equal("Transparent", (string?)b.Parent?.Attribute("Background"));
        });
    }

    [Fact]
    public void Detail_values_keep_raw_copy_parameters_and_contextual_help()
    {
        var view = View();
        var labels = view.Descendants(Wpf + "TextBlock").ToArray();
        Assert.Contains(labels, l => ((string?)l.Attribute("ToolTip"))?.Contains("Explorer_ETagHint") == true);
        Assert.Contains(labels, l => ((string?)l.Attribute("ToolTip"))?.Contains("Explorer_ContentTypeHint") == true);
        var parameters = view.Descendants(Wpf + "Button")
            .Select(b => (string?)b.Attribute("CommandParameter")).Where(s => s is not null).ToArray();
        foreach (var value in new[] { "Name", "Key", "Uri", "ETagRaw", "ContentType" })
            Assert.Contains(parameters, parameter => parameter!.Contains("Binding " + value));
    }

    [Fact]
    public void Detail_sections_adapt_to_location_and_selection_scope()
    {
        var view = View();
        var controls = view.Descendants(Wpf + "StackPanel").ToArray();
        foreach (var property in new[] { "ShowLocation", "ShowBucket", "ShowKey", "ShowUri" })
            Assert.Contains(controls, panel =>
                ((string?)panel.Attribute("Visibility"))?.Contains(property) == true);

        Assert.Contains(view.Descendants(Wpf + "Button"), button =>
            ((string?)button.Attribute("Visibility"))?.Contains("ShowNameCopy") == true);

        Assert.Contains(view.Descendants(Wpf + "TextBlock"), label =>
            ((string?)label.Attribute("Text"))?.Contains("Explorer_CopyToBucket") == true);
    }
}
