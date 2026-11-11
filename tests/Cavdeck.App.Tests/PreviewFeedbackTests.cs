using System.Xml.Linq;
using Xunit;

namespace Cavdeck.App.Tests;

public sealed class PreviewFeedbackTests
{
    private static readonly XNamespace Wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void Unified_feedback_uses_theme_resources_and_accessible_actions()
    {
        var view = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "CavdeckPreviewFeedbackView.xaml"));
        var border = Assert.Single(view.Descendants(Wpf + "Border"));
        Assert.Equal("{DynamicResource SurfaceBrush}", (string?)border.Attribute("Background"));
        Assert.Equal("{DynamicResource BorderBrush}", (string?)border.Attribute("BorderBrush"));

        var buttons = view.Descendants(Wpf + "Button").ToArray();
        Assert.Equal(2, buttons.Length);
        Assert.All(buttons, b => Assert.NotNull(b.Attribute("AutomationProperties.Name")));
        Assert.Contains(buttons, b => ((string?)b.Attribute("Command"))?.Contains("PrimaryCommand") == true);
        Assert.Contains(buttons, b => ((string?)b.Attribute("Command"))?.Contains("SecondaryCommand") == true);

        var progress = Assert.Single(view.Descendants(Wpf + "ProgressBar"));
        Assert.Contains("ShowProgress", (string?)progress.Attribute("Visibility"));
        Assert.Contains("IsIndeterminate", (string?)progress.Attribute("IsIndeterminate"));
        Assert.Contains("ProgressValue", (string?)progress.Attribute("Value"));
    }

    [Fact]
    public void Pane_and_viewer_use_the_same_feedback_component()
    {
        foreach (var source in new[] { "CavdeckPreviewPane.xaml", "CavdeckImagePreviewWindow.xaml",
                                        "CavdeckMediaPreviewWindow.xaml" })
        {
            var view = XDocument.Load(Path.Combine(AppContext.BaseDirectory, source));
            Assert.Contains(view.Descendants(), node => node.Name.LocalName == "PreviewFeedbackView");
        }
    }
}
