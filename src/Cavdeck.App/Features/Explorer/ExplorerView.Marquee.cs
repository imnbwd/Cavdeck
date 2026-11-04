using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace Cavdeck.App.Features.Explorer;

public partial class ExplorerView
{
    private UIElement? _marqueeOwner;
    private ExplorerViewModel? _marqueeSource;
    private ScrollViewer? _marqueeScroller;
    private VirtualizingTilePanel? _marqueePanel;
    private ExplorerRow[] _marqueeRows = [];
    private ExplorerRow[] _marqueeBefore = [];
    private HashSet<ExplorerRow> _marqueeCurrent = [];
    private Point _marqueePointerDown;
    private Point _marqueeOrigin;
    private bool _marqueeCandidate;
    private bool _marqueeActive;
    private bool _marqueeAdd;
    private readonly DispatcherTimer _marqueeTimer = new(DispatcherPriority.Input)
    {
        Interval = TimeSpan.FromMilliseconds(75)
    };

    private void Marquee_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (Explorer is not { ShowObjects: true } explorer || _marqueeCandidate ||
            e.ChangedButton != MouseButton.Left || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            return;
        var source = e.OriginalSource as DependencyObject;
        // Never reinterpret an item drag, resize, scrollbar, column header or button as a selection.
        if (FindAncestor<DataGridRow>(source) is not null ||
            FindAncestor<ListBoxItem>(source) is not null ||
            FindAncestor<DataGridColumnHeader>(source) is not null ||
            FindAncestor<ScrollBar>(source) is not null ||
            FindAncestor<ButtonBase>(source) is not null)
            return;
        if (sender is not UIElement owner)
            return;

        _marqueeScroller = FindMarqueeChild<ScrollViewer>(owner);
        _marqueePanel = ReferenceEquals(owner, ObjectsIcons)
            ? FindMarqueeChild<VirtualizingTilePanel>(owner) : null;
        if (_marqueeScroller is null || ReferenceEquals(owner, ObjectsIcons) && _marqueePanel is null)
            return;

        var pointer = e.GetPosition(MarqueeLayer);
        var viewport = MarqueeViewport();
        if (viewport.IsEmpty || !viewport.Contains(pointer))
            return;

        _marqueeOwner = owner;
        _marqueeSource = explorer;
        _marqueePointerDown = pointer;
        _marqueeOrigin = MarqueeContentPosition(pointer, viewport);
        _marqueeAdd = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        _marqueeCandidate = true;
        owner.Focus();
        e.Handled = true;
    }

    private void Marquee_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_marqueeCandidate || !ReferenceEquals(sender, _marqueeOwner))
            return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            CancelMarquee();
            return;
        }

        var pointer = e.GetPosition(MarqueeLayer);
        if (!_marqueeActive)
        {
            if (Math.Abs(pointer.X - _marqueePointerDown.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(pointer.Y - _marqueePointerDown.Y) < SystemParameters.MinimumVerticalDragDistance)
                return;
            var source = _marqueeSource;
            if (source is null || !ReferenceEquals(Explorer, source) || _marqueeOwner is null)
            {
                CancelMarquee();
                return;
            }
            _marqueeRows = source.Rows.ToArray();
            _marqueeBefore = _marqueeSource.SelectedRows.ToArray();
            _marqueeCurrent = _marqueeBefore.ToHashSet();
            if (!_marqueeOwner.CaptureMouse())
            {
                CancelMarquee();
                return;
            }
            _marqueeActive = true;
            MarqueeBox.Visibility = Visibility.Visible;
            _marqueeTimer.Start();
        }

        UpdateMarquee(pointer);
        e.Handled = true;
    }

    private void Marquee_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_marqueeCandidate || !ReferenceEquals(sender, _marqueeOwner))
            return;
        if (!_marqueeActive)
        {
            var owner = _marqueeOwner;
            var add = _marqueeAdd;
            ClearMarquee();
            if (!add)
            {
                if (ReferenceEquals(owner, ObjectsIcons))
                    ObjectsIcons.UnselectAll();
                else
                    ObjectsGrid.UnselectAll();
            }
        }
        else
        {
            // The viewmodel (including dirty-preview navigation confirmation) is updated ONCE.
            UpdateMarquee(e.GetPosition(MarqueeLayer));
            var source = _marqueeSource;
            var selection = _marqueeRows.Where(_marqueeCurrent.Contains).ToArray();
            var wasIconView = ReferenceEquals(_marqueeOwner, ObjectsIcons);
            var caret = Array.FindLastIndex(_marqueeRows, _marqueeCurrent.Contains);
            ClearMarquee();
            if (source is not null && ReferenceEquals(source, Explorer))
            {
                if (source.RequestSelection(selection) == false)
                    RestoreListedSelection();
                else if (wasIconView)
                {
                    // Arrow/Shift-arrow navigation resumes from the last framed item.
                    _iconCaret = caret;
                    _iconAnchor = Math.Max(0, caret);
                }
            }
        }
        e.Handled = true;
    }

    private void Marquee_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (_marqueeActive && ReferenceEquals(sender, _marqueeOwner))
            CancelMarquee();
    }

    private void Marquee_ScrollTick(object? sender, EventArgs e)
    {
        if (!_marqueeActive || _marqueeScroller is null || Mouse.LeftButton != MouseButtonState.Pressed)
        {
            CancelMarquee();
            return;
        }
        var viewport = MarqueeViewport();
        if (viewport.IsEmpty)
        {
            CancelMarquee();
            return;
        }
        var point = Mouse.GetPosition(MarqueeLayer);
        const double edge = 24;
        var previous = _marqueeScroller.VerticalOffset;
        if (point.Y <= viewport.Top + edge)
            _marqueeScroller.LineUp();
        else if (point.Y >= viewport.Bottom - edge)
            _marqueeScroller.LineDown();
        if (Math.Abs(previous - _marqueeScroller.VerticalOffset) > 0.001)
            UpdateMarquee(point);
    }

    private void UpdateMarquee(Point pointer)
    {
        if (!_marqueeActive || !ReferenceEquals(Explorer, _marqueeSource))
        {
            CancelMarquee();
            return;
        }
        var viewport = MarqueeViewport();
        if (viewport.IsEmpty)
            return;

        var bounded = new Point(Math.Clamp(pointer.X, viewport.Left, viewport.Right),
            Math.Clamp(pointer.Y, viewport.Top, viewport.Bottom));
        var end = MarqueeContentPosition(bounded, viewport);
        var region = MarqueeSelectionGeometry.Between(_marqueeOrigin, end);
        var offsetY = MarqueeVerticalOffset();
        var offsetX = _marqueeScroller?.HorizontalOffset ?? 0;
        var onScreen = new Rect(viewport.Left + region.Left - offsetX,
            viewport.Top + region.Top - offsetY, region.Width, region.Height);
        var visible = Rect.Intersect(onScreen, viewport);
        if (!visible.IsEmpty)
        {
            Canvas.SetLeft(MarqueeBox, visible.Left);
            Canvas.SetTop(MarqueeBox, visible.Top);
            MarqueeBox.Width = visible.Width;
            MarqueeBox.Height = visible.Height;
        }
        MarqueeBox.Visibility = visible.IsEmpty ? Visibility.Collapsed : Visibility.Visible;

        IReadOnlyList<int> hits;
        if (ReferenceEquals(_marqueeOwner, ObjectsIcons) && _marqueePanel is not null)
            hits = MarqueeSelectionGeometry.HitTiles(region, _marqueeRows.Length,
                _marqueePanel.Columns, _marqueePanel.ItemWidth, _marqueePanel.ItemHeight);
        else
            hits = MarqueeSelectionGeometry.HitRows(region, _marqueeRows.Length,
                Math.Max(1, ObjectsGrid.RowHeight), viewport.Width + offsetX);

        var next = _marqueeAdd ? _marqueeBefore.ToHashSet() : new HashSet<ExplorerRow>();
        foreach (var index in hits)
            next.Add(_marqueeRows[index]);
        if (next.SetEquals(_marqueeCurrent))
            return;

        var selected = ReferenceEquals(_marqueeOwner, ObjectsIcons)
            ? (IList)ObjectsIcons.SelectedItems : ObjectsGrid.SelectedItems;
        _restoringSelection = true;
        try
        {
            foreach (var old in _marqueeCurrent)
                if (!next.Contains(old))
                    selected.Remove(old);
            foreach (var added in next)
                if (!_marqueeCurrent.Contains(added))
                    selected.Add(added);
        }
        finally
        {
            _restoringSelection = false;
        }
        _marqueeCurrent = next;
    }

    private Rect MarqueeViewport()
    {
        if (_marqueeScroller is null)
            return Rect.Empty;
        var presenter = FindMarqueeChild<ScrollContentPresenter>(_marqueeScroller);
        if (presenter is null || presenter.ActualWidth <= 0 || presenter.ActualHeight <= 0)
            return Rect.Empty;
        try
        {
            var view = presenter.TransformToVisual(MarqueeLayer)
                .TransformBounds(new Rect(presenter.RenderSize));
            return Rect.Intersect(view, new Rect(MarqueeLayer.RenderSize));
        }
        catch (InvalidOperationException)
        {
            return Rect.Empty;
        }
    }

    private Point MarqueeContentPosition(Point pointer, Rect viewport) =>
        new(pointer.X - viewport.Left + (_marqueeScroller?.HorizontalOffset ?? 0),
            pointer.Y - viewport.Top + MarqueeVerticalOffset());

    private double MarqueeVerticalOffset()
    {
        if (ReferenceEquals(_marqueeOwner, ObjectsIcons))
            return _marqueePanel?.VerticalOffset ?? 0;

        // DataGrid may report logical (item) rather than pixel scroll units.
        // Use a realized row as the pixel-coordinate anchor, regardless of scroll unit.
        if (FindMarqueeChild<DataGridRow>(ObjectsGrid) is { } row)
        {
            var index = ObjectsGrid.ItemContainerGenerator.IndexFromContainer(row);
            var viewport = MarqueeViewport();
            if (index >= 0 && !viewport.IsEmpty)
            {
                var top = row.TransformToVisual(MarqueeLayer).Transform(new Point(0, 0)).Y;
                return index * Math.Max(1, ObjectsGrid.RowHeight) - (top - viewport.Top);
            }
        }
        return (_marqueeScroller?.VerticalOffset ?? 0) * Math.Max(1, ObjectsGrid.RowHeight);
    }

    private void CancelMarquee()
    {
        var active = _marqueeActive;
        var original = _marqueeSource;
        ClearMarquee();
        if (active && ReferenceEquals(original, Explorer))
            RestoreListedSelection();
    }

    private void ClearMarquee()
    {
        _marqueeTimer.Stop();
        MarqueeBox.Visibility = Visibility.Collapsed;
        var owner = _marqueeOwner;
        _marqueeCandidate = false;
        _marqueeActive = false;
        _marqueeOwner = null;
        _marqueeSource = null;
        _marqueeScroller = null;
        _marqueePanel = null;
        _marqueeRows = [];
        _marqueeBefore = [];
        _marqueeCurrent.Clear();
        if (owner is not null && Mouse.Captured == owner)
            owner.ReleaseMouseCapture();
    }

    private void MarqueeRowsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (_marqueeCandidate)
            CancelMarquee();
    }

    private static T? FindMarqueeChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T target)
                return target;
            if (FindMarqueeChild<T>(child) is { } descendant)
                return descendant;
        }
        return null;
    }
}
