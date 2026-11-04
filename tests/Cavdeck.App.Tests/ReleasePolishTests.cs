using System.ComponentModel;
using System.Xml.Linq;
using Cavdeck.App.Controls;
using Cavdeck.App.Features.Explorer;
using Xunit;

namespace Cavdeck.App.Tests;

public sealed class ReleasePolishTests
{
    private static readonly XNamespace Wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static XDocument View(string file) =>
        XDocument.Load(Path.Combine(AppContext.BaseDirectory, file));

    [Fact]
    public void File_columns_prioritize_the_name_across_responsive_breakpoints()
    {
        Assert.Equal(new ExplorerVisibleColumns(false, false, false), ExplorerColumnLayout.ForWidth(0));
        Assert.Equal(new ExplorerVisibleColumns(false, false, false), ExplorerColumnLayout.ForWidth(359));
        Assert.Equal(new ExplorerVisibleColumns(true, false, false), ExplorerColumnLayout.ForWidth(360));
        Assert.Equal(new ExplorerVisibleColumns(true, false, false), ExplorerColumnLayout.ForWidth(519));
        Assert.Equal(new ExplorerVisibleColumns(true, false, true), ExplorerColumnLayout.ForWidth(520));
        Assert.Equal(new ExplorerVisibleColumns(true, false, true), ExplorerColumnLayout.ForWidth(759));
        Assert.Equal(new ExplorerVisibleColumns(true, true, true), ExplorerColumnLayout.ForWidth(760));
        Assert.Equal(new ExplorerVisibleColumns(false, false, false), ExplorerColumnLayout.ForWidth(double.NaN));
    }

    [Fact]
    public void Initial_sort_direction_is_field_appropriate_and_clicks_toggle()
    {
        Assert.False(ExplorerSortDirection.NextDescending("Name", null));
        Assert.True(ExplorerSortDirection.NextDescending("SizeBytes", null));
        Assert.True(ExplorerSortDirection.NextDescending("Modified", null));
        Assert.True(ExplorerSortDirection.NextDescending("Name", ListSortDirection.Ascending));
        Assert.False(ExplorerSortDirection.NextDescending("Modified", ListSortDirection.Descending));
    }

    [Fact]
    public void Danger_dialogs_never_default_to_primary_action()
    {
        Assert.False(DialogInteractionPolicy.IsPrimaryDefault(DialogTone.Danger));
        Assert.True(DialogInteractionPolicy.IsPrimaryDefault(DialogTone.Neutral));

        var dialog = View("CavdeckAppDialog.xaml");
        var primary = dialog.Descendants(Wpf + "Button")
            .Single(x => (string?)x.Attribute(Xaml + "Name") == "PrimaryAction");
        Assert.Null(primary.Attribute("IsDefault"));
    }

    [Fact]
    public void Explorer_keeps_responsive_grid_and_contextual_keyboard_shortcuts()
    {
        var view = View("CavdeckExplorerView.xaml");
        var grid = view.Descendants(Wpf + "DataGrid")
            .Single(x => (string?)x.Attribute(Xaml + "Name") == "ObjectsGrid");
        Assert.Equal("ObjectsGrid_SizeChanged", (string?)grid.Attribute("SizeChanged"));

        var expected = new Dictionary<string, string>
        {
            ["Explorer_Copy"] = "Ctrl+C",
            ["Explorer_Cut"] = "Ctrl+X",
            ["Explorer_Paste"] = "Ctrl+V",
            ["Explorer_Rename"] = "F2",
            ["Explorer_Delete"] = "Del"
        };
        foreach (var (key, gesture) in expected)
        {
            Assert.Contains(view.Descendants(Wpf + "MenuItem"), item =>
                ((string?)item.Attribute("Header"))?.Contains($"Texts[{key}]") == true &&
                (string?)item.Attribute("InputGestureText") == gesture);
        }

        Assert.DoesNotContain(view.Descendants(Wpf + "StackPanel"),
            stack => (string?)stack.Attribute("Width") == "420");
    }

    [Fact]
    public void Connection_cards_keep_one_primary_action_and_accessible_overflow()
    {
        var view = View("CavdeckConnectionsView.xaml");
        var more = view.Descendants(Wpf + "Button")
            .Single(x => (string?)x.Attribute("Click") == "ConnectionMore_Click");
        Assert.NotNull(more.Attribute("AutomationProperties.Name"));
        var menu = more.Element(Wpf + "Button.ContextMenu")?.Element(Wpf + "ContextMenu");
        Assert.NotNull(menu);
        Assert.Equal("Bottom", (string?)menu.Attribute("Placement"));
        var keys = menu.Descendants(Wpf + "MenuItem")
            .Select(item => (string?)item.Attribute("Header")).ToArray();
        Assert.Equal(3, keys.Length);
        foreach (var name in new[] { "Connection_Test", "Connection_Edit", "Connection_Delete" })
            Assert.Contains(keys, header => header?.Contains(name, StringComparison.Ordinal) == true);
        Assert.Contains(menu.Descendants(Wpf + "MenuItem"), item =>
            ((string?)item.Attribute("Header"))?.Contains("Connection_Delete") == true
            && (string?)item.Attribute(XName.Get("MenuAssist.IsDanger", "clr-namespace:Cavdeck.App.Controls")) == "True");
    }

    [Fact]
    public void Transfer_tabs_and_focus_and_failure_status_are_distinguishable()
    {
        var view = View("CavdeckTransfersView.xaml");
        Assert.Contains(view.Descendants(Wpf + "DataTrigger"), t =>
            ((string?)t.Attribute("Binding"))?.Contains("Transfers.ShowHistory") == true
            && t.Elements(Wpf + "Setter").Any(s => (string?)s.Attribute("Property") == "Background"));
        Assert.Contains(view.Descendants(Wpf + "Trigger"), t =>
            (string?)t.Attribute("Property") == "IsKeyboardFocusWithin" &&
            t.Elements(Wpf + "Setter").Any(s => (string?)s.Attribute("TargetName") == "FocusOutline"));
        Assert.Contains(view.Descendants(Wpf + "DataTrigger"), t =>
            ((string?)t.Attribute("Binding"))?.Contains("IsFailure") == true
            && t.Elements(Wpf + "Setter").Any(s => ((string?)s.Attribute("Value"))?.Contains("DangerTextBrush") == true));
    }

    [Fact]
    public void Hidden_tab_close_action_reappears_on_keyboard_focus()
    {
        var view = View("CavdeckShellWindow.xaml");
        var style = view.Descendants(Wpf + "Style").Single(x =>
            (string?)x.Attribute(Xaml + "Key") == "TabCloseButton");
        Assert.Contains(style.Descendants(Wpf + "Trigger"), trigger =>
            (string?)trigger.Attribute("Property") == "IsKeyboardFocused"
            && trigger.Descendants(Wpf + "Setter").Any(s =>
                (string?)s.Attribute("Property") == "Opacity" && (string?)s.Attribute("Value") == "1"));
    }

    [Fact]
    public void Copy_destination_browse_error_has_local_retry_button()
    {
        var picker = View("CavdeckCopyPicker.xaml");
        var retry = picker.Descendants(Wpf + "Button")
            .Single(x => (string?)x.Attribute(Xaml + "Name") == "RetryButton");
        Assert.Equal("Retry_Click", (string?)retry.Attribute("Click"));
        Assert.Equal("Collapsed", (string?)retry.Attribute("Visibility"));
    }
}
