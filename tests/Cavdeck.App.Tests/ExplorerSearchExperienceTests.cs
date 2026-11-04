using System.Xml.Linq;
using Xunit;

namespace Cavdeck.App.Tests;

public sealed class ExplorerSearchExperienceTests
{
    private static readonly XNamespace Wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void Search_offers_four_scopes_and_actions_for_stopping_continuing_and_clearing()
    {
        var view = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "CavdeckExplorerView.xaml"));
        var searchMenu = view.Descendants(Wpf + "Button")
            .Single(node => (string?)node.Attribute(Xaml + "Name") == "SearchScopeButton")
            .Descendants(Wpf + "MenuItem").ToList();
        Assert.Equal(new[] { "0", "3", "1", "2" },
            searchMenu.Select(node => (string?)node.Attribute("Tag")));

        var buttons = view.Descendants(Wpf + "Button").ToList();
        Assert.Contains(buttons, node => ((string?)node.Attribute("Command"))?.Contains("Explorer.ClearSearchCommand") == true
            && ((string?)node.Attribute("ToolTip"))?.Contains("Explorer_ClearSearch") == true);
        Assert.Contains(buttons, node => ((string?)node.Attribute("Command"))?.Contains("Explorer.ContinueSearchCommand") == true
            && ((string?)node.Attribute("Visibility"))?.Contains("Explorer.CanContinueSearch") == true);
        Assert.Contains(buttons, node => ((string?)node.Attribute("Command"))?.Contains("Explorer.CancelSearchCommand") == true
            && ((string?)node.Attribute("Visibility"))?.Contains("Explorer.CanCancelSearch") == true);
    }

    [Fact]
    public void Search_distinguishes_pending_partial_and_complete_no_match_states()
    {
        var view = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "CavdeckExplorerView.xaml"));
        var bindings = view.Descendants(Wpf + "StackPanel")
            .Select(node => (string?)node.Attribute("Visibility") ?? "").ToArray();
        Assert.Contains(bindings, value => value.Contains("Explorer.ShowSearchPrompt"));
        Assert.Contains(bindings, value => value.Contains("Explorer.ShowSearchPartialEmpty"));
        Assert.Contains(bindings, value => value.Contains("Explorer.ShowNoMatches"));
        Assert.Contains(view.Descendants(Wpf + "TextBlock"), node =>
            ((string?)node.Attribute("Text"))?.Contains("Explorer.SearchStatusText") == true);
    }
}
