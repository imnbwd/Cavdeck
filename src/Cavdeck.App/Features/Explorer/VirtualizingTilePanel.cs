using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace Cavdeck.App.Features.Explorer;

// A fixed-size wrap layout that creates containers only for visible rows.
public sealed class VirtualizingTilePanel : VirtualizingPanel, IScrollInfo
{
    public static readonly DependencyProperty ItemWidthProperty = DependencyProperty.Register(
        nameof(ItemWidth), typeof(double), typeof(VirtualizingTilePanel),
        new FrameworkPropertyMetadata(164d, FrameworkPropertyMetadataOptions.AffectsMeasure));
    public static readonly DependencyProperty ItemHeightProperty = DependencyProperty.Register(
        nameof(ItemHeight), typeof(double), typeof(VirtualizingTilePanel),
        new FrameworkPropertyMetadata(136d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    private const double TileMargin = MarqueeSelectionGeometry.TileGap;
    private Size _extent;
    private Size _viewport;
    private double _verticalOffset;
    private int _columns = 1;

    public double ItemWidth { get => (double)GetValue(ItemWidthProperty); set => SetValue(ItemWidthProperty, value); }
    public double ItemHeight { get => (double)GetValue(ItemHeightProperty); set => SetValue(ItemHeightProperty, value); }

    // Expose the actual layout column count for virtualization-independent hit testing.
    public int Columns => _columns;

    private double CellWidth => Math.Max(1, ItemWidth + TileMargin);
    private double CellHeight => Math.Max(1, ItemHeight + TileMargin);
    private int Count => ItemsControl.GetItemsOwner(this)?.Items.Count ?? 0;
    private int IndexFromContainer(DependencyObject child) =>
        ItemsControl.GetItemsOwner(this)?.ItemContainerGenerator.IndexFromContainer(child) ?? -1;

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width) ? Math.Max(CellWidth, ActualWidth) : availableSize.Width;
        var height = double.IsInfinity(availableSize.Height) ? 0 : availableSize.Height;
        _columns = Math.Max(1, (int)Math.Floor(Math.Max(CellWidth, width) / CellWidth));
        _viewport = new Size(width, height);
        _extent = new Size(width, Math.Ceiling((double)Count / _columns) * CellHeight);
        _verticalOffset = Math.Clamp(_verticalOffset, 0, Math.Max(0, _extent.Height - _viewport.Height));
        ScrollOwner?.InvalidateScrollInfo();

        var firstRow = Math.Max(0, (int)Math.Floor(_verticalOffset / CellHeight) - 1);
        var lastRow = (int)Math.Ceiling((_verticalOffset + height) / CellHeight) + 1;
        var first = Math.Min(Count, firstRow * _columns);
        var last = Math.Min(Count, (lastRow + 1) * _columns);
        var generator = ItemContainerGenerator ?? ItemsControl.GetItemsOwner(this)?.ItemContainerGenerator;
        if (generator is null)
            return _viewport;
        for (var child = InternalChildren.Count - 1; child >= 0; child--)
        {
            var index = IndexFromContainer(InternalChildren[child]);
            if (index >= first && index < last)
                continue;
            generator.Remove(new GeneratorPosition(child, 0), 1);
            RemoveInternalChildRange(child, 1);
        }

        for (var index = first; index < last; index++)
        {
            var position = generator.GeneratorPositionFromIndex(index);
            using (generator.StartAt(position, GeneratorDirection.Forward, true))
            {
                if (generator.GenerateNext(out var isNew) is not UIElement child)
                    continue;
                if (isNew)
                {
                    var childIndex = position.Offset == 0 ? position.Index : position.Index + 1;
                    InsertInternalChild(Math.Clamp(childIndex, 0, InternalChildren.Count), child);
                    generator.PrepareItemContainer(child);
                }
                child.Measure(new Size(CellWidth, CellHeight));
            }
        }
        return _viewport;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        foreach (UIElement child in InternalChildren)
        {
            var index = IndexFromContainer(child);
            if (index < 0)
                continue;
            child.Arrange(new Rect((index % _columns) * CellWidth,
                (index / _columns) * CellHeight - _verticalOffset, CellWidth, CellHeight));
        }
        return finalSize;
    }

    protected override void BringIndexIntoView(int index) =>
        SetVerticalOffset((index / _columns) * CellHeight);

    protected override void OnItemsChanged(object sender, ItemsChangedEventArgs args)
    {
        base.OnItemsChanged(sender, args);
        InvalidateMeasure();
    }

    public bool CanHorizontallyScroll { get; set; }
    public bool CanVerticallyScroll { get; set; } = true;
    public double ExtentWidth => _extent.Width;
    public double ExtentHeight => _extent.Height;
    public double ViewportWidth => _viewport.Width;
    public double ViewportHeight => _viewport.Height;
    public double HorizontalOffset => 0;
    public double VerticalOffset => _verticalOffset;
    public ScrollViewer? ScrollOwner { get; set; }
    public void LineDown() => SetVerticalOffset(_verticalOffset + CellHeight);
    public void LineUp() => SetVerticalOffset(_verticalOffset - CellHeight);
    public void PageDown() => SetVerticalOffset(_verticalOffset + _viewport.Height);
    public void PageUp() => SetVerticalOffset(_verticalOffset - _viewport.Height);
    public void MouseWheelDown() => SetVerticalOffset(_verticalOffset + CellHeight * 3);
    public void MouseWheelUp() => SetVerticalOffset(_verticalOffset - CellHeight * 3);
    public void LineLeft() { }
    public void LineRight() { }
    public void PageLeft() { }
    public void PageRight() { }
    public void MouseWheelLeft() { }
    public void MouseWheelRight() { }
    public void SetHorizontalOffset(double offset) { }
    public void SetVerticalOffset(double offset)
    {
        var next = Math.Clamp(offset, 0, Math.Max(0, _extent.Height - _viewport.Height));
        if (Math.Abs(next - _verticalOffset) < 0.1)
            return;
        _verticalOffset = next;
        InvalidateMeasure();
        ScrollOwner?.InvalidateScrollInfo();
    }

    public Rect MakeVisible(Visual visual, Rect rectangle)
    {
        var container = visual as DependencyObject;
        while (container is not null && IndexFromContainer(container) < 0)
            container = VisualTreeHelper.GetParent(container);
        if (container is null)
            return Rect.Empty;
        var index = IndexFromContainer(container);
        var top = (index / _columns) * CellHeight;
        if (top < _verticalOffset)
            SetVerticalOffset(top);
        else if (top + CellHeight > _verticalOffset + _viewport.Height)
            SetVerticalOffset(top + CellHeight - _viewport.Height);
        return new Rect(0, top - _verticalOffset, CellWidth, CellHeight);
    }
}
