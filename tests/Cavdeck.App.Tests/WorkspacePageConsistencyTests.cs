using System.Xml.Linq;
using Xunit;

namespace Cavdeck.App.Tests;

public sealed class WorkspacePageConsistencyTests
{
    private static readonly XNamespace Wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static XDocument Load(string file) =>
        XDocument.Load(Path.Combine(AppContext.BaseDirectory, file));

    [Theory]
    [InlineData("CavdeckConnectionsView.xaml")]
    [InlineData("CavdeckPlacesView.xaml")]
    [InlineData("CavdeckTransfersView.xaml")]
    public void Workspaces_share_full_bleed_toolbars_and_empty_states(string file)
    {
        var view = Load(file);
        // A shared full-bleed toolbar precedes the content for every workspace.
        var content = view.Root!.Elements(Wpf + "Grid").Single();
        Assert.Null(content.Attribute("Margin"));
        var host = content.Descendants(Wpf + "StackPanel").Single(element =>
            (string?)element.Attribute("Style") == "{StaticResource PageEmptyHost}");
        Assert.Contains(host.Descendants(Wpf + "Border"), element =>
            (string?)element.Attribute("Style") == "{StaticResource PageEmptyIcon}");
        Assert.Contains(host.Descendants(Wpf + "TextBlock"), element =>
            (string?)element.Attribute("Style") == "{StaticResource PageEmptyTitle}");
        Assert.Contains(host.Descendants(Wpf + "TextBlock"), element =>
            (string?)element.Attribute("Style") == "{StaticResource PageEmptyDescription}");
        Assert.Contains(content.Descendants(Wpf + "Border"), element =>
            (string?)element.Attribute("Style") == "{StaticResource PageToolbar}");
    }

    [Fact]
    public void Place_rows_keep_explorer_keyboard_actions_and_compact_columns()
    {
        var places = Load("CavdeckPlacesView.xaml");
        var list = places.Descendants(Wpf + "DataGrid").Single();
        Assert.Equal("PlacesGrid_SizeChanged", (string?)list.Attribute("SizeChanged"));
        Assert.Equal("Single", (string?)list.Attribute("SelectionMode"));
        Assert.Equal("PlacesGrid_PreviewKeyDown", (string?)list.Attribute("PreviewKeyDown"));
        var commands = list.Descendants(Wpf + "MenuItem")
            .Select(item => (string?)item.Attribute("Command") ?? "").ToArray();
        Assert.Contains(commands, command => command.Contains("OpenNewTabCommand"));
        Assert.Contains(commands, command => command.Contains("RemoveCommand"));
        Assert.Contains(places.Descendants(Wpf + "Button"), button =>
            ((string?)button.Attribute("Command"))?.Contains("BrowseCommand") == true &&
            button.Descendants(Wpf + "TextBlock").Any(t =>
                ((string?)t.Attribute("Text"))?.Contains("BrowseLabel") == true));
    }

    [Fact]
    public void Transfers_have_one_virtualized_scroll_surface_with_both_row_types()
    {
        var transfers = Load("CavdeckTransfersView.xaml");
        Assert.Empty(transfers.Descendants(Wpf + "ScrollViewer"));
        var list = transfers.Descendants(Wpf + "ListBox").Single();
        Assert.Contains("DisplayItems", (string?)list.Attribute("ItemsSource"));
        Assert.Equal("True", (string?)list.Attribute("VirtualizingPanel.IsVirtualizing"));
        Assert.Equal("Recycling", (string?)list.Attribute("VirtualizingPanel.VirtualizationMode"));
        var templates = list.Descendants(Wpf + "DataTemplate")
            .Select(template => (string?)template.Attribute("DataType") ?? "").ToArray();
        Assert.Contains(templates, type => type.Contains("CrossBatchRowViewModel"));
        Assert.Contains(templates, type => type.Contains("TransferRowViewModel"));
        Assert.Contains(list.Descendants(Wpf + "ProgressBar"), bar =>
            ((string?)bar.Attribute("Value"))?.Contains("Mode=OneWay") == true);
        foreach (var action in new[] { "PauseCommand", "ResumeCommand", "CancelCommand", "RetryCommand", "RetryBatchCommand" })
            Assert.Contains(list.Descendants(Wpf + "Button"), button =>
                ((string?)button.Attribute("Command"))?.Contains(action) == true);
    }

    [Fact]
    public void Transfers_keep_compact_accessible_actions_and_hide_unknown_percentages()
    {
        var transfers = Load("CavdeckTransfersView.xaml");
        var list = transfers.Descendants(Wpf + "ListBox").Single();
        var row = list.Descendants(Wpf + "DataTemplate").Single(template =>
            ((string?)template.Attribute("DataType"))?.Contains("TransferRowViewModel") == true);
        Assert.Contains(row.Descendants(Wpf + "TextBlock"), text =>
            ((string?)text.Attribute("Visibility"))?.Contains("ShowPercentage") == true);
        foreach (var action in new[] { "PauseCommand", "ResumeCommand", "CancelCommand" })
            Assert.Contains(row.Descendants(Wpf + "Button"), button =>
                ((string?)button.Attribute("Command"))?.Contains(action) == true &&
                button.Attribute("ToolTip") is not null &&
                button.Attribute("AutomationProperties.Name") is not null);
        Assert.Contains(row.Descendants(Wpf + "DataTrigger"), trigger =>
            ((string?)trigger.Attribute("Binding"))?.Contains("IsFailure") == true);
    }

    [Theory]
    [InlineData("Colors.Light.xaml")]
    [InlineData("Colors.Dark.xaml")]
    public void Transfers_use_theme_aware_colors_for_file_names_and_icons(string themeFile)
    {
        var colors = Load(themeFile);
        foreach (var brush in new[] { "PrimaryTextBrush", "MutedTextBrush" })
            Assert.Contains(colors.Descendants(Wpf + "SolidColorBrush"), element =>
                (string?)element.Attribute(Xaml + "Key") == brush);

        var transfers = Load("CavdeckTransfersView.xaml");
        var list = transfers.Descendants(Wpf + "ListBox").Single();
        Assert.Equal("{DynamicResource PrimaryTextBrush}", (string?)list.Attribute("Foreground"));

        var fileTemplate = list.Descendants(Wpf + "DataTemplate").Single(template =>
            ((string?)template.Attribute("DataType"))?.Contains("TransferRowViewModel") == true);

        var name = fileTemplate.Descendants(Wpf + "TextBlock").Single(text =>
            ((string?)text.Attribute("Text"))?.Contains("Binding Name") == true);
        Assert.Equal("{DynamicResource PrimaryTextBrush}", (string?)name.Attribute("Foreground"));

        // IconGlyph normally inherits a Control.Foreground. In this row the
        // closest control is the ListBox, so pin the standalone glyph to a
        // semantic brush that updates when the theme changes.
        var glyph = fileTemplate.Descendants(Wpf + "Path").Single(path =>
            path.Descendants(Wpf + "Setter").Any(setter =>
                (string?)setter.Attribute("Property") == "Data" &&
                (string?)setter.Attribute("Value") == "{StaticResource IconTransfers}"));
        // The default stroke now lives in the icon's Style, where direction-specific
        // geometries can change while the themed muted color remains stable.
        Assert.Contains(glyph.Descendants(Wpf + "Setter"), setter =>
            (string?)setter.Attribute("Property") == "Stroke" &&
            (string?)setter.Attribute("Value") == "{DynamicResource MutedTextBrush}");

        // The state text remains theme-aware, including the failure override.
        var status = fileTemplate.Descendants(Wpf + "TextBlock").Single(text =>
            ((string?)text.Attribute("Text"))?.Contains("StatusText") == true);
        var style = status.Element(Wpf + "TextBlock.Style")?.Element(Wpf + "Style");
        Assert.NotNull(style);
        Assert.Contains(style.Elements(Wpf + "Setter"), setter =>
            (string?)setter.Attribute("Property") == "Foreground" &&
            (string?)setter.Attribute("Value") == "{DynamicResource MutedTextBrush}");
        Assert.Contains(style.Descendants(Wpf + "Setter"), setter =>
            (string?)setter.Attribute("Property") == "Foreground" &&
            (string?)setter.Attribute("Value") == "{DynamicResource DangerTextBrush}");
    }

    [Fact]
    public void Transfers_offer_localized_status_filters_and_accessible_expansion_without_nested_scrolling()
    {
        var transfers = Load("CavdeckTransfersView.xaml");
        Assert.Empty(transfers.Descendants(Wpf + "ScrollViewer"));
        var list = transfers.Descendants(Wpf + "ListBox").Single();
        Assert.Equal("True", (string?)list.Attribute("VirtualizingPanel.IsVirtualizing"));
        Assert.Equal("Recycling", (string?)list.Attribute("VirtualizingPanel.VirtualizationMode"));

        var filters = transfers.Descendants(Wpf + "ComboBox")
            .Where(combo => ((string?)combo.Attribute("ItemsSource"))?.Contains("Filters") == true)
            .ToArray();
        Assert.Equal(2, filters.Length);
        foreach (var filter in filters)
        {
            Assert.Equal("Label", (string?)filter.Attribute("DisplayMemberPath"));
            Assert.Contains("Selected", (string?)filter.Attribute("SelectedItem"));
            Assert.Contains("Transfer_FilterLabel", (string?)filter.Attribute("AutomationProperties.Name"));
        }

        foreach (var action in new[] { "ToggleBatchCommand", "ToggleDetailsCommand" })
            Assert.Contains(list.Descendants(Wpf + "Button"), button =>
                ((string?)button.Attribute("Command"))?.Contains(action) == true &&
                button.Attribute("ToolTip") is not null &&
                button.Attribute("AutomationProperties.Name") is not null);

        Assert.Contains(list.Descendants(Wpf + "Border"), border =>
            ((string?)border.Attribute("Margin"))?.Contains("ChildMargin") == true);
        Assert.Contains(list.Descendants(Wpf + "TextBlock"), block =>
            ((string?)block.Attribute("Text"))?.Contains("RouteText") == true);
        Assert.Contains(list.Descendants(Wpf + "TextBlock"), block =>
            ((string?)block.Attribute("Text"))?.Contains("SourcePath") == true);
        Assert.Contains(list.Descendants(Wpf + "TextBlock"), block =>
            ((string?)block.Attribute("Text"))?.Contains("DestinationPath") == true);
    }

    [Fact]
    public void Transfer_history_and_current_tabs_have_distinct_accessible_selection()
    {
        var transfers = Load("CavdeckTransfersView.xaml");
        Assert.Contains(transfers.Descendants(Wpf + "DataTrigger"), trigger =>
            ((string?)trigger.Attribute("Binding"))?.Contains("Transfers.ShowHistory") == true &&
            trigger.Descendants(Wpf + "Setter").Any(setter =>
                (string?)setter.Attribute("Property") == "Background"));
        Assert.Contains(transfers.Descendants(Wpf + "Trigger"), trigger =>
            (string?)trigger.Attribute("Property") == "IsKeyboardFocusWithin");
        Assert.Contains(transfers.Descendants(Wpf + "TextBlock"), t =>
            ((string?)t.Attribute("Text"))?.Contains("Transfers.EmptyTitle") == true);
    }

    [Fact]
    public void Explorer_path_and_actions_use_the_same_header_and_toolbar_as_other_pages()
    {
        var explorer = Load("CavdeckExplorerView.xaml");
        var chrome = explorer.Root!.Elements(Wpf + "Grid").Single()
            .Elements(Wpf + "Grid")
            .Single(element => (string?)element.Attribute("Grid.Row") == "0");

        var borders = chrome.Elements(Wpf + "Border").ToArray();
        Assert.Equal(2, borders.Length);
        Assert.Equal("{StaticResource WorkspaceHeader}", (string?)borders[0].Attribute("Style"));
        Assert.Equal("{StaticResource WorkspaceToolbar}", (string?)borders[1].Attribute("Style"));

        var toolbar = borders[1].Descendants(Wpf + "Grid")
            .Single(element => (string?)element.Attribute(Xaml + "Name") == "ToolbarRow");
        Assert.Equal("32", (string?)toolbar.Attribute("MinHeight"));
        Assert.Equal("ToolbarRow_SizeChanged", (string?)toolbar.Attribute("SizeChanged"));

        var shell = Load("CavdeckShellWindow.xaml");
        Assert.Contains(shell.Descendants(Wpf + "Border"), element =>
            (string?)element.Attribute("Style") == "{StaticResource WorkspaceHeader}" &&
            (string?)element.Attribute("Grid.Row") == "0");

        var surfaces = Load("CavdeckSurfaces.xaml");
        var header = surfaces.Descendants(Wpf + "Style").Single(element =>
            (string?)element.Attribute(Xaml + "Key") == "WorkspaceHeader");
        var actions = surfaces.Descendants(Wpf + "Style").Single(element =>
            (string?)element.Attribute(Xaml + "Key") == "WorkspaceToolbar");
        Assert.Contains(header.Elements(Wpf + "Setter"), element =>
            (string?)element.Attribute("Property") == "Height" &&
            (string?)element.Attribute("Value") == "48");
        Assert.Contains(actions.Elements(Wpf + "Setter"), element =>
            (string?)element.Attribute("Property") == "MinHeight" &&
            (string?)element.Attribute("Value") == "48");
    }

    [Theory]
    [InlineData("ShowEmptyConnection")]
    [InlineData("ShowEmptyBuckets")]
    [InlineData("ShowEmptyObjects")]
    [InlineData("ShowNoMatches")]
    public void Explorer_empty_states_share_the_common_visual_language(string visibility)
    {
        var explorer = Load("CavdeckExplorerView.xaml");
        var area = explorer.Descendants().Single(element =>
            ((string?)element.Attribute("Visibility"))?.Contains("Explorer." + visibility) == true);
        var host = area.Name == Wpf + "StackPanel" ? area
            : area.Descendants(Wpf + "StackPanel").Single(element =>
                (string?)element.Attribute("Style") == "{StaticResource PageEmptyHost}");
        Assert.Equal("{StaticResource PageEmptyHost}", (string?)host.Attribute("Style"));
        Assert.Contains(host.Descendants(Wpf + "Border"), element =>
            (string?)element.Attribute("Style") == "{StaticResource PageEmptyIcon}");
        Assert.Contains(host.Descendants(Wpf + "TextBlock"), element =>
            (string?)element.Attribute("Style") == "{StaticResource PageEmptyTitle}");
        Assert.Contains(host.Descendants(Wpf + "TextBlock"), element =>
            (string?)element.Attribute("Style") == "{StaticResource PageEmptyDescription}");
    }
}
