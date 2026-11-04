using System.Xml.Linq;
using Cavdeck.App.Features.Explorer;
using Cavdeck.App.Localization;
using Cavdeck.Domain.Storage;
using Xunit;

namespace Cavdeck.App.Tests;

public sealed class BucketFavoriteExperienceTests
{
    private static readonly XNamespace Wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static XDocument View() =>
        XDocument.Load(Path.Combine(AppContext.BaseDirectory, "CavdeckExplorerView.xaml"));

    [Fact]
    public void Bucket_table_uses_name_as_first_column_without_a_favorite_column()
    {
        var grid = View().Descendants(Wpf + "DataGrid").Single(e =>
            (string?)e.Attribute(Xaml + "Name") == "BucketsGrid");
        var columns = grid.Element(Wpf + "DataGrid.Columns")!.Elements().ToArray();

        Assert.Equal(2, columns.Length);
        Assert.Equal("DataGridTemplateColumn", columns[0].Name.LocalName);
        Assert.Equal("*", (string?)columns[0].Attribute("Width"));
        Assert.Equal("Name", (string?)columns[0].Attribute("SortMemberPath"));
        Assert.Equal("140", (string?)columns[1].Attribute("Width"));
        Assert.Equal("BucketsGrid_SizeChanged", (string?)grid.Attribute("SizeChanged"));

        var nameCell = columns[0].Descendants(Wpf + "Grid").Single();
        Assert.Equal(3, nameCell.Element(Wpf + "Grid.ColumnDefinitions")!.Elements().Count());
        Assert.Contains(nameCell.Descendants(Wpf + "TextBlock"), text =>
            (string?)text.Attribute("Text") == "{Binding Name}" &&
            (string?)text.Attribute("TextTrimming") == "CharacterEllipsis");
    }

    [Fact]
    public void Favorite_button_preserves_layout_and_is_discoverable_on_hover_focus_or_saved_state()
    {
        var view = View();
        var style = view.Descendants(Wpf + "Style").Single(e =>
            (string?)e.Attribute(Xaml + "Key") == "BucketFavoriteButton");
        var setters = style.Elements(Wpf + "Setter").ToArray();
        Assert.Contains(setters, s => (string?)s.Attribute("Property") == "Opacity" &&
            (string?)s.Attribute("Value") == "0");
        Assert.Contains(setters, s => (string?)s.Attribute("Property") == "Width" &&
            (string?)s.Attribute("Value") == "28");

        var triggers = style.Element(Wpf + "Style.Triggers")!;
        Assert.Contains(triggers.Elements(Wpf + "DataTrigger"), t =>
            ((string?)t.Attribute("Binding"))?.Contains("IsFavorite") == true);
        Assert.Contains(triggers.Elements(Wpf + "DataTrigger"), t =>
            ((string?)t.Attribute("Binding"))?.Contains("AncestorType=DataGridRow") == true);
        Assert.Contains(triggers.Elements(Wpf + "Trigger"), t =>
            (string?)t.Attribute("Property") == "IsKeyboardFocused");

        var button = view.Descendants(Wpf + "Button").Single(b =>
            (string?)b.Attribute("Style") == "{StaticResource BucketFavoriteButton}");
        Assert.Contains("ToggleBucketFavoriteCommand", (string?)button.Attribute("Command"));
        Assert.Equal("{Binding}", (string?)button.Attribute("CommandParameter"));
        Assert.Equal("{Binding FavoriteActionText}", (string?)button.Attribute("ToolTip"));
        Assert.Contains("Explorer.CanFavorite", (string?)button.Attribute("IsEnabled"));
        Assert.Equal("{Binding FavoriteActionText}", (string?)button.Attribute("AutomationProperties.Name"));
        Assert.Equal("2", (string?)button.Attribute("Grid.Column"));

        var pathStyle = button.Descendants(Wpf + "Style").Single();
        Assert.Contains(pathStyle.Descendants(Wpf + "Setter"), setter =>
            (string?)setter.Attribute("Property") == "Fill" &&
            (string?)setter.Attribute("Value") == "{DynamicResource AccentBrush}");
    }

    [Fact]
    public void Right_click_favorite_action_uses_the_clicked_bucket_not_an_old_selection()
    {
        var grid = View().Descendants(Wpf + "DataGrid").Single(e =>
            (string?)e.Attribute(Xaml + "Name") == "BucketsGrid");
        var menu = grid.Descendants(Wpf + "ContextMenu").Single();
        var item = menu.Elements(Wpf + "MenuItem").Single(node =>
            ((string?)node.Attribute("Header"))?.Contains("FavoriteActionText") == true);

        Assert.Contains("PlacementTarget.DataContext.FavoriteActionText", (string?)item.Attribute("Header"));
        Assert.Contains("PlacementTarget.Tag.ToggleBucketFavoriteCommand", (string?)item.Attribute("Command"));
        Assert.Contains("PlacementTarget.DataContext", (string?)item.Attribute("CommandParameter"));
        Assert.Contains("PlacementTarget.Tag.CanFavorite", (string?)item.Attribute("IsEnabled"));
        Assert.Contains(grid.Descendants(Wpf + "Setter"), s =>
            (string?)s.Attribute("Property") == "Tag" &&
            ((string?)s.Attribute("Value"))?.Contains("DataContext.Explorer") == true);
    }

    [Fact]
    public void Favorite_action_label_updates_on_toggle_and_language_refresh()
    {
        var texts = new LocalizationService();
        var bucket = new BucketRow(new StorageBucket("media", "us-east-1"), texts);
        var notifications = new List<string?>();
        bucket.PropertyChanged += (_, e) => notifications.Add(e.PropertyName);

        Assert.Equal(texts["Places_AddFavorite"], bucket.FavoriteActionText);
        bucket.SetFavorite(true);
        Assert.True(bucket.IsFavorite);
        Assert.Equal(texts["Places_RemoveFavorite"], bucket.FavoriteActionText);
        Assert.Contains(nameof(BucketRow.IsFavorite), notifications);
        Assert.Contains(nameof(BucketRow.FavoriteActionText), notifications);

        notifications.Clear();
        bucket.SetFavorite(true);
        Assert.Empty(notifications);

        bucket.SetFavorite(false);
        Assert.Equal(texts["Places_AddFavorite"], bucket.FavoriteActionText);
        notifications.Clear();
        bucket.RefreshLanguage();
        Assert.Equal(new[] { nameof(BucketRow.FavoriteActionText) }, notifications);
    }
}
