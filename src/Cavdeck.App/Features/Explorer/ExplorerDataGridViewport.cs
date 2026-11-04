using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Cavdeck.App.Features.Explorer;

/// <summary>
/// Keeps the leading Name column anchored when the Explorer viewport is resized.
/// </summary>
public static class ExplorerDataGridViewport
{
    public static void Reflow(DataGrid grid)
    {
        ArgumentNullException.ThrowIfNull(grid);
        if (grid.Columns.Count == 0)
            return;

        var nameColumn = grid.Columns[0];
        var width = nameColumn.Width;

        // WPF can retain a stale minimum/extent for a star column after a large
        // viewport change. Re-enter star sizing to make it measure against the
        // current viewport. Pixel/Auto widths are user intent and stay untouched.
        if (width.IsStar)
        {
            nameColumn.Width = DataGridLength.Auto;
            nameColumn.Width = width;
        }

        grid.InvalidateMeasure();
        grid.UpdateLayout();

        // The Name column is the leading column and must remain the visible anchor.
        // A stale horizontal offset can otherwise leave Size/Kind visible while
        // the Name column is scrolled out of view after the viewport contracts.
        // Preserve scrolling for an explicitly resized (non-star) Name column.
        if (width.IsStar && FindScrollViewer(grid) is { } scrollViewer)
            scrollViewer.ScrollToHorizontalOffset(0);
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject parent)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is ScrollViewer scrollViewer)
                return scrollViewer;

            var nested = FindScrollViewer(child);
            if (nested is not null)
                return nested;
        }

        return null;
    }
}
