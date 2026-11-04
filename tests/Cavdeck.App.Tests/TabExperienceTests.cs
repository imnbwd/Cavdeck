using System.Xml.Linq;
using Cavdeck.App.Localization;
using Cavdeck.App.ViewModels;
using Cavdeck.Application.Shell;
using Xunit;

namespace Cavdeck.App.Tests;

public sealed class TabExperienceTests
{
    private static readonly XNamespace Wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void New_tab_and_duplicate_tab_are_distinct_actions()
    {
        var view = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "CavdeckShellWindow.xaml"));
        var add = view.Descendants(Wpf + "Button")
            .Single(node => (string?)node.Attribute(Xaml + "Name") == "NewTabButton");
        Assert.Equal("{Binding NewTabCommand}", (string?)add.Attribute("Command"));
        // Keep the plus action clickable at capacity so it can show a localized warning.
        Assert.Null(add.Attribute("IsEnabled"));
        Assert.Null(add.Attribute("ToolTipService.ShowOnDisabled"));
        Assert.Equal("{Binding NewTabTip}", (string?)add.Attribute("ToolTip"));
        Assert.Contains(view.Descendants(Wpf + "MenuItem"),
            node => (string?)node.Attribute("Click") == "TabDuplicate_Click");
        var overflow = view.Descendants(Wpf + "Button").Single(node =>
            (string?)node.Attribute(Xaml + "Name") == "TabOverflowButton");
        Assert.Equal("Collapsed", (string?)overflow.Attribute("Visibility"));
        Assert.Equal("TabOverflow_Click", (string?)overflow.Attribute("Click"));
        Assert.Contains("Tab_AllTabs", (string?)overflow.Attribute("AutomationProperties.Name"));
    }

    [Fact]
    public void Tab_labels_resize_and_ellipsis_without_losing_context_actions()
    {
        var view = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "CavdeckShellWindow.xaml"));
        var chrome = view.Descendants(Wpf + "Border").Single(node =>
            (string?)node.Attribute(Xaml + "Name") == "TabChrome");
        Assert.Contains("TabItemWidth", (string?)chrome.Attribute("Width"));
        Assert.Null(chrome.Attribute("MinWidth"));

        var label = chrome.Descendants(Wpf + "TextBlock").Single(node =>
            (string?)node.Attribute("Text") == "{Binding Title}");
        Assert.Equal("CharacterEllipsis", (string?)label.Attribute("TextTrimming"));
        Assert.Null(label.Attribute("MaxWidth"));
        Assert.Equal("1", (string?)label.Attribute("Grid.Column"));

        var selectStyle = view.Descendants(Wpf + "Style").Single(node =>
            (string?)node.Attribute(Xaml + "Key") == "TabSelectButton");
        Assert.Contains(selectStyle.Elements(Wpf + "Setter"), node =>
            (string?)node.Attribute("Property") == "HorizontalContentAlignment" &&
            (string?)node.Attribute("Value") == "Stretch");

        var allTabs = view.Descendants(Wpf + "Button").Single(node =>
            (string?)node.Attribute(Xaml + "Name") == "TabOverflowButton");
        Assert.Contains(allTabs.Descendants(Wpf + "Path"), node =>
            ((string?)node.Attribute("Data"))?.Contains("IconChevronDown") == true);
    }

    [Fact]
    public void Brand_name_can_collapse_without_hiding_the_system_menu()
    {
        var style = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "CavdeckWindow.xaml"));
        var title = style.Descendants(Wpf + "TextBlock").Single(node =>
            (string?)node.Attribute(Xaml + "Name") == "TitleText");
        Assert.Contains(title.Descendants(Wpf + "DataTrigger"), trigger =>
            ((string?)trigger.Attribute("Binding"))?.Contains("ShowBrandName") == true &&
            (string?)trigger.Attribute("Value") == "False");
        Assert.Contains(style.Descendants(Wpf + "Button"), button =>
            (string?)button.Attribute(Xaml + "Name") == "SystemMenuButton");
    }

    [Fact]
    public void Bucket_and_folder_context_menus_offer_new_tab_navigation()
    {
        var view = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "CavdeckExplorerView.xaml"));
        Assert.Equal(3, view.Descendants(Wpf + "MenuItem").Count(node =>
            ((string?)node.Attribute("Command"))?.Contains("OpenRowNewTabCommand") == true));
    }

    [Fact]
    public void Unsaved_tab_indicator_uses_the_shared_theme_and_view_model_state()
    {
        var view = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "CavdeckShellWindow.xaml"));
        var dot = view.Descendants(Wpf + "Ellipse").Single(node =>
            ((string?)node.Attribute("Visibility"))?.Contains("HasUnsavedChanges") == true);
        Assert.Equal("{DynamicResource AccentBrush}", (string?)dot.Attribute("Fill"));
        Assert.Contains("Tab_UnsavedChanges", (string?)dot.Attribute("ToolTip"));
    }

    [Fact]
    public void Only_connections_tab_hides_its_close_action()
    {
        var texts = new LocalizationService();
        var tab = new ShellTabViewModel(texts, Guid.NewGuid());
        tab.Update(ShellPage.Connections, 0, true, isOnlyTab: true);
        Assert.False(tab.CanClose);
        tab.Update(ShellPage.Connections, 0, true, isOnlyTab: false);
        Assert.True(tab.CanClose);
        tab.Update(ShellPage.Explorer, 0, true, isOnlyTab: true);
        Assert.True(tab.CanClose);

        var view = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "CavdeckShellWindow.xaml"));
        var closeStyle = view.Descendants(Wpf + "Style").Single(node =>
            (string?)node.Attribute(Xaml + "Key") == "TabCloseButton");
        Assert.Contains(closeStyle.Descendants(Wpf + "DataTrigger"), trigger =>
            (string?)trigger.Attribute("Binding") == "{Binding CanClose}" &&
            (string?)trigger.Attribute("Value") == "False");
    }

    [Fact]
    public void Empty_explorer_tab_has_localized_title_and_action_label()
    {
        var texts = new LocalizationService();
        var tab = new ShellTabViewModel(texts, Guid.NewGuid());
        tab.Update(ShellPage.Explorer, 0, true);
        Assert.Equal(texts["Nav_Explorer"], tab.Title);
        Assert.Equal(texts["Tab_Duplicate"], tab.DuplicateLabel);
        Assert.False(tab.HasUnsavedChanges);
    }
}
