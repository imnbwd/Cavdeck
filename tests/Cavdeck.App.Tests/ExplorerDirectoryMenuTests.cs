using System.Runtime.ExceptionServices;
using System.Globalization;
using System.Resources;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Xml.Linq;
using Cavdeck.App.Features.Explorer;
using Cavdeck.App.Localization;
using Cavdeck.Domain.Storage;
using Xunit;

namespace Cavdeck.App.Tests;

public sealed class ExplorerDirectoryMenuTests
{
    private static readonly XNamespace Wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void Directory_menu_is_attached_to_both_file_views_and_the_full_empty_area()
    {
        var view = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "CavdeckExplorerView.xaml"));
        var menu = view.Descendants(Wpf + "ContextMenu").Single(element =>
            (string?)element.Attribute(Xaml + "Key") == "CurrentDirectoryMenu");
        Assert.Equal("False", (string?)menu.Attribute(Xaml + "Shared"));
        Assert.Contains("PlacementTarget.DataContext", (string?)menu.Attribute("DataContext"));
        Assert.Equal(new[] { "MenuItem", "Separator", "MenuItem", "MenuItem", "MenuItem", "Separator", "MenuItem", "MenuItem", "MenuItem" },
            menu.Elements().Select(element => element.Name.LocalName));

        foreach (var (name, handler) in new[]
                 { ("ObjectsGrid", "Objects_ContextMenuOpening"), ("ObjectsIcons", "Icons_ContextMenuOpening") })
        {
            var host = view.Descendants().Single(element => (string?)element.Attribute(Xaml + "Name") == name);
            Assert.Equal(handler, (string?)host.Attribute("ContextMenuOpening"));
            Assert.NotNull(host.Elements().Single(element => element.Name.LocalName.EndsWith(".ContextMenu")));
        }

        var empty = view.Descendants(Wpf + "Grid").Single(element =>
            (string?)element.Attribute(Xaml + "Name") == "EmptyObjectsArea");
        Assert.Equal("{StaticResource CurrentDirectoryMenu}", (string?)empty.Attribute("ContextMenu"));
        Assert.Contains("ShowEmptyObjects", (string?)empty.Attribute("Visibility"));
        Assert.Equal("{DynamicResource SurfaceBrush}", (string?)empty.Attribute("Background"));
    }

    [Fact]
    public void Directory_menu_uses_current_location_commands_and_localized_copy()
    {
        var view = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "CavdeckExplorerView.xaml"));
        var menu = view.Descendants(Wpf + "ContextMenu").Single(element =>
            (string?)element.Attribute(Xaml + "Key") == "CurrentDirectoryMenu");
        var entries = menu.Elements(Wpf + "MenuItem").ToArray();
        Assert.Contains("CanPasteToCurrentDirectory", (string?)entries[0].Attribute("IsEnabled"));
        Assert.Contains("PasteCommand", (string?)entries[0].Attribute("Command"));
        Assert.Equal("Ctrl+V", (string?)entries[0].Attribute("InputGestureText"));
        Assert.Equal("UploadFiles_Click", (string?)entries[1].Attribute("Click"));
        Assert.Equal("UploadFolder_Click", (string?)entries[2].Attribute("Click"));
        Assert.All(entries.Skip(1).Take(3), entry =>
            Assert.Contains("CanWriteToCurrentDirectory", (string?)entry.Attribute("IsEnabled")));
        Assert.Equal(new[] { "NewFolderCommand", "NewTextFileCommand" },
            entries[3].Elements(Wpf + "MenuItem").Select(item =>
                ((string?)item.Attribute("Command"))!.Split('.').Last().TrimEnd('}')));
        Assert.Equal("{Binding Explorer.GoToPathCommand}", (string?)entries[4].Attribute("Command"));
        Assert.Equal("{Binding Texts[ExplorerPath_Menu]}", (string?)entries[4].Attribute("Header"));
        Assert.Equal("Ctrl+G", (string?)entries[4].Attribute("InputGestureText"));
        Assert.Equal("{StaticResource IconGoToPath}",
            (string?)entries[4].Descendants(Wpf + "Path").Single().Attribute("Data"));
        Assert.Single(view.Descendants(Wpf + "MenuItem"), entry =>
            (string?)entry.Attribute("Command") == "{Binding Explorer.GoToPathCommand}");
        Assert.Contains("RefreshCommand", (string?)entries[5].Attribute("Command"));
        Assert.Equal("F5", (string?)entries[5].Attribute("InputGestureText"));
        Assert.Contains("ToggleFavoriteCommand", (string?)entries[6].Attribute("Command"));
        Assert.All(menu.Descendants(Wpf + "Path"), path =>
            Assert.Equal("{StaticResource ContextMenuIconStyle}", (string?)path.Attribute("Style")));
        Assert.All(menu.Elements(Wpf + "Separator"), separator =>
            Assert.Equal("{StaticResource ContextMenuSeparatorStyle}", (string?)separator.Attribute("Style")));

        var strings = new ResourceManager("Cavdeck.App.Resources.Strings", typeof(LocalizationService).Assembly);
        foreach (var key in new[] { "Explorer_UploadFiles", "Explorer_UploadFolder", "Explorer_RefreshMenu" })
        {
            var english = strings.GetString(key, CultureInfo.GetCultureInfo("en"));
            var chinese = strings.GetString(key, CultureInfo.GetCultureInfo("zh-CN"));
            Assert.False(string.IsNullOrWhiteSpace(english));
            Assert.False(string.IsNullOrWhiteSpace(chinese));
            Assert.NotEqual(english, chinese);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Hit_test_distinguishes_item_whitespace_and_chrome(bool icons)
    {
        RunSta(() =>
        {
            var row = new ExplorerRow(new ObjectEntry(ObjectEntryKind.File, "report.pdf", "report.pdf",
                12, null, null), new LocalizationService());
            ItemsControl host;
            if (icons)
                host = new ListBox { ItemsSource = new[] { row } };
            else
            {
                var grid = new DataGrid { ItemsSource = new[] { row }, AutoGenerateColumns = false };
                grid.Columns.Add(new DataGridTextColumn { Header = "Name",
                    Binding = new System.Windows.Data.Binding(nameof(ExplorerRow.Name)) });
                host = grid;
            }
            var window = new Window { Width = 500, Height = 450, Content = host,
                ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None };
            try
            {
                window.Show();
                window.UpdateLayout();
                var item = host.ItemContainerGenerator.ContainerFromItem(row) as DependencyObject;
                Assert.NotNull(item);
                var hit = ExplorerView.ResolveObjectContextTarget(host, item, null);
                Assert.Equal(ExplorerContextTargetKind.Item, hit.Kind);
                Assert.Same(row, hit.Row);

                var viewport = FindChild<ScrollContentPresenter>(host);
                Assert.NotNull(viewport);
                var blankPoint = viewport.TransformToAncestor(host)
                    .Transform(new Point(viewport.ActualWidth / 2, viewport.ActualHeight - 12));
                Assert.Equal(ExplorerContextTargetKind.Directory,
                    ExplorerView.ResolveObjectContextTarget(host, viewport, blankPoint).Kind);
                Assert.Equal(ExplorerContextTargetKind.Directory,
                    ExplorerView.ResolveObjectContextTarget(host, host, null).Kind);

                if (!icons)
                {
                    var header = FindChild<DataGridColumnHeader>(host);
                    Assert.NotNull(header);
                    Assert.Equal(ExplorerContextTargetKind.None,
                        ExplorerView.ResolveObjectContextTarget(host, header, blankPoint).Kind);
                }
                var scrollbar = FindChild<ScrollBar>(host);
                Assert.NotNull(scrollbar);
                Assert.Equal(ExplorerContextTargetKind.None,
                    ExplorerView.ResolveObjectContextTarget(host, scrollbar, blankPoint).Kind);
            }
            finally { window.Close(); }
        });
    }

    private static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match)
                return match;
            if (FindChild<T>(child) is { } nested)
                return nested;
        }
        return null;
    }

    private static void RunSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception exception) { error = exception; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "Explorer context test timed out");
        if (error is not null)
            ExceptionDispatchInfo.Capture(error).Throw();
    }
}
