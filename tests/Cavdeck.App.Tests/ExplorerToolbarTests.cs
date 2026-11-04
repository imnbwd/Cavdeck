using System.Xml.Linq;
using Xunit;

namespace Cavdeck.App.Tests;

public sealed class ExplorerToolbarTests
{
    private static readonly XNamespace Wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void Toolbar_stays_single_row_with_compact_32_dip_controls()
    {
        var toolbar = Toolbar();
        Assert.Equal("32", (string?)toolbar.Attribute("MinHeight"));
        Assert.Null(toolbar.Attribute("Margin"));
        Assert.Equal("ToolbarRow_SizeChanged", (string?)toolbar.Attribute("SizeChanged"));
        Assert.Empty(toolbar.Descendants(Wpf + "WrapPanel"));
        Assert.Contains(toolbar.Descendants(Wpf + "StackPanel"),
            element => ((string?)element.Attribute("Visibility"))?.Contains("ToolbarShowDefaultCommands") == true);

        var view = Load("CavdeckExplorerView.xaml");
        var style = view.Descendants(Wpf + "Style")
            .Single(element => (string?)element.Attribute(Xaml + "Key") == "ExplorerToolbarButton");
        Assert.Contains(style.Elements(Wpf + "Setter"),
            setter => (string?)setter.Attribute("Property") == "Height"
                      && (string?)setter.Attribute("Value") == "32");
    }

    [Fact]
    public void Loading_keeps_primary_actions_in_place_but_disables_them()
    {
        var toolbar = Toolbar();
        var actionGroups = toolbar.Elements(Wpf + "StackPanel")
            .Where(panel => (string?)panel.Attribute("Grid.Column") == "0")
            .ToArray();
        Assert.Equal(2, actionGroups.Length);
        Assert.All(actionGroups, group => Assert.DoesNotContain(
            group.Element(Wpf + "StackPanel.Style")!.Descendants(Wpf + "DataTrigger"),
            trigger => ((string?)trigger.Attribute("Binding"))?.Contains("Explorer.IsBusy") == true));

        var upload = Named(toolbar, "Border", "UploadSplitButton");
        Assert.Contains("Explorer.ShowWriteActions", (string?)upload.Attribute("Visibility"));
        Assert.Contains("Explorer.CanWrite", (string?)upload.Attribute("IsEnabled"));
        // The split button's children inherit its disabled state.
        Assert.Contains(upload.Descendants(Wpf + "Button"),
            button => (string?)button.Attribute("Click") == "UploadFiles_Click");
        Assert.Contains(upload.Descendants(Wpf + "Button"),
            button => (string?)button.Attribute(Xaml + "Name") == "UploadOptionsButton");

        var newButton = Named(toolbar, "Button", "NewButton");
        Assert.Contains("Explorer.ShowWriteActions", (string?)newButton.Attribute("Visibility"));
        Assert.Contains("Explorer.CanWrite", (string?)newButton.Attribute("IsEnabled"));

        var more = Named(toolbar, "Button", "MoreButton");
        var selectionMore = Named(toolbar, "Button", "SelectionMoreButton");
        Assert.Contains("Explorer.CanFavorite", (string?)more.Attribute("IsEnabled"));
        Assert.Contains("Explorer.CanFavorite", (string?)selectionMore.Attribute("IsEnabled"));

        Assert.Contains(toolbar.Elements(Wpf + "StackPanel"),
            panel => (string?)panel.Attribute("Grid.Column") == "1");
    }

    [Fact]
    public void Select_loaded_is_only_in_more_and_paste_keeps_a_conditional_separator()
    {
        var toolbar = Toolbar();
        var extras = Assert.Single(toolbar.Descendants(Wpf + "StackPanel")
            , panel => ((string?)panel.Attribute("Visibility"))?
                .Contains("Explorer.ToolbarShowDefaultCommands") == true);

        // The separator only exists within the Paste action group, not as an orphan.
        var separator = Assert.Single(extras.Elements(Wpf + "Border"));
        Assert.Null(separator.Attribute("Visibility"));
        Assert.DoesNotContain(extras.Elements(Wpf + "Button"),
            button => ((string?)button.Attribute("Content"))?.Contains("Explorer_SelectLoaded") == true);

        var paste = Assert.Single(extras.Elements(Wpf + "Button"));
        Assert.Contains("Explorer.ShowPaste", (string?)paste.Attribute("Visibility"));
        Assert.Contains("Explorer.CanPaste", (string?)paste.Attribute("IsEnabled"));

        var more = Named(toolbar, "Button", "MoreButton");
        var menu = more.Element(Wpf + "Button.ContextMenu")!.Element(Wpf + "ContextMenu")!;
        var select = Assert.Single(menu.Elements(Wpf + "MenuItem")
            , item => ((string?)item.Attribute("Header"))?.Contains("Explorer_SelectLoaded") == true);
        Assert.Equal("SelectAll_Click", (string?)select.Attribute("Click"));
        Assert.Contains("Explorer.CanSelectAll", (string?)select.Attribute("IsEnabled"));
    }

    [Fact]
    public void Compact_search_preserves_search_scope_and_view_switching()
    {
        var toolbar = Toolbar();
        var compactSearch = Named(toolbar, "Button", "SearchScopeButton");
        var textInput = Named(toolbar, "TextBox", "SearchInput");
        Assert.NotNull(compactSearch.Element(Wpf + "Button.ContextMenu"));
        Assert.Contains("Explorer.Filter", (string?)textInput.Attribute("Text"));
        Assert.Contains(toolbar.Descendants(Wpf + "Button"),
            button => (string?)button.Attribute("Click") == "ToolbarSearch_Click"
                && ((string?)button.Attribute("Visibility"))?.Contains("ToolbarShowSearchButton") == true);
        Assert.Contains(toolbar.Descendants(Wpf + "Grid"),
            grid => ((string?)grid.Attribute("Visibility"))?.Contains("ToolbarShowSearchInput") == true);
        Assert.Contains(toolbar.Descendants(Wpf + "StackPanel"),
            panel => ((string?)panel.Attribute("Visibility"))?.Contains("ToolbarShowActions") == true);
    }

    [Fact]
    public void Upload_split_button_exposes_files_and_folders()
    {
        var toolbar = Toolbar();
        var uploadOptions = Named(toolbar, "Button", "UploadOptionsButton");
        Assert.Equal("UploadOptions_Click", (string?)uploadOptions.Attribute("Click"));
        var menu = uploadOptions.Element(Wpf + "Button.ContextMenu")?.Element(Wpf + "ContextMenu");
        Assert.NotNull(menu);
        Assert.Contains(menu.Elements(Wpf + "MenuItem"),
            item => ((string?)item.Attribute("Header"))?.Contains("Transfer_UploadFolder") == true
                && (string?)item.Attribute("Click") == "UploadFolder_Click");
        Assert.Contains(toolbar.Descendants(Wpf + "Button"),
            button => (string?)button.Attribute("Click") == "UploadFiles_Click");
    }

    [Fact]
    public void Toolbar_dropdown_menus_align_to_the_button_instead_of_the_pointer()
    {
        var toolbar = Toolbar();
        foreach (var name in new[]
                 {
                     "UploadOptionsButton", "NewButton", "MoreButton",
                     "SelectionMoreButton", "SearchScopeButton"
                 })
        {
            var button = Named(toolbar, "Button", name);
            var menu = button.Element(Wpf + "Button.ContextMenu")?.Element(Wpf + "ContextMenu");
            Assert.NotNull(menu);
            Assert.Equal("Bottom", (string?)menu.Attribute("Placement"));
            // Cancel the four-DIP outer inset in the shared ContextMenu template.
            Assert.Equal("-4", (string?)menu.Attribute("HorizontalOffset"));
            Assert.Equal("-4", (string?)menu.Attribute("VerticalOffset"));
        }

        // The upload arrow lives on the right; its menu must start at the main button's left edge.
        var uploadSplit = Named(toolbar, "Border", "UploadSplitButton");
        Assert.Contains(uploadSplit.Descendants(Wpf + "Button"),
            button => (string?)button.Attribute("Click") == "UploadFiles_Click");
        Assert.Contains(uploadSplit.Descendants(Wpf + "Button"),
            button => (string?)button.Attribute(Xaml + "Name") == "UploadOptionsButton");
    }

    [Fact]
    public void Selected_items_keep_essential_commands_in_overflow()
    {
        var toolbar = Toolbar();
        var more = Named(toolbar, "Button", "SelectionMoreButton");
        var menu = more.Element(Wpf + "Button.ContextMenu")?.Element(Wpf + "ContextMenu");
        Assert.NotNull(menu);
        var headers = menu.Elements(Wpf + "MenuItem")
            .Select(item => (string?)item.Attribute("Header") ?? "")
            .ToArray();
        foreach (var key in new[]
                 {
                     "Explorer_Cut", "Explorer_Paste", "Explorer_Rename", "Share_Menu",
                     "Explorer_CopyKey", "Explorer_CopyUri", "Transfer_Upload",
                     "Transfer_UploadFolder", "Explorer_NewFolder", "Explorer_NewTextFile",
                     "Explorer_Delete"
                 })
            Assert.Contains(headers, header => header.Contains(key, StringComparison.Ordinal));

        var icons = Load("FileIconGeometries.xaml").Descendants(Wpf + "StreamGeometry")
            .Select(item => (string?)item.Attribute(Xaml + "Key"))
            .ToArray();
        Assert.Contains("IconMoreHorizontal", icons);
        Assert.Contains("IconSearch", icons);
    }

    private static XElement Toolbar() => Named(Load("CavdeckExplorerView.xaml").Root!, "Grid", "ToolbarRow");
    private static XElement Named(XContainer root, string tag, string name) =>
        root.Descendants(Wpf + tag).Single(element => (string?)element.Attribute(Xaml + "Name") == name);
    private static XDocument Load(string filename) =>
        XDocument.Load(Path.Combine(AppContext.BaseDirectory, filename));
}
