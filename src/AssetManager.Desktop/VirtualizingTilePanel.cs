using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace AssetManager.Desktop;

public sealed class VirtualizingTilePanel : VirtualizingPanel, IScrollInfo
{
    public static readonly DependencyProperty ItemWidthProperty =
        DependencyProperty.Register(
            nameof(ItemWidth),
            typeof(double),
            typeof(VirtualizingTilePanel),
            new FrameworkPropertyMetadata(222d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty ItemHeightProperty =
        DependencyProperty.Register(
            nameof(ItemHeight),
            typeof(double),
            typeof(VirtualizingTilePanel),
            new FrameworkPropertyMetadata(298d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty OverscanRowsProperty =
        DependencyProperty.Register(
            nameof(OverscanRows),
            typeof(int),
            typeof(VirtualizingTilePanel),
            new FrameworkPropertyMetadata(1, FrameworkPropertyMetadataOptions.AffectsMeasure));

    private int _itemsPerRow = 1;
    private double _extentWidth;
    private double _extentHeight;
    private double _viewportWidth;
    private double _viewportHeight;
    private double _horizontalOffset;
    private double _verticalOffset;

    public double ItemWidth
    {
        get => (double)GetValue(ItemWidthProperty);
        set => SetValue(ItemWidthProperty, value);
    }

    public double ItemHeight
    {
        get => (double)GetValue(ItemHeightProperty);
        set => SetValue(ItemHeightProperty, value);
    }

    public int OverscanRows
    {
        get => (int)GetValue(OverscanRowsProperty);
        set => SetValue(OverscanRowsProperty, value);
    }

    public bool CanHorizontallyScroll { get; set; }

    public bool CanVerticallyScroll { get; set; } = true;

    public double ExtentWidth => _extentWidth;

    public double ExtentHeight => _extentHeight;

    public double ViewportWidth => _viewportWidth;

    public double ViewportHeight => _viewportHeight;

    public double HorizontalOffset => _horizontalOffset;

    public double VerticalOffset => _verticalOffset;

    public ScrollViewer? ScrollOwner { get; set; }

    protected override Size MeasureOverride(Size availableSize)
    {
        var itemsControl = ItemsControl.GetItemsOwner(this);
        if (itemsControl is null)
        {
            return availableSize;
        }

        var itemCount = itemsControl.Items.Count;
        var itemWidth = Math.Max(1d, ItemWidth);
        var itemHeight = Math.Max(1d, ItemHeight);
        var availableWidth = double.IsInfinity(availableSize.Width) || availableSize.Width <= 0
            ? itemWidth
            : availableSize.Width;

        _itemsPerRow = Math.Max(1, (int)Math.Floor(availableWidth / itemWidth));

        if (itemCount == 0)
        {
            CleanupAllChildren();
            UpdateScrollInfo(availableWidth, availableSize.Height, 0, 0);
            return availableSize;
        }

        var totalRows = (int)Math.Ceiling((double)itemCount / _itemsPerRow);
        var viewportHeight = double.IsInfinity(availableSize.Height) || availableSize.Height <= 0
            ? totalRows * itemHeight
            : availableSize.Height;

        UpdateScrollInfo(
            availableWidth,
            viewportHeight,
            _itemsPerRow * itemWidth,
            totalRows * itemHeight);

        var firstVisibleRow = (int)Math.Floor(_verticalOffset / itemHeight);
        var visibleRowCount = Math.Max(1, (int)Math.Ceiling(_viewportHeight / itemHeight));
        var overscanRows = Math.Max(0, OverscanRows);
        var startRow = Math.Max(0, firstVisibleRow - overscanRows);
        var endRow = Math.Min(totalRows - 1, firstVisibleRow + visibleRowCount + overscanRows);
        var firstIndex = startRow * _itemsPerRow;
        var lastIndex = Math.Min(itemCount - 1, ((endRow + 1) * _itemsPerRow) - 1);

        RealizeChildren(firstIndex, lastIndex, new Size(itemWidth, itemHeight));
        CleanupChildrenOutsideRange(firstIndex, lastIndex);

        return availableSize;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        for (var childIndex = 0; childIndex < Children.Count; childIndex++)
        {
            var itemIndex = ItemContainerGenerator.IndexFromGeneratorPosition(new GeneratorPosition(childIndex, 0));
            if (itemIndex < 0)
            {
                continue;
            }

            var row = itemIndex / _itemsPerRow;
            var column = itemIndex % _itemsPerRow;
            var bounds = new Rect(
                column * ItemWidth - _horizontalOffset,
                row * ItemHeight - _verticalOffset,
                ItemWidth,
                ItemHeight);
            Children[childIndex].Arrange(bounds);
        }

        return finalSize;
    }

    protected override void OnItemsChanged(object sender, ItemsChangedEventArgs args)
    {
        base.OnItemsChanged(sender, args);
        InvalidateMeasure();
    }

    protected override void BringIndexIntoView(int index)
    {
        if (index < 0)
        {
            return;
        }

        var row = index / _itemsPerRow;
        SetVerticalOffset(row * ItemHeight);
    }

    public void LineUp()
    {
        SetVerticalOffset(_verticalOffset - ItemHeight);
    }

    public void LineDown()
    {
        SetVerticalOffset(_verticalOffset + ItemHeight);
    }

    public void LineLeft()
    {
        SetHorizontalOffset(_horizontalOffset - ItemWidth);
    }

    public void LineRight()
    {
        SetHorizontalOffset(_horizontalOffset + ItemWidth);
    }

    public void MouseWheelUp()
    {
        SetVerticalOffset(_verticalOffset - ItemHeight);
    }

    public void MouseWheelDown()
    {
        SetVerticalOffset(_verticalOffset + ItemHeight);
    }

    public void MouseWheelLeft()
    {
        SetHorizontalOffset(_horizontalOffset - ItemWidth);
    }

    public void MouseWheelRight()
    {
        SetHorizontalOffset(_horizontalOffset + ItemWidth);
    }

    public void PageUp()
    {
        SetVerticalOffset(_verticalOffset - _viewportHeight);
    }

    public void PageDown()
    {
        SetVerticalOffset(_verticalOffset + _viewportHeight);
    }

    public void PageLeft()
    {
        SetHorizontalOffset(_horizontalOffset - _viewportWidth);
    }

    public void PageRight()
    {
        SetHorizontalOffset(_horizontalOffset + _viewportWidth);
    }

    public Rect MakeVisible(Visual visual, Rect rectangle)
    {
        if (visual is not UIElement element)
        {
            return rectangle;
        }

        var childIndex = Children.IndexOf(element);
        if (childIndex < 0)
        {
            return rectangle;
        }

        var itemIndex = ItemContainerGenerator.IndexFromGeneratorPosition(new GeneratorPosition(childIndex, 0));
        if (itemIndex < 0)
        {
            return rectangle;
        }

        var row = itemIndex / _itemsPerRow;
        var top = row * ItemHeight;
        var bottom = top + ItemHeight;

        if (top < _verticalOffset)
        {
            SetVerticalOffset(top);
        }
        else if (bottom > _verticalOffset + _viewportHeight)
        {
            SetVerticalOffset(bottom - _viewportHeight);
        }

        return new Rect(
            (itemIndex % _itemsPerRow) * ItemWidth,
            top - _verticalOffset,
            ItemWidth,
            ItemHeight);
    }

    public void SetHorizontalOffset(double offset)
    {
        var nextOffset = ClampOffset(offset, _extentWidth, _viewportWidth);
        if (DoubleUtil.AreClose(_horizontalOffset, nextOffset))
        {
            return;
        }

        _horizontalOffset = nextOffset;
        ScrollOwner?.InvalidateScrollInfo();
        InvalidateArrange();
    }

    public void SetVerticalOffset(double offset)
    {
        var nextOffset = ClampOffset(offset, _extentHeight, _viewportHeight);
        if (DoubleUtil.AreClose(_verticalOffset, nextOffset))
        {
            return;
        }

        _verticalOffset = nextOffset;
        ScrollOwner?.InvalidateScrollInfo();
        InvalidateMeasure();
    }

    private void RealizeChildren(int firstIndex, int lastIndex, Size itemSize)
    {
        if (firstIndex > lastIndex)
        {
            CleanupAllChildren();
            return;
        }

        var generatorPosition = ItemContainerGenerator.GeneratorPositionFromIndex(firstIndex);
        var childIndex = generatorPosition.Offset == 0
            ? generatorPosition.Index
            : generatorPosition.Index + 1;

        using (ItemContainerGenerator.StartAt(
                   generatorPosition,
                   GeneratorDirection.Forward,
                   true))
        {
            for (var itemIndex = firstIndex; itemIndex <= lastIndex; itemIndex++, childIndex++)
            {
                var child = (UIElement)ItemContainerGenerator.GenerateNext(out var newlyRealized);
                if (newlyRealized)
                {
                    if (childIndex >= Children.Count)
                    {
                        AddInternalChild(child);
                    }
                    else
                    {
                        InsertInternalChild(childIndex, child);
                    }

                    ItemContainerGenerator.PrepareItemContainer(child);
                }

                child.Measure(itemSize);
            }
        }
    }

    private void CleanupChildrenOutsideRange(int firstIndex, int lastIndex)
    {
        for (var childIndex = Children.Count - 1; childIndex >= 0; childIndex--)
        {
            var position = new GeneratorPosition(childIndex, 0);
            var itemIndex = ItemContainerGenerator.IndexFromGeneratorPosition(position);
            if (itemIndex >= firstIndex && itemIndex <= lastIndex)
            {
                continue;
            }

            ItemContainerGenerator.Remove(position, 1);
            RemoveInternalChildRange(childIndex, 1);
        }
    }

    private void CleanupAllChildren()
    {
        for (var childIndex = Children.Count - 1; childIndex >= 0; childIndex--)
        {
            ItemContainerGenerator.Remove(new GeneratorPosition(childIndex, 0), 1);
            RemoveInternalChildRange(childIndex, 1);
        }
    }

    private void UpdateScrollInfo(
        double viewportWidth,
        double viewportHeight,
        double extentWidth,
        double extentHeight)
    {
        _viewportWidth = double.IsNaN(viewportWidth) ? 0 : viewportWidth;
        _viewportHeight = double.IsNaN(viewportHeight) || double.IsInfinity(viewportHeight) ? 0 : viewportHeight;
        _extentWidth = double.IsNaN(extentWidth) ? 0 : extentWidth;
        _extentHeight = double.IsNaN(extentHeight) ? 0 : extentHeight;
        _horizontalOffset = ClampOffset(_horizontalOffset, _extentWidth, _viewportWidth);
        _verticalOffset = ClampOffset(_verticalOffset, _extentHeight, _viewportHeight);
        ScrollOwner?.InvalidateScrollInfo();
    }

    private static double ClampOffset(double offset, double extent, double viewport)
    {
        if (viewport >= extent)
        {
            return 0;
        }

        return Math.Max(0, Math.Min(offset, extent - viewport));
    }

    private static class DoubleUtil
    {
        public static bool AreClose(double first, double second)
        {
            if (first == second)
            {
                return true;
            }

            var epsilon = (Math.Abs(first) + Math.Abs(second) + 10.0) * 2.2204460492503131e-16;
            var delta = first - second;
            return -epsilon < delta && epsilon > delta;
        }
    }
}
