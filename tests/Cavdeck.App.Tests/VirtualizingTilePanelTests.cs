using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Cavdeck.App.Features.Explorer;
using Xunit;

namespace Cavdeck.App.Tests;

public sealed class VirtualizingTilePanelTests
{
    [Fact]
    public void Large_grid_realizes_only_visible_tiles_and_can_scroll()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var factory = new FrameworkElementFactory(typeof(VirtualizingTilePanel));
                factory.SetBinding(VirtualizingTilePanel.ItemWidthProperty,
                    new Binding("Tag") { RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(ListBox), 1) });
                factory.SetValue(VirtualizingTilePanel.ItemHeightProperty, 50d);
                var list = new ListBox
                {
                    ItemsSource = Enumerable.Range(0, 1000).ToArray(),
                    ItemsPanel = new ItemsPanelTemplate(factory),
                    SelectionMode = SelectionMode.Extended,
                    Tag = "100"
                };
                VirtualizingPanel.SetIsVirtualizing(list, true);
                ScrollViewer.SetCanContentScroll(list, true);
                var window = new Window { Content = list, Width = 400, Height = 200,
                    WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false };
                window.Show();
                window.UpdateLayout();

                var panel = Find<VirtualizingTilePanel>(list);
                Assert.NotNull(panel);
                Assert.Equal(100, panel.ItemWidth);
                Assert.True(panel.Children.Count < 100, $"Realized {panel.Children.Count} tiles");
                Assert.True(panel.ExtentHeight > 200);
                list.SelectAll();
                Assert.Equal(1000, list.SelectedItems.Count);
                Assert.True(panel.Children.Count < 100, "Selecting all should not realize all tiles");
                list.UnselectAll();

                panel.SetVerticalOffset(1000);
                list.UpdateLayout();
                Assert.True(panel.VerticalOffset > 0);
                Assert.True(panel.Children.Count < 100, $"Realized {panel.Children.Count} tiles after scrolling");
                list.SelectedItem = 900;
                list.ScrollIntoView(900);
                list.UpdateLayout();
                Assert.NotNull(list.ItemContainerGenerator.ContainerFromItem(900));
                Assert.Equal(900, list.SelectedItem);
                window.Close();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "WPF layout test timed out");
        if (failure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static T? Find<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match)
            return match;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            if (Find<T>(VisualTreeHelper.GetChild(root, index)) is { } child)
                return child;
        return null;
    }
}
