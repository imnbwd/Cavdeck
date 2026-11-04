using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Cavdeck.App.Features.Explorer;
using Xunit;

namespace Cavdeck.App.Tests;

public sealed class ExplorerDataGridViewportTests
{
    [Fact]
    public void Directory_changes_and_details_pane_keep_name_column_visible()
    {
        RunSta(() =>
        {
            var fileGrid = new DataGrid
            {
                AutoGenerateColumns = false,
                HeadersVisibility = DataGridHeadersVisibility.Column,
                RowHeaderWidth = 0,
                CanUserAddRows = false,
                IsReadOnly = true
            };
            // Let WPF calculate actual overflow while the details pane resizes the viewport.
            fileGrid.Columns.Add(new DataGridTextColumn
                { Header = "Name", Binding = new System.Windows.Data.Binding(nameof(Item.Name)), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
            fileGrid.Columns.Add(new DataGridTextColumn
                { Header = "Size", Binding = new System.Windows.Data.Binding(nameof(Item.Size)), Width = new DataGridLength(110) });
            fileGrid.Columns.Add(new DataGridTextColumn
                { Header = "Type", Binding = new System.Windows.Data.Binding(nameof(Item.Kind)), Width = new DataGridLength(110) });
            fileGrid.Columns.Add(new DataGridTextColumn
                { Header = "Modified", Binding = new System.Windows.Data.Binding(nameof(Item.Modified)), Width = new DataGridLength(180) });
            ScrollViewer.SetHorizontalScrollBarVisibility(fileGrid, ScrollBarVisibility.Auto);

            fileGrid.SizeChanged += (_, args) =>
            {
                if (!args.WidthChanged || fileGrid.ActualWidth <= 0)
                    return;
                var layout = ExplorerColumnLayout.ForWidth(fileGrid.ActualWidth);
                fileGrid.Columns[1].Visibility = layout.Size ? Visibility.Visible : Visibility.Collapsed;
                fileGrid.Columns[2].Visibility = layout.Kind ? Visibility.Visible : Visibility.Collapsed;
                fileGrid.Columns[3].Visibility = layout.Modified ? Visibility.Visible : Visibility.Collapsed;
                fileGrid.Dispatcher.BeginInvoke(DispatcherPriority.Background,
                    new Action(() => ExplorerDataGridViewport.Reflow(fileGrid)));
            };
            var page = new Grid();
            page.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            page.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0) });
            Grid.SetColumn(fileGrid, 0);
            page.Children.Add(fileGrid);
            var window = new Window { Width = 1150, Height = 560, Content = page,
                ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None };
            try
            {
                window.Show();
                SettleLayout(window, fileGrid);
                // Mirror navigation: the list disappears while a new directory loads,
                // then reappears with a replacement collection in a resized workspace.
                fileGrid.IsVisibleChanged += (_, args) =>
                {
                    if (args.NewValue is not true)
                        return;
                    fileGrid.Dispatcher.BeginInvoke(() =>
                    {
                        if (!fileGrid.IsVisible)
                            return;
                        ExplorerDataGridViewport.Reflow(fileGrid);
                    }, DispatcherPriority.Background);
                };
                foreach (var sideWidth in new[] { 0.0, 360.0, 590.0, 830.0, 0.0, 360.0 })
                {
                    page.ColumnDefinitions[1].Width = new GridLength(sideWidth);
                    foreach (var name in new[] { "short.zip", new string('文', 200) + ".txt", "scripts" })
                    {
                        fileGrid.Visibility = Visibility.Collapsed;
                        fileGrid.ItemsSource = new[] { new Item(name) };
                        SettleLayout(window, fileGrid);
                        fileGrid.Visibility = Visibility.Visible;
                        SettleLayout(window, fileGrid);
                        var scroll = FindVisualChild<ScrollViewer>(fileGrid);
                        Assert.NotNull(scroll);
                        var columns = ExplorerColumnLayout.ForWidth(fileGrid.ActualWidth);
                        Assert.Equal(Visibility.Visible, fileGrid.Columns[0].Visibility);
                        Assert.Equal(columns.Size ? Visibility.Visible : Visibility.Collapsed, fileGrid.Columns[1].Visibility);
                        Assert.Equal(columns.Kind ? Visibility.Visible : Visibility.Collapsed, fileGrid.Columns[2].Visibility);
                        Assert.Equal(columns.Modified ? Visibility.Visible : Visibility.Collapsed, fileGrid.Columns[3].Visibility);
                        Assert.True(fileGrid.Columns[0].ActualWidth > 40,
                            $"Name column lost after side={sideWidth}; width={fileGrid.ActualWidth:F1}, name={fileGrid.Columns[0].ActualWidth:F1}.");
                        Assert.InRange(scroll.HorizontalOffset, 0, 0.1);
                        Assert.Equal(ScrollBarVisibility.Auto, ScrollViewer.GetHorizontalScrollBarVisibility(fileGrid));
                        Assert.InRange(scroll.ScrollableWidth, 0, 2);
                    }
                }
                // A manually resized name column is allowed to exceed the viewport:
                // the user must still be able to reach off-screen columns.
                fileGrid.Columns[0].Width = new DataGridLength(1400);
                SettleLayout(window, fileGrid);
                var resizedScroll = FindVisualChild<ScrollViewer>(fileGrid);
                Assert.NotNull(resizedScroll);
                Assert.Equal(ScrollBarVisibility.Auto,
                    ScrollViewer.GetHorizontalScrollBarVisibility(fileGrid));
                Assert.True(resizedScroll.ScrollableWidth > 5,
                    $"Expected real horizontal overflow after resizing; extent={resizedScroll.ExtentWidth:F1}, viewport={resizedScroll.ViewportWidth:F1}.");
                resizedScroll.ScrollToHorizontalOffset(resizedScroll.ScrollableWidth);
                SettleLayout(window, fileGrid);
                Assert.True(resizedScroll.HorizontalOffset > 5,
                    "The fixed-width Name column should allow intentional horizontal scrolling.");

                // Navigation must not erase a user's explicit pixel width or suppress real overflow.
                fileGrid.Visibility = Visibility.Collapsed;
                SettleLayout(window, fileGrid);
                fileGrid.Visibility = Visibility.Visible;
                SettleLayout(window, fileGrid);
                Assert.True(fileGrid.Columns[0].Width.IsAbsolute);
                Assert.Equal(1400, fileGrid.Columns[0].Width.Value);
                Assert.True(resizedScroll.ScrollableWidth > 5);
                Assert.True(resizedScroll.HorizontalOffset > 5,
                    "Reflow must preserve the horizontal position of an explicitly resized Name column.");

                // Re-entering flexible sizing after a viewport contraction must bring
                // the leading Name column back into view, even if a prior layout left
                // the horizontal viewport scrolled to the right.
                fileGrid.Columns[0].Width = new DataGridLength(1, DataGridLengthUnitType.Star);
                SettleLayout(window, fileGrid);
                ExplorerDataGridViewport.Reflow(fileGrid);
                Assert.Equal(ScrollBarVisibility.Auto,
                    ScrollViewer.GetHorizontalScrollBarVisibility(fileGrid));
                Assert.True(fileGrid.Columns[0].ActualWidth > 40);
                Assert.InRange(resizedScroll.HorizontalOffset, 0, 0.1);
            }
            finally { window.Close(); }
        });
    }

    private static void SettleLayout(Window window, DataGrid grid)
    {
        // Headless STA tests do not run a WPF dispatcher loop between UpdateLayout calls.
        // Flush Loaded/DataBind/Render work so star columns are measured like a live window.
        var frame = new DispatcherFrame();
        window.Dispatcher.BeginInvoke(DispatcherPriority.Background,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
        window.UpdateLayout();
        grid.UpdateLayout();
    }

    private sealed record Item(string Name)
    {
        public string Size => "8.8 KB";
        public string Kind => "Folder";
        public string Modified => "2026/10/16 17:09";
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T t)
                return t;
            var nested = FindVisualChild<T>(child);
            if (nested is not null)
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
            catch (Exception ex) { error = ex; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "WPF DataGrid test timed out");
        if (error is not null)
            ExceptionDispatchInfo.Capture(error).Throw();
    }
}
