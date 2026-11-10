using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Xml.Linq;
using Cavdeck.App.Features.Explorer;
using Xunit;

namespace Cavdeck.App.Tests;

public sealed class ExplorerHorizontalOverflowTests
{
    private static readonly XNamespace Wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void Object_name_is_constrained_to_the_remaining_column_width()
    {
        var view = LoadView();
        var grid = ObjectGrid(view);
        var nameColumn = Assert.Single(grid.Element(Wpf + "DataGrid.Columns")!
            .Elements(Wpf + "DataGridTemplateColumn"));
        Assert.Equal("*", (string?)nameColumn.Attribute("Width"));

        var template = nameColumn.Descendants(Wpf + "DataTemplate").Single();
        var layout = Assert.Single(template.Elements(Wpf + "Grid"));
        Assert.Equal("0", (string?)layout.Attribute("MinWidth"));
        Assert.Equal("True", (string?)layout.Attribute("ClipToBounds"));

        var definitions = layout.Element(Wpf + "Grid.ColumnDefinitions")!
            .Elements(Wpf + "ColumnDefinition").ToArray();
        Assert.Equal(2, definitions.Length);
        Assert.Equal("Auto", (string?)definitions[0].Attribute("Width"));
        Assert.Equal("*", (string?)definitions[1].Attribute("Width"));
        Assert.Equal("0", (string?)definitions[1].Attribute("MinWidth"));

        var iconHost = Assert.Single(layout.Elements(Wpf + "Grid"));
        Assert.Equal("0", (string?)iconHost.Attribute("Grid.Column"));
        Assert.Equal("20", (string?)iconHost.Attribute("Width"));
        var icon = Assert.Single(iconHost.Elements(Wpf + "Path"));
        Assert.Equal("{StaticResource ThumbnailFallbackGlyph}", (string?)icon.Attribute("Style"));
        Assert.Single(iconHost.Elements(Wpf + "Image"));

        var name = Assert.Single(layout.Elements(Wpf + "TextBlock"));
        Assert.Equal("1", (string?)name.Attribute("Grid.Column"));
        Assert.Equal("{Binding DisplayName}", (string?)name.Attribute("Text"));
        Assert.Equal("CharacterEllipsis", (string?)name.Attribute("TextTrimming"));
        Assert.Equal("NoWrap", (string?)name.Attribute("TextWrapping"));
        Assert.Equal("{Binding DisplayName}", (string?)name.Attribute("ToolTip"));
    }

    [Fact]
    public void Object_list_uses_native_auto_horizontal_scrolling()
    {
        var view = LoadView();
        Assert.Equal("Auto", (string?)ObjectGrid(view).Attribute("ScrollViewer.HorizontalScrollBarVisibility"));
        Assert.Equal("ObjectsGrid_IsVisibleChanged", (string?)ObjectGrid(view).Attribute("IsVisibleChanged"));
        var buckets = view.Descendants(Wpf + "DataGrid").Single(grid =>
            (string?)grid.Attribute(Xaml + "Name") == "BucketsGrid");
        Assert.Equal("Disabled", (string?)buckets.Attribute("ScrollViewer.HorizontalScrollBarVisibility"));
    }


    [Theory]
    [InlineData(359, false, false, false)]
    [InlineData(360, true, false, false)]
    [InlineData(519, true, false, false)]
    [InlineData(520, true, false, true)]
    [InlineData(759, true, false, true)]
    [InlineData(760, true, true, true)]
    public void Responsive_columns_still_preserve_a_flexible_name_column(
        double width, bool size, bool kind, bool modified)
    {
        var visible = ExplorerColumnLayout.ForWidth(width);
        Assert.Equal(size, visible.Size);
        Assert.Equal(kind, visible.Kind);
        Assert.Equal(modified, visible.Modified);
    }

    [Fact]
    public void Switching_between_short_and_long_names_keeps_the_cell_within_its_width()
    {
        RunSta(() =>
        {
            // Use the actual filename template inside a bounded presenter.
            // This tests its desired width without depending on a top-level
            // Window or themed DataGrid chrome in a headless CI session.
            var cell = new ContentPresenter { ContentTemplate = RuntimeNameTemplate() };
            foreach (var fileName in new[]
                     {
                         "short.txt",
                         new string('文', 400) + new string('A', 400) + ".epub",
                         "short-again.txt",
                     })
            {
                cell.Content = new FilenameRow(fileName);
                cell.InvalidateMeasure();
                cell.Measure(new Size(320, 32));
                Assert.InRange(cell.DesiredSize.Width, 0, 320.5);
                cell.Arrange(new Rect(0, 0, 320, 32));
                cell.UpdateLayout();

                var name = FindVisualChild<TextBlock>(cell);
                Assert.NotNull(name);
                Assert.Equal(fileName, name.Text);
                Assert.InRange(name.ActualWidth, 0, 320);
            }
        });
    }

    private static DataTemplate RuntimeNameTemplate()
    {
        // Use the real XAML layout, replacing only the icon style dependency so
        // the template can be measured without starting the complete application.
        var nameTemplate = new XElement(ObjectGrid(LoadView()).Descendants(Wpf + "DataTemplate").Single());
        // Thumbnail presenters are viewport-driven and require the Explorer visual
        // tree. The layout-only test measures text with the glyph placeholder.
        nameTemplate.Descendants(Wpf + "Image").Remove();
        var icon = nameTemplate.Descendants(Wpf + "Path").Single();
        icon.SetAttributeValue("Style", null);
        icon.SetAttributeValue("Width", "16");
        icon.SetAttributeValue("Height", "16");
        return (DataTemplate)XamlReader.Parse(nameTemplate.ToString());
    }

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception exception) { failure = exception; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)), "WPF layout regression test timed out.");
        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match)
                return match;
            var nested = FindVisualChild<T>(child);
            if (nested is not null)
                return nested;
        }
        return null;
    }

    public sealed record FilenameRow(string DisplayName)
    {
        public string Size => "1 KB";
        public string Kind => "File";
        public string ModifiedText => "2026/10/19";
    }

    private static XElement ObjectGrid(XDocument view) =>
        view.Descendants(Wpf + "DataGrid").Single(grid =>
            (string?)grid.Attribute(Xaml + "Name") == "ObjectsGrid");

    private static XDocument LoadView() =>
        XDocument.Load(Path.Combine(AppContext.BaseDirectory, "CavdeckExplorerView.xaml"));
}
