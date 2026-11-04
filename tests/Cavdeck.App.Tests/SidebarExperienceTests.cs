using System.Xml.Linq;
using Xunit;

namespace Cavdeck.App.Tests;

public sealed class SidebarExperienceTests
{
    private static readonly XNamespace Wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void Nav_items_follow_effective_compact_state()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "CavdeckShellWindow.xaml"));
        Assert.DoesNotContain("Binding SidebarCollapsed", source, StringComparison.Ordinal);
        Assert.Contains("Binding EffectiveSidebarCollapsed", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Expanded_storage_connection_has_discoverable_actions()
    {
        var root = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "CavdeckShellWindow.xaml")).Root!;
        var row = root.Descendants(Wpf + "Grid")
            .Single(node => (string?)node.Attribute(Xaml + "Name") == "ConnectionRow");
        var primary = row.Elements(Wpf + "Button")
            .Single(node => (string?)node.Attribute(Xaml + "Name") == "ConnectionPrimaryButton");
        var more = row.Elements(Wpf + "Button")
            .Single(node => (string?)node.Attribute("Click") == "SidebarConnectionMore_Click");
        Assert.NotNull(primary.Element(Wpf + "Button.ContextMenu"));
        Assert.Contains("ConnectionPrimaryButton", (string?)more.Attribute("Tag"));
        Assert.Contains(row.Descendants(Wpf + "ColumnDefinition"),
            node => (string?)node.Attribute("Width") == "*");
    }

    [Fact]
    public void Empty_loading_error_and_retry_states_are_distinct()
    {
        var root = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "CavdeckShellWindow.xaml")).Root!;
        foreach (var state in new[] { "ShowSidebarEmpty", "ShowSidebarLoading", "ShowSidebarError" })
            Assert.Contains(root.Descendants().Attributes("Visibility"),
                attr => attr.Value.Contains("Connections." + state, StringComparison.Ordinal));
        Assert.Equal(2, root.Descendants(Wpf + "Button")
            .Count(button => ((string?)button.Attribute("Command"))?.Contains("Connections.ReloadCommand") == true));
    }

    [Fact]
    public void Compact_storage_separator_sits_between_workspace_and_storage()
    {
        var root = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "CavdeckShellWindow.xaml")).Root!;
        var storageButton = root.Descendants(Wpf + "Button")
            .Single(button => (string?)button.Attribute(Xaml + "Name") == "StorageNavButton");
        var siblings = storageButton.Parent!.Elements().ToList();
        var storageIndex = siblings.IndexOf(storageButton);
        Assert.True(storageIndex >= 2);

        var divider = siblings[storageIndex - 1];
        Assert.Equal(Wpf + "Separator", divider.Name);
        Assert.Equal("CompactStorageSeparator", (string?)divider.Attribute(Xaml + "Name"));
        Assert.Equal("8,8,8,8", (string?)divider.Attribute("Margin"));
        Assert.Contains("Binding EffectiveSidebarCollapsed", (string?)divider.Attribute("Visibility"));
        Assert.Equal("transfers", (string?)siblings[storageIndex - 2].Attribute("Tag"));

        // Expanded storage keeps its existing divider and spacing.
        var expandedSection = siblings[storageIndex + 1];
        Assert.Equal(Wpf + "StackPanel", expandedSection.Name);
        Assert.Contains("ConverterParameter=invert", (string?)expandedSection.Attribute("Visibility"));
        Assert.Contains(expandedSection.Elements(Wpf + "Separator"),
            separator => (string?)separator.Attribute("Margin") == "8,0,8,12");
    }

    [Fact]
    public void Compact_storage_menu_has_keyboard_focus_and_escape_path()
    {
        var root = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "CavdeckShellWindow.xaml")).Root!;
        var popup = root.Descendants(Wpf + "Popup")
            .Single(node => (string?)node.Attribute(Xaml + "Name") == "StorageMenu");
        Assert.Equal("StorageMenu_Opened", (string?)popup.Attribute("Opened"));
        Assert.Contains(popup.Descendants(Wpf + "Border"),
            node => (string?)node.Attribute("PreviewKeyDown") == "StorageMenu_PreviewKeyDown");
    }
}
