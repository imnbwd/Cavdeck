using System.Xml.Linq;
using Xunit;

namespace Cavdeck.App.Tests;

public sealed class ToastAccessibilityTests
{
    private static readonly XNamespace Wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    [Fact]
    public void Toasts_announce_one_localized_summary_without_stealing_focus()
    {
        var xaml = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "CavdeckToastHost.xaml"));
        var card = Assert.Single(xaml.Descendants(Wpf + "Border"), element =>
            (string?)element.Attribute("Name") == "Card" ||
            element.Attributes().Any(attribute => attribute.Name.LocalName == "Name"
                && attribute.Value == "Card"));
        Assert.Equal("False", (string?)card.Attribute("Focusable"));
        Assert.Equal("Card_GotKeyboardFocus", (string?)card.Attribute("GotKeyboardFocus"));
        Assert.Equal("Card_LostKeyboardFocus", (string?)card.Attribute("LostKeyboardFocus"));

        var announcement = Assert.Single(xaml.Descendants(Wpf + "TextBlock"), element =>
            element.Attributes().Any(attribute => attribute.Name.LocalName == "Name"
                && attribute.Value == "Announcement"));
        Assert.Equal("{Binding AccessibleAnnouncement}",
            (string?)announcement.Attribute("AutomationProperties.Name"));
        Assert.Equal("Announcement_Loaded", (string?)announcement.Attribute("Loaded"));
        Assert.Equal("Polite",
            (string?)announcement.Attribute("AutomationProperties.LiveSetting"));

        Assert.Contains(xaml.Descendants(Wpf + "Setter"), element =>
            (string?)element.Attribute("TargetName") == "Announcement" &&
            (string?)element.Attribute("Property") == "AutomationProperties.LiveSetting" &&
            (string?)element.Attribute("Value") == "Assertive");
        Assert.DoesNotContain(xaml.Descendants(), element =>
            (string?)element.Attribute("Focus") == "True");
    }

    [Fact]
    public void Keyboard_actions_and_copy_feedback_support_long_localized_labels()
    {
        var xml = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "CavdeckToastHost.xaml")).ToString();
        Assert.Contains("{Binding CopyCommand}", xml);
        Assert.Contains("{Binding CopyHelpText}", xml);
        Assert.Contains("{Binding CopyFeedback, NotifyOnTargetUpdated=True}", xml);
        Assert.Contains("CopyFeedback_TargetUpdated", xml);
        Assert.Contains("TextWrapping=\"Wrap\"", xml);
        Assert.Contains("{Binding ActionCommand}", xml);
        Assert.Contains("{Binding DismissCommand}", xml);
    }

    [Fact]
    public void All_four_languages_have_toast_accessibility_and_diagnostic_actions()
    {
        var keys = new[]
        {
            "Toast_DismissNamed", "Toast_ToneInformation", "Toast_ToneSuccess",
            "Toast_ToneWarning", "Toast_ToneError", "Toast_CopyDiagnostics",
            "Toast_CopyHelpText", "Toast_Copied", "Toast_ViewFailedTransfers"
        };

        foreach (var file in new[] { "Strings.resx", "Strings.zh-CN.resx",
                     "Strings.ja-JP.resx", "Strings.de-DE.resx" })
        {
            var resx = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Localization", file));
            var labels = resx.Descendants("data").ToDictionary(
                element => (string)element.Attribute("name")!,
                element => (string?)element.Element("value"));
            foreach (var key in keys)
                Assert.False(string.IsNullOrWhiteSpace(labels[key]), $"{file}: {key}");
            Assert.Contains("{0}", labels["Toast_DismissNamed"]);
        }
    }
}
