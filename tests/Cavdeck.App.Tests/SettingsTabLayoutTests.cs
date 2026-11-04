using System.Xml.Linq;
using Xunit;

namespace Cavdeck.App.Tests;

public sealed class SettingsTabLayoutTests
{
    private static readonly XNamespace Wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static XDocument View() =>
        XDocument.Load(Path.Combine(AppContext.BaseDirectory, "CavdeckSettings.xaml"));

    private static XElement[] Tabs() =>
        Assert.Single(View().Descendants(Wpf + "TabControl")).Elements(Wpf + "TabItem").ToArray();

    [Fact]
    public void Settings_navigation_has_only_populated_localized_sections()
    {
        var view = View();
        var control = Assert.Single(view.Descendants(Wpf + "TabControl"));
        var tabs = control.Elements(Wpf + "TabItem").ToArray();

        Assert.Equal(new[]
        {
            "{Binding Texts[Settings_TabGeneral]}",
            "{Binding Texts[Settings_TabBrowsing]}",
            "{Binding Texts[Settings_TabTransfers]}",
            "{Binding Texts[Settings_TabShortcuts]}",
            "{Binding Texts[Settings_TabAbout]}"
        }, tabs.Select(tab => (string?)tab.Attribute("Header")).ToArray());
        Assert.Equal("{Binding SelectedTabIndex, Mode=TwoWay}", (string?)control.Attribute("SelectedIndex"));

        Assert.All(tabs, tab =>
        {
            Assert.Equal((string?)tab.Attribute("Header"),
                (string?)tab.Attribute("AutomationProperties.Name"));
            var scroll = Assert.Single(tab.Elements(Wpf + "ScrollViewer"));
            Assert.Equal("Auto", (string?)scroll.Attribute("VerticalScrollBarVisibility"));
            Assert.Equal("Disabled", (string?)scroll.Attribute("HorizontalScrollBarVisibility"));
            Assert.NotEmpty(scroll.Descendants(Wpf + "Border"));
        });

        // A nested read-only ItemsControl is expected for the shortcut reference.
        // Settings navigation itself remains a local TabControl, not a shell workspace tab.
        Assert.Single(view.Descendants(Wpf + "TabControl"));
    }

    [Fact]
    public void Language_dropdown_uses_registry_instead_of_hardcoded_items()
    {
        var combo = Assert.Single(View().Descendants(Wpf + "ComboBox"), element =>
            (string?)element.Attribute(Xaml + "Name") == "LanguageCombo");
        Assert.Equal("{Binding LanguageOptions}", (string?)combo.Attribute("ItemsSource"));
        Assert.Equal("DisplayName", (string?)combo.Attribute("DisplayMemberPath"));
        Assert.Equal("Code", (string?)combo.Attribute("SelectedValuePath"));
        Assert.Empty(combo.Elements(Wpf + "ComboBoxItem"));
    }

    [Theory]
    [InlineData(0, "ThemeChoice", "LanguageChoice", "CloseBehaviorChoice")]
    [InlineData(1, "BrowserViewChoice", "GridDensityChoice", "ShowDetailsPane")]
    [InlineData(2, "DownloadDestinationChoice", "TransferConflictChoice", "Concurrency")]
    [InlineData(3, "GlobalHotkeyEnabled", "GlobalHotkeyButtonLabel", "ShortcutGroups")]
    [InlineData(4, "CheckUpdatesCommand", "CopyDiagnosticCommand", "OpenLogsCommand")]
    public void Original_settings_and_commands_remain_in_the_correct_section(
        int section, string first, string second, string third)
    {
        var tab = Tabs()[section];
        var markup = string.Join(" ", tab.DescendantsAndSelf()
            .Attributes().Select(attribute => attribute.Value));

        Assert.Contains(first, markup);
        Assert.Contains(second, markup);
        Assert.Contains(third, markup);
    }

    [Fact]
    public void Navigation_stays_fixed_above_the_scrolling_content()
    {
        var view = View();
        var template = view.Descendants(Wpf + "Style").Single(style =>
            (string?)style.Attribute(Xaml + "Key") == "SettingsTabControl")
            .Descendants(Wpf + "ControlTemplate").Single();

        var rows = Assert.Single(template.Descendants(Wpf + "Grid.RowDefinitions"));
        Assert.Equal(new[] { "Auto", "*" },
            rows.Elements(Wpf + "RowDefinition")
                .Select(row => (string?)row.Attribute("Height")).ToArray());

        var headerScroll = Assert.Single(template.Descendants(Wpf + "ScrollViewer"));
        Assert.Equal("Hidden", (string?)headerScroll.Attribute("HorizontalScrollBarVisibility"));
        Assert.Contains(headerScroll.Descendants(Wpf + "ItemsPresenter"),
            _ => true);
        var itemsPanel = Assert.Single(View().Descendants(Wpf + "Style").Single(style =>
                (string?)style.Attribute(Xaml + "Key") == "SettingsTabControl")
            .Descendants(Wpf + "ItemsPanelTemplate"));
        Assert.Equal("Horizontal", (string?)Assert.Single(itemsPanel.Elements(Wpf + "StackPanel"))
            .Attribute("Orientation"));
        Assert.Empty(template.Descendants(Wpf + "TabPanel"));

        Assert.Contains(template.Descendants(Wpf + "ContentPresenter"),
            presenter => (string?)presenter.Attribute("ContentSource") == "SelectedContent" &&
                (string?)presenter.Attribute("Grid.Row") == "1");
    }

    [Fact]
    public void Selected_header_touches_divider_without_competing_pill_or_spacing()
    {
        var view = View();
        var style = view.Descendants(Wpf + "Style").Single(element =>
            (string?)element.Attribute(Xaml + "Key") == "SettingsTabItem");
        var setters = style.Elements(Wpf + "Setter").ToDictionary(
            element => (string)element.Attribute("Property")!,
            element => (string)element.Attribute("Value")!);
        Assert.Equal("36", setters["Height"]);
        Assert.Equal("14,4,14,0", setters["Padding"]);
        Assert.Equal("Medium", setters["FontWeight"]);
        Assert.Equal("{DynamicResource MutedTextBrush}", setters["Foreground"]);

        var template = Assert.Single(style.Descendants(Wpf + "ControlTemplate"));
        var indicator = Assert.Single(template.Descendants(Wpf + "Border"), border =>
            (string?)border.Attribute(Xaml + "Name") == "SelectionIndicator");
        Assert.Equal("2", (string?)indicator.Attribute("Height"));
        Assert.Equal("Bottom", (string?)indicator.Attribute("VerticalAlignment"));
        Assert.Equal("14,0,14,0", (string?)indicator.Attribute("Margin"));
        Assert.Equal("{DynamicResource AccentBrush}", (string?)indicator.Attribute("Background"));
        Assert.Equal("Collapsed", (string?)indicator.Attribute("Visibility"));

        var hoverSurface = Assert.Single(template.Descendants(Wpf + "Border"), border =>
            (string?)border.Attribute(Xaml + "Name") == "HoverSurface");
        Assert.Equal("0,0,0,2", (string?)hoverSurface.Attribute("Margin"));
        var hover = Assert.Single(template.Descendants(Wpf + "Trigger"), trigger =>
            (string?)trigger.Attribute("Property") == "IsMouseOver");
        var selected = Assert.Single(template.Descendants(Wpf + "Trigger"), trigger =>
            (string?)trigger.Attribute("Property") == "IsSelected");
        Assert.Contains(hover.Elements(Wpf + "Setter"), setter =>
            (string?)setter.Attribute("TargetName") == "HoverSurface" &&
            (string?)setter.Attribute("Value") == "{DynamicResource HoverBrush}");
        Assert.Contains(selected.Elements(Wpf + "Setter"), setter =>
            (string?)setter.Attribute("TargetName") == "SelectionIndicator" &&
            (string?)setter.Attribute("Value") == "Visible");
        Assert.Contains(selected.Elements(Wpf + "Setter"), setter =>
            (string?)setter.Attribute("TargetName") == "HoverSurface" &&
            (string?)setter.Attribute("Value") == "Transparent");
        Assert.DoesNotContain(selected.Elements(Wpf + "Setter"), setter =>
            (string?)setter.Attribute("Property") == "FontWeight");

        var controlTemplate = view.Descendants(Wpf + "Style").Single(element =>
                (string?)element.Attribute(Xaml + "Key") == "SettingsTabControl")
            .Descendants(Wpf + "ControlTemplate").Single();
        var rule = Assert.Single(controlTemplate.Descendants(Wpf + "Border"), border =>
            (string?)border.Attribute(Xaml + "Name") == "HeaderRule");
        Assert.Equal("{DynamicResource BorderBrush}", (string?)rule.Attribute("Background"));
        Assert.Equal("1", (string?)rule.Attribute("Height"));
        Assert.Equal("Bottom", (string?)rule.Attribute("VerticalAlignment"));

        // Rule and selected underline share the same header grid bottom edge.
        var headerGrid = Assert.IsType<XElement>(rule.Parent);
        Assert.Equal(Wpf + "Grid", headerGrid.Name);
        Assert.Equal("0", (string?)headerGrid.Attribute("Grid.Row"));
        Assert.Contains(headerGrid.Elements(Wpf + "ScrollViewer"), scroll =>
            (string?)scroll.Attribute("HorizontalScrollBarVisibility") == "Hidden");
        Assert.DoesNotContain(headerGrid.Descendants(Wpf + "Border"), border =>
            (string?)border.Attribute("BorderThickness") == "0,0,0,1");
    }

    [Fact]
    public void Keyboard_focus_is_visible_without_shifting_the_tab_header()
    {
        var style = View().Descendants(Wpf + "Style").Single(element =>
            (string?)element.Attribute(Xaml + "Key") == "SettingsTabItem");
        var template = Assert.Single(style.Descendants(Wpf + "ControlTemplate"));
        var ring = Assert.Single(template.Descendants(Wpf + "Border"), border =>
            (string?)border.Attribute(Xaml + "Name") == "FocusRing");
        Assert.Equal("2,2,2,4", (string?)ring.Attribute("Margin"));
        Assert.Equal("{DynamicResource FocusBorderBrush}", (string?)ring.Attribute("BorderBrush"));
        Assert.Equal("Collapsed", (string?)ring.Attribute("Visibility"));
        Assert.Contains(template.Descendants(Wpf + "DataTrigger"), trigger =>
            ((string?)trigger.Attribute("Binding"))?.Contains("FocusCue.Visible") == true);
    }

    [Fact]
    public void Save_failure_feedback_is_visible_on_every_section()
    {
        var error = Assert.Single(View().Descendants(Wpf + "TextBlock"), node =>
            (string?)node.Attribute("Text") == "{Binding SaveError}");
        Assert.DoesNotContain(error.Ancestors(), node => node.Name == Wpf + "TabItem");
        Assert.Contains("StringVis", (string?)error.Attribute("Visibility"));
        Assert.Equal("Assertive", (string?)error.Attribute("AutomationProperties.LiveSetting"));
    }
}
