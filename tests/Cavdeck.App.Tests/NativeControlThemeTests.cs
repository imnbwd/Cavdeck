using System.Xml.Linq;
using Xunit;

namespace Cavdeck.App.Tests;

public sealed class NativeControlThemeTests
{
    private static readonly XNamespace Wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static XDocument Load(string file) =>
        XDocument.Load(Path.Combine(AppContext.BaseDirectory, file));

    private static XElement NamedStyle(string key) =>
        Assert.Single(Load("CavdeckInputs.xaml").Descendants(Wpf + "Style")
            , style => (string?)style.Attribute(Xaml + "Key") == key);

    private static string? Setter(XElement style, string property) =>
        style.Elements(Wpf + "Setter")
            .Single(s => (string?)s.Attribute("Property") == property)
            .Attribute("Value")?.Value;

    [Fact]
    public void Copy_destination_picker_uses_themed_dialog_list_without_changing_navigation()
    {
        var picker = Load("CavdeckCopyPicker.xaml");
        var folders = Assert.Single(picker.Descendants(Wpf + "ListBox"));
        Assert.Equal("Folders", (string?)folders.Attribute(Xaml + "Name"));
        Assert.Equal("{StaticResource DialogFolderList}", (string?)folders.Attribute("Style"));
        Assert.Equal("Folders_MouseDoubleClick", (string?)folders.Attribute("MouseDoubleClick"));
        Assert.Equal("Folders_KeyDown", (string?)folders.Attribute("KeyDown"));
    }

    [Fact]
    public void Dialog_list_uses_theme_brushes_for_surface_and_every_interaction_state()
    {
        var list = NamedStyle("DialogFolderList");
        Assert.Equal("ListBox", (string?)list.Attribute("TargetType"));
        Assert.Equal("{DynamicResource InputBackgroundBrush}", Setter(list, "Background"));
        Assert.Equal("{DynamicResource BorderBrush}", Setter(list, "BorderBrush"));
        Assert.Equal("{DynamicResource PrimaryTextBrush}", Setter(list, "Foreground"));
        Assert.Equal("{StaticResource DialogFolderListItem}", Setter(list, "ItemContainerStyle"));

        var listTemplate = Assert.Single(list.Descendants(Wpf + "ControlTemplate"));
        Assert.NotNull(listTemplate.Descendants(Wpf + "ItemsPresenter").SingleOrDefault());
        Assert.NotNull(listTemplate.Descendants(Wpf + "ScrollViewer").SingleOrDefault());
        Assert.Equal("Auto", Setter(list, "ScrollViewer.HorizontalScrollBarVisibility"));

        var item = NamedStyle("DialogFolderListItem");
        Assert.Equal("ListBoxItem", (string?)item.Attribute("TargetType"));
        Assert.Equal("{DynamicResource PrimaryTextBrush}", Setter(item, "Foreground"));
        var template = Assert.Single(item.Descendants(Wpf + "ControlTemplate"));
        foreach (var (state, brush) in new[]
        {
            ("IsMouseOver", "HoverBrush"),
            ("IsSelected", "SelectedBrush")
        })
        {
            Assert.Contains(template.Descendants(Wpf + "Trigger"), trigger =>
                (string?)trigger.Attribute("Property") == state &&
                trigger.Descendants(Wpf + "Setter").Any(setter =>
                    (string?)setter.Attribute("Value") == $"{{DynamicResource {brush}}}"));
        }
        Assert.Contains(template.Descendants(Wpf + "DataTrigger"), trigger =>
            ((string?)trigger.Attribute("Binding"))?.Contains("FocusCue.Visible") == true &&
            trigger.Descendants(Wpf + "Setter").Any(setter =>
                (string?)setter.Attribute("Value") == "{DynamicResource FocusBorderBrush}"));
        Assert.Contains(template.Descendants(Wpf + "Trigger"), trigger =>
            (string?)trigger.Attribute("Property") == "IsEnabled" &&
            (string?)trigger.Attribute("Value") == "False");
    }

    [Theory]
    [InlineData("CavdeckTransfersView.xaml")]
    [InlineData("CavdeckPreviewProgressView.xaml")]
    public void Progress_bars_use_shared_palette_without_overriding_native_animation(string view)
    {
        var bars = Load(view).Descendants(Wpf + "ProgressBar").ToArray();
        Assert.NotEmpty(bars);
        foreach (var progress in bars)
        {
            Assert.Equal("{StaticResource ThemedProgressBar}", (string?)progress.Attribute("Style"));
            Assert.Equal(true, ((string?)progress.Attribute("Value"))?.Contains("Mode=OneWay"));
        }

        var style = NamedStyle("ThemedProgressBar");
        Assert.Equal("{DynamicResource AccentBrush}", Setter(style, "Foreground"));
        Assert.Equal("{DynamicResource SurfaceMutedBrush}", Setter(style, "Background"));
        Assert.Null(style.Descendants(Wpf + "ControlTemplate").FirstOrDefault());
    }

    [Fact]
    public void Existing_explorer_and_transfers_listboxes_keep_their_specialized_item_styles()
    {
        var explorer = Load("CavdeckExplorerView.xaml").Descendants(Wpf + "ListBox").Single();
        Assert.Equal("{StaticResource ExplorerDropIconTile}", (string?)explorer.Attribute("ItemContainerStyle"));
        Assert.Null(explorer.Attribute("Style"));

        var transfers = Load("CavdeckTransfersView.xaml").Descendants(Wpf + "ListBox").Single();
        Assert.NotNull(transfers.Element(Wpf + "ListBox.ItemContainerStyle"));
        Assert.Null(transfers.Attribute("Style"));
        Assert.DoesNotContain(Load("CavdeckInputs.xaml").Root!.Elements(Wpf + "Style"),
            s => (string?)s.Attribute("TargetType") == "ListBox" &&
                 s.Attribute(Xaml + "Key") is null);
    }
}
