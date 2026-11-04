using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Cavdeck.App.Localization;
using Cavdeck.Domain.Storage;

namespace Cavdeck.App.Features.Explorer;

public partial class ExplorerView
{
    private void Objects_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (FindRow(e.OriginalSource as DependencyObject) is { Item: ExplorerRow row, IsSelected: false })
            ObjectsGrid.SelectedItem = row;
    }

    private void Objects_ContextMenuOpening(object sender, ContextMenuEventArgs e) =>
        RouteObjectContextMenu(ObjectsGrid, _gridFileMenu, _gridDirectoryMenu, e);

    private void Icons_ContextMenuOpening(object sender, ContextMenuEventArgs e) =>
        RouteObjectContextMenu(ObjectsIcons, _iconsFileMenu, _iconsDirectoryMenu, e);

    private void EmptyObjects_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (Explorer is not { ShowEmptyObjects: true } explorer)
        {
            e.Handled = true;
            return;
        }
        explorer.RefreshPasteAvailability();
    }

    private void RouteObjectContextMenu(ItemsControl host, ContextMenu fileMenu,
        ContextMenu directoryMenu, ContextMenuEventArgs e)
    {
        if (Explorer is not { ShowObjects: true } explorer)
        {
            e.Handled = true;
            return;
        }

        // Keyboard invocation has no cursor coordinates. Never reuse the last mouse hit.
        Point? pointer = e.CursorLeft < 0 && e.CursorTop < 0 ? null : Mouse.GetPosition(host);
        var source = pointer is { } point ? host.InputHitTest(point) as DependencyObject
            : Keyboard.FocusedElement as DependencyObject;
        var target = ResolveObjectContextTarget(host, source, pointer);
        if (target is { Kind: ExplorerContextTargetKind.Item, Row: { } row })
        {
            if (!explorer.SelectedRows.Contains(row))
            {
                if (host is DataGrid grid)
                    grid.SelectedItem = row;
                else if (host is ListBox icons)
                    icons.SelectedItem = row;
            }
            // RequestSelection can reject the change while a dirty preview awaits confirmation.
            // Never show the old selection's file actions under the new row.
            if (!explorer.SelectedRows.Contains(row))
            {
                e.Handled = true;
                return;
            }
            host.ContextMenu = fileMenu;
        }
        else if (target.Kind == ExplorerContextTargetKind.None)
        {
            e.Handled = true;
            return;
        }
        else
        {
            host.ContextMenu = directoryMenu;
        }
        explorer.RefreshPasteAvailability();
    }

    // A pointer must be inside the scrolling viewport. Keyboard invocation uses focus.
    internal static ExplorerContextTarget ResolveObjectContextTarget(
        ItemsControl host, DependencyObject? source, Point? pointer)
    {
        if (source is null || !IsWithin(source, host)
            || FindAncestor<ScrollBar>(source) is not null
            || FindAncestor<Thumb>(source) is not null
            || FindAncestor<DataGridColumnHeader>(source) is not null
            || FindAncestor<ButtonBase>(source) is not null)
            return default;

        var row = host is DataGrid
            ? FindAncestor<DataGridRow>(source)?.Item as ExplorerRow
            : FindAncestor<ListBoxItem>(source)?.DataContext as ExplorerRow;
        if (row is not null && host.Items.Contains(row))
            return new(ExplorerContextTargetKind.Item, row);

        if (pointer is null)
            return ReferenceEquals(source, host) || FindAncestor<ScrollContentPresenter>(source) is not null
                ? new(ExplorerContextTargetKind.Directory, null) : default;

        var viewport = FindMarqueeChild<ScrollContentPresenter>(host);
        if (viewport is null)
            return default;
        var bounds = viewport.TransformToAncestor(host).TransformBounds(new Rect(viewport.RenderSize));
        return bounds.Contains(pointer.Value)
            ? new(ExplorerContextTargetKind.Directory, null) : default;
    }

    private static bool IsWithin(DependencyObject source, DependencyObject host)
    {
        for (DependencyObject? node = source; node is not null;
             node = node is Visual or System.Windows.Media.Media3D.Visual3D
                 ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
            if (ReferenceEquals(node, host))
                return true;
        return false;
    }

}
